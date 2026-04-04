// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class MenuExecuteMessage   { public MenuExecutePayload payload; }
    [Serializable] internal class MenuExecutePayload   { public string menuPath = ""; }

    [Serializable]
    internal class MenuExecuteResultPayload
    {
        public string menuPath;
        public bool   executed;
    }

    [Serializable]
    internal class MenuItemInfoPayload
    {
        public string menuPath;
    }

    [Serializable]
    internal class MenuListResultPayload
    {
        public List<MenuItemInfoPayload> items = new List<MenuItemInfoPayload>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotMenuService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotMenuService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("menu.execute", HandleExecuteAsync);
            _bridge.Router.Register("menu.list",    HandleListAsync);
        }

        // ── menu.execute ────────────────────────────────────────────────────────

        private async Task HandleExecuteAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MenuExecuteMessage>(json);
            var p   = msg?.payload ?? new MenuExecutePayload();

            if (string.IsNullOrEmpty(p.menuPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "menuPath is required.", token, "menu.execute");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    bool ok = EditorApplication.ExecuteMenuItem(p.menuPath);
                    tcs.SetResult(ok);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                bool executed = await tcs.Task;
                var payload = new MenuExecuteResultPayload
                {
                    menuPath = p.menuPath,
                    executed = executed,
                };
                await _bridge.SendResultAsync(id, "menu.execute", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MENU_EXECUTE_FAILED", ex.Message, token, "menu.execute");
            }
        }

        // ── menu.list ───────────────────────────────────────────────────────────

        private async Task HandleListAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<MenuListResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = new MenuListResultPayload();
                    var collected = new HashSet<string>();

                    // Scan all loaded assemblies for [MenuItem] attributes
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            foreach (var type in assembly.GetTypes())
                            {
                                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                                {
                                    var attrs = method.GetCustomAttributes(typeof(MenuItem), false);
                                    foreach (MenuItem attr in attrs)
                                    {
                                        if (!string.IsNullOrEmpty(attr.menuItem) && collected.Add(attr.menuItem))
                                        {
                                            // Skip validation methods (they have same path but bool return)
                                            if (attr.validate) continue;

                                            result.items.Add(new MenuItemInfoPayload
                                            {
                                                menuPath = attr.menuItem,
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Skip assemblies that fail reflection
                        }
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "menu.list", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MENU_LIST_FAILED", ex.Message, token, "menu.list");
            }
        }
    }
}

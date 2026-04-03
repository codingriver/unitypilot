// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class SelectionSetMessage  { public SelectionSetPayload payload; }
    [Serializable]
    internal class SelectionSetPayload
    {
        public List<int>    gameObjectIds = new();
        public List<string> assetPaths    = new();
    }

    [Serializable]
    internal class SelectionResultPayload
    {
        public List<int>    selectedGameObjectIds = new();
        public List<string> selectedAssetPaths    = new();
        public int          activeGameObjectId;
        public int          selectionCount;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotSelectionService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotSelectionService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("selection.get",   HandleGetAsync);
            _bridge.Router.Register("selection.set",   HandleSetAsync);
            _bridge.Router.Register("selection.clear",  HandleClearAsync);
        }

        // ── selection.get ───────────────────────────────────────────────────────

        private async Task HandleGetAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<SelectionResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try { tcs.SetResult(BuildSelectionResult()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "selection.get", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SELECTION_GET_FAILED", ex.Message, token, "selection.get");
            }
        }

        // ── selection.set ───────────────────────────────────────────────────────

        private async Task HandleSetAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SelectionSetMessage>(json);
            var p   = msg?.payload ?? new SelectionSetPayload();

            var tcs = new TaskCompletionSource<SelectionResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var objects = new List<UnityEngine.Object>();

                    // Resolve game object IDs
                    if (p.gameObjectIds != null)
                    {
                        foreach (int instanceId in p.gameObjectIds)
                        {
                            var obj = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                            if (obj != null) objects.Add(obj);
                        }
                    }

                    // Resolve asset paths
                    if (p.assetPaths != null)
                    {
                        foreach (string path in p.assetPaths)
                        {
                            if (string.IsNullOrEmpty(path)) continue;
                            var asset = AssetDatabase.LoadMainAssetAtPath(path);
                            if (asset != null) objects.Add(asset);
                        }
                    }

                    Selection.objects = objects.ToArray();
                    tcs.SetResult(BuildSelectionResult());
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "selection.set", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SELECTION_SET_FAILED", ex.Message, token, "selection.set");
            }
        }

        // ── selection.clear ─────────────────────────────────────────────────────

        private async Task HandleClearAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    Selection.objects = new UnityEngine.Object[0];
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "selection.clear", new GenericOkPayload(), token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SELECTION_CLEAR_FAILED", ex.Message, token, "selection.clear");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static SelectionResultPayload BuildSelectionResult()
        {
            var result = new SelectionResultPayload();

            // Game objects in scene
            foreach (var go in Selection.gameObjects)
            {
                result.selectedGameObjectIds.Add(go.GetInstanceID());
            }

            // All selected objects — check for assets
            foreach (var obj in Selection.objects)
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    result.selectedAssetPaths.Add(assetPath);
                }
            }

            result.activeGameObjectId = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetInstanceID()
                : 0;
            result.selectionCount = Selection.objects.Length;

            return result;
        }
    }
}

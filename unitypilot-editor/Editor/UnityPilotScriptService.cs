// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class ScriptReadMessage    { public ScriptReadPayload payload; }
    [Serializable] internal class ScriptReadPayload    { public string scriptPath = ""; }

    [Serializable] internal class ScriptCreateMessage  { public ScriptCreatePayload payload; }
    [Serializable] internal class ScriptCreatePayload  { public string scriptPath = ""; public string content = ""; }

    [Serializable] internal class ScriptUpdateMessage  { public ScriptUpdatePayload payload; }
    [Serializable] internal class ScriptUpdatePayload  { public string scriptPath = ""; public string content = ""; }

    [Serializable] internal class ScriptDeleteMessage  { public ScriptDeletePayload payload; }
    [Serializable] internal class ScriptDeletePayload  { public string scriptPath = ""; }

    [Serializable]
    internal class ScriptContentPayload
    {
        public string scriptPath;
        public string content;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotScriptService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotScriptService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("script.read",   HandleReadAsync);
            _bridge.Router.Register("script.create",  HandleCreateAsync);
            _bridge.Router.Register("script.update",  HandleUpdateAsync);
            _bridge.Router.Register("script.delete",  HandleDeleteAsync);
        }

        // ── Validation helpers ──────────────────────────────────────────────────

        private static bool ValidateScriptPath(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "scriptPath is required.";
                return false;
            }
            if (!path.StartsWith("Assets/") && !path.StartsWith("Assets\\"))
            {
                error = "scriptPath must start with 'Assets/'.";
                return false;
            }
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = "scriptPath must end with '.cs'.";
                return false;
            }
            return true;
        }

        private static string ToAbsolutePath(string assetPath)
        {
            // Unity project root is parent of Assets/
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot, assetPath);
        }

        // ── script.read ─────────────────────────────────────────────────────────

        private async Task HandleReadAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScriptReadMessage>(json);
            var p   = msg?.payload ?? new ScriptReadPayload();

            if (!ValidateScriptPath(p.scriptPath, out string error))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", error, token, "script.read");
                return;
            }

            try
            {
                string absPath = ToAbsolutePath(p.scriptPath);
                if (!File.Exists(absPath))
                {
                    await _bridge.SendErrorAsync(id, "FILE_NOT_FOUND", $"Script not found: {p.scriptPath}", token, "script.read");
                    return;
                }

                string content = File.ReadAllText(absPath, System.Text.Encoding.UTF8);
                var payload = new ScriptContentPayload
                {
                    scriptPath = p.scriptPath,
                    content    = content,
                };
                await _bridge.SendResultAsync(id, "script.read", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCRIPT_READ_FAILED", ex.Message, token, "script.read");
            }
        }

        // ── script.create ───────────────────────────────────────────────────────

        private async Task HandleCreateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScriptCreateMessage>(json);
            var p   = msg?.payload ?? new ScriptCreatePayload();

            if (!ValidateScriptPath(p.scriptPath, out string error))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", error, token, "script.create");
                return;
            }

            try
            {
                string absPath = ToAbsolutePath(p.scriptPath);
                if (File.Exists(absPath))
                {
                    await _bridge.SendErrorAsync(id, "FILE_ALREADY_EXISTS", $"Script already exists: {p.scriptPath}", token, "script.create");
                    return;
                }

                // Ensure directory exists
                string dir = Path.GetDirectoryName(absPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string content = string.IsNullOrEmpty(p.content)
                    ? "// Auto-generated script placeholder\n"
                    : p.content;

                File.WriteAllText(absPath, content, System.Text.Encoding.UTF8);

                // Import into AssetDatabase on main thread
                var tcs = new TaskCompletionSource<bool>();
                _bridge.MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        AssetDatabase.ImportAsset(p.scriptPath, ImportAssetOptions.ForceUpdate);
                        tcs.SetResult(true);
                    }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                await tcs.Task;

                var payload = new ScriptContentPayload
                {
                    scriptPath = p.scriptPath,
                    content    = content,
                };
                await _bridge.SendResultAsync(id, "script.create", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCRIPT_CREATE_FAILED", ex.Message, token, "script.create");
            }
        }

        // ── script.update ───────────────────────────────────────────────────────

        private async Task HandleUpdateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScriptUpdateMessage>(json);
            var p   = msg?.payload ?? new ScriptUpdatePayload();

            if (!ValidateScriptPath(p.scriptPath, out string error))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", error, token, "script.update");
                return;
            }

            try
            {
                string absPath = ToAbsolutePath(p.scriptPath);
                if (!File.Exists(absPath))
                {
                    await _bridge.SendErrorAsync(id, "FILE_NOT_FOUND", $"Script not found: {p.scriptPath}", token, "script.update");
                    return;
                }

                File.WriteAllText(absPath, p.content ?? "", System.Text.Encoding.UTF8);

                // Re-import on main thread
                var tcs = new TaskCompletionSource<bool>();
                _bridge.MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        AssetDatabase.ImportAsset(p.scriptPath, ImportAssetOptions.ForceUpdate);
                        tcs.SetResult(true);
                    }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                await tcs.Task;

                var payload = new ScriptContentPayload
                {
                    scriptPath = p.scriptPath,
                    content    = p.content ?? "",
                };
                await _bridge.SendResultAsync(id, "script.update", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCRIPT_UPDATE_FAILED", ex.Message, token, "script.update");
            }
        }

        // ── script.delete ───────────────────────────────────────────────────────

        private async Task HandleDeleteAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScriptDeleteMessage>(json);
            var p   = msg?.payload ?? new ScriptDeletePayload();

            if (!ValidateScriptPath(p.scriptPath, out string error))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", error, token, "script.delete");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    bool ok = AssetDatabase.DeleteAsset(p.scriptPath);
                    tcs.SetResult(ok);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                bool deleted = await tcs.Task;
                if (!deleted)
                {
                    await _bridge.SendErrorAsync(id, "SCRIPT_DELETE_FAILED", $"Failed to delete: {p.scriptPath}", token, "script.delete");
                    return;
                }
                await _bridge.SendResultAsync(id, "script.delete", new GenericOkPayload(), token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCRIPT_DELETE_FAILED", ex.Message, token, "script.delete");
            }
        }
    }
}

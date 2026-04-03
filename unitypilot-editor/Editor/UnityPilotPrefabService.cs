// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace codingriver.unity.pilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class PrefabCreateMessage       { public PrefabCreatePayload payload; }
    [Serializable] internal class PrefabCreatePayload       { public int sourceGameObjectId; public string prefabPath = ""; }

    [Serializable] internal class PrefabInstantiateMessage   { public PrefabInstantiatePayload payload; }
    [Serializable] internal class PrefabInstantiatePayload   { public string prefabPath = ""; public int parentId; }

    [Serializable] internal class PrefabPathMessage          { public PrefabPathPayload payload; }
    [Serializable] internal class PrefabPathPayload          { public string prefabPath = ""; }

    [Serializable]
    internal class PrefabResultPayload
    {
        public int    instanceId;
        public string prefabPath;
        public string name;
    }

    [Serializable]
    internal class PrefabStatusPayload
    {
        public bool isInPrefabMode;
        public string currentPrefabPath;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotPrefabService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotPrefabService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("prefab.create",      HandleCreateAsync);
            _bridge.Router.Register("prefab.instantiate",  HandleInstantiateAsync);
            _bridge.Router.Register("prefab.open",         HandleOpenAsync);
            _bridge.Router.Register("prefab.close",        HandleCloseAsync);
            _bridge.Router.Register("prefab.save",         HandleSaveAsync);
        }

        // ── prefab.create ───────────────────────────────────────────────────────

        private async Task HandleCreateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PrefabCreateMessage>(json);
            var p   = msg?.payload ?? new PrefabCreatePayload();

            if (p.sourceGameObjectId == 0 || string.IsNullOrEmpty(p.prefabPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "sourceGameObjectId and prefabPath are required.", token, "prefab.create");
                return;
            }

            var tcs = new TaskCompletionSource<PrefabResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var go = EditorUtility.InstanceIDToObject(p.sourceGameObjectId) as GameObject;
                    if (go == null)
                    {
                        tcs.SetException(new Exception($"GameObject not found: {p.sourceGameObjectId}"));
                        return;
                    }

                    // Ensure directory exists
                    string dir = System.IO.Path.GetDirectoryName(p.prefabPath);
                    if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                    {
                        // Create intermediate folders
                        EnsureFolder(dir);
                    }

                    bool success;
                    var prefab = PrefabUtility.SaveAsPrefabAsset(go, p.prefabPath, out success);
                    if (!success || prefab == null)
                    {
                        tcs.SetException(new Exception($"Failed to create prefab at: {p.prefabPath}"));
                        return;
                    }

                    AssetDatabase.SaveAssets();
                    tcs.SetResult(new PrefabResultPayload
                    {
                        instanceId = prefab.GetInstanceID(),
                        prefabPath = p.prefabPath,
                        name       = prefab.name,
                    });
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "prefab.create", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "PREFAB_CREATE_FAILED", ex.Message, token, "prefab.create");
            }
        }

        // ── prefab.instantiate ──────────────────────────────────────────────────

        private async Task HandleInstantiateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PrefabInstantiateMessage>(json);
            var p   = msg?.payload ?? new PrefabInstantiatePayload();

            if (string.IsNullOrEmpty(p.prefabPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "prefabPath is required.", token, "prefab.instantiate");
                return;
            }

            var tcs = new TaskCompletionSource<PrefabResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefabPath);
                    if (prefab == null)
                    {
                        tcs.SetException(new Exception($"Prefab not found at: {p.prefabPath}"));
                        return;
                    }

                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    if (instance == null)
                    {
                        tcs.SetException(new Exception($"Failed to instantiate prefab: {p.prefabPath}"));
                        return;
                    }

                    // Set parent if specified
                    if (p.parentId != 0)
                    {
                        var parent = EditorUtility.InstanceIDToObject(p.parentId) as GameObject;
                        if (parent != null)
                            instance.transform.SetParent(parent.transform, true);
                    }

                    Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");

                    tcs.SetResult(new PrefabResultPayload
                    {
                        instanceId = instance.GetInstanceID(),
                        prefabPath = p.prefabPath,
                        name       = instance.name,
                    });
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "prefab.instantiate", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "PREFAB_INSTANTIATE_FAILED", ex.Message, token, "prefab.instantiate");
            }
        }

        // ── prefab.open ─────────────────────────────────────────────────────────

        private async Task HandleOpenAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PrefabPathMessage>(json);
            var p   = msg?.payload ?? new PrefabPathPayload();

            if (string.IsNullOrEmpty(p.prefabPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "prefabPath is required.", token, "prefab.open");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefabPath);
                    if (prefab == null)
                    {
                        tcs.SetException(new Exception($"Prefab not found at: {p.prefabPath}"));
                        return;
                    }

                    AssetDatabase.OpenAsset(prefab);
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                var payload = new PrefabStatusPayload
                {
                    isInPrefabMode   = true,
                    currentPrefabPath = p.prefabPath,
                };
                await _bridge.SendResultAsync(id, "prefab.open", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "PREFAB_OPEN_FAILED", ex.Message, token, "prefab.open");
            }
        }

        // ── prefab.close ────────────────────────────────────────────────────────

        private async Task HandleCloseAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var stage = PrefabStageUtility.GetCurrentPrefabStage();
                    if (stage == null)
                    {
                        tcs.SetResult(false); // Not in prefab mode
                        return;
                    }

                    // Go back to main stage
                    StageUtility.GoToMainStage();
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                var payload = new PrefabStatusPayload
                {
                    isInPrefabMode    = false,
                    currentPrefabPath = "",
                };
                await _bridge.SendResultAsync(id, "prefab.close", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "PREFAB_CLOSE_FAILED", ex.Message, token, "prefab.close");
            }
        }

        // ── prefab.save ─────────────────────────────────────────────────────────

        private async Task HandleSaveAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var stage = PrefabStageUtility.GetCurrentPrefabStage();
                    if (stage == null)
                    {
                        tcs.SetException(new Exception("Not in Prefab editing mode."));
                        return;
                    }

                    // Save the prefab stage
                    // Use PrefabUtility.SaveAsPrefabAsset on the root object
                    var root = stage.prefabContentsRoot;
                    if (root == null)
                    {
                        tcs.SetException(new Exception("Prefab stage has no root object."));
                        return;
                    }

                    bool success;
                    PrefabUtility.SaveAsPrefabAsset(root, stage.assetPath, out success);
                    if (!success)
                    {
                        tcs.SetException(new Exception("Failed to save prefab."));
                        return;
                    }

                    AssetDatabase.SaveAssets();
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "prefab.save", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "PREFAB_SAVE_FAILED", ex.Message, token, "prefab.save");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static void EnsureFolder(string folderPath)
        {
            // Recursively create folders under Assets/
            if (AssetDatabase.IsValidFolder(folderPath)) return;

            string parent = System.IO.Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            string name   = System.IO.Path.GetFileName(folderPath);

            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, name);
        }
    }
}

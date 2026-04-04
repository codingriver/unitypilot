// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace codingriver.unity.pilot
{
    // ── M09 Scene DTOs ────────────────────────────────────────────────────────

    [Serializable]
    internal class SceneCreateMessage
    {
        public string id;
        public string type;
        public string name;
        public SceneCreatePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class SceneCreatePayload
    {
        public string sceneName = "";
    }

    [Serializable]
    internal class SceneOpenMessage
    {
        public string id;
        public string type;
        public string name;
        public SceneOpenPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class SceneOpenPayload
    {
        public string scenePath = "";
        public string mode = "single";
    }

    [Serializable]
    internal class SceneSaveMessage
    {
        public string id;
        public string type;
        public string name;
        public SceneSavePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class SceneSavePayload
    {
        public string scenePath = "";
    }

    [Serializable]
    internal class SceneLoadMessage
    {
        public string id;
        public string type;
        public string name;
        public SceneLoadPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class SceneLoadPayload
    {
        public string scenePath = "";
        public string mode = "additive";
    }

    [Serializable]
    internal class SceneSetActiveMessage
    {
        public string id;
        public string type;
        public string name;
        public SceneSetActivePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class SceneSetActivePayload
    {
        public string scenePath = "";
    }

    [Serializable]
    internal class SceneInfoPayload
    {
        public string scenePath;
        public string sceneName;
        public int buildIndex;
        public bool isLoaded;
        public bool isDirty;
        public int rootCount;
        public bool isActive;
    }

    [Serializable]
    internal class SceneUnloadMessage
    {
        public string id;
        public string type;
        public string name;
        public SceneUnloadPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class SceneUnloadPayload
    {
        public string scenePath = "";
        public int removeScene; // 0 = false (keep), 1 = true (remove from hierarchy)
    }

    [Serializable]
    internal class SceneListResultPayload
    {
        public List<SceneInfoPayload> scenes = new();
    }

    [Serializable]
    internal class SceneEnsureTestMessage
    {
        public string id;
        public string type;
        public string name;
        public SceneEnsureTestPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class SceneEnsureTestPayload
    {
        /// <summary>Short name (e.g. unitypilot-test) or Assets/... path fragment; default unitypilot-test.</summary>
        public string sceneName = "";
        /// <summary>If set, takes precedence (must be under Assets/, .unity optional).</summary>
        public string scenePath = "";
    }

    [Serializable]
    internal class SceneEnsureTestResultPayload
    {
        /// <summary>opened | created</summary>
        public string ensureAction = "";
        public SceneInfoPayload scene;
    }

    // ── M09 Scene Service ─────────────────────────────────────────────────────

    internal sealed class UnityPilotSceneService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotSceneService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("scene.create",    HandleSceneCreateAsync);
            _bridge.Router.Register("scene.open",      HandleSceneOpenAsync);
            _bridge.Router.Register("scene.save",      HandleSceneSaveAsync);
            _bridge.Router.Register("scene.load",      HandleSceneLoadAsync);
            _bridge.Router.Register("scene.setActive", HandleSceneSetActiveAsync);
            _bridge.Router.Register("scene.list",      HandleSceneListAsync);
            _bridge.Router.Register("scene.unload",    HandleSceneUnloadAsync);
            _bridge.Router.Register("scene.ensureTest", HandleSceneEnsureTestAsync);
        }

        // ── scene.ensureTest — empty test scene: open if asset exists, else create + save ──

        private async Task HandleSceneEnsureTestAsync(string id, string json, CancellationToken token)
        {
            var msg   = JsonUtility.FromJson<SceneEnsureTestMessage>(json);
            var payload = msg?.payload ?? new SceneEnsureTestPayload();

            var tcs = new TaskCompletionSource<SceneEnsureTestResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var path = ResolveEnsureTestScenePath(payload);
                    SceneEnsureTestResultPayload result;

                    if (System.IO.File.Exists(path))
                    {
                        var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                        result = new SceneEnsureTestResultPayload
                        {
                            ensureAction = "opened",
                            scene        = BuildSceneInfo(scene),
                        };
                    }
                    else
                    {
                        var dir = System.IO.Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                            System.IO.Directory.CreateDirectory(dir);

                        var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                        if (!EditorSceneManager.SaveScene(newScene, path))
                        {
                            tcs.TrySetException(new System.Exception($"保存空测试场景失败：{path}"));
                            return;
                        }

                        AssetDatabase.Refresh();
                        var active = SceneManager.GetActiveScene();
                        result = new SceneEnsureTestResultPayload
                        {
                            ensureAction = "created",
                            scene        = BuildSceneInfo(active),
                        };
                    }

                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "scene.ensureTest", result, token);
                _bridge.AddLog("info", $"[Scene] ensureTest: {result.ensureAction} → {result.scene.scenePath}");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENE_ENSURE_TEST_FAILED", ex.Message, token, "scene.ensureTest");
            }
        }

        private static string ResolveEnsureTestScenePath(SceneEnsureTestPayload payload)
        {
            if (!string.IsNullOrEmpty(payload.scenePath))
            {
                var p = payload.scenePath.Trim().Replace('\\', '/');
                if (!p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    p += ".unity";
                if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    p = "Assets/" + p.TrimStart('/');
                return p;
            }

            var raw = string.IsNullOrEmpty(payload.sceneName) ? "unitypilot-test" : payload.sceneName.Trim();
            raw = raw.Replace('\\', '/');
            if (raw.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return raw.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".unity";
            if (raw.Contains("/"))
            {
                var p = "Assets/" + raw.TrimStart('/');
                return p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? p : p + ".unity";
            }

            return "Assets/" + raw + ".unity";
        }

        // ── scene.create ──────────────────────────────────────────────────────

        private async Task HandleSceneCreateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SceneCreateMessage>(json);
            var sceneName = msg?.payload?.sceneName ?? "";

            var tcs = new TaskCompletionSource<SceneInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                    if (!string.IsNullOrEmpty(sceneName))
                    {
                        // Save to create the asset with the given name
                        var path = "Assets/" + sceneName;
                        if (!path.EndsWith(".unity"))
                            path += ".unity";
                        EditorSceneManager.SaveScene(newScene, path);
                    }
                    tcs.TrySetResult(BuildSceneInfo(newScene));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "scene.create", result, token);
                _bridge.AddLog("info", $"[Scene] 新建场景: {result.sceneName}");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENE_CREATE_FAILED", ex.Message, token, "scene.create");
            }
        }

        // ── scene.open ────────────────────────────────────────────────────────

        private async Task HandleSceneOpenAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SceneOpenMessage>(json);
            var scenePath = msg?.payload?.scenePath ?? "";
            var modeStr = msg?.payload?.mode ?? "single";

            if (string.IsNullOrEmpty(scenePath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "scenePath 不能为空", token, "scene.open");
                return;
            }

            var openMode = ParseOpenSceneMode(modeStr);

            var tcs = new TaskCompletionSource<SceneInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    if (!System.IO.File.Exists(scenePath))
                    {
                        tcs.TrySetException(new Exception($"未找到指定的场景资源：{scenePath}"));
                        return;
                    }
                    var scene = EditorSceneManager.OpenScene(scenePath, openMode);
                    tcs.TrySetResult(BuildSceneInfo(scene));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "scene.open", result, token);
                _bridge.AddLog("info", $"[Scene] 打开场景: {scenePath}");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENE_OPEN_FAILED", ex.Message, token, "scene.open");
            }
        }

        // ── scene.save ────────────────────────────────────────────────────────

        private async Task HandleSceneSaveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SceneSaveMessage>(json);
            var scenePath = msg?.payload?.scenePath ?? "";

            var tcs = new TaskCompletionSource<SceneInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    Scene scene;
                    if (string.IsNullOrEmpty(scenePath))
                    {
                        // Save the active scene
                        scene = SceneManager.GetActiveScene();
                    }
                    else
                    {
                        scene = SceneManager.GetSceneByPath(scenePath);
                        if (!scene.IsValid())
                        {
                            tcs.TrySetException(new Exception($"未找到已加载的场景：{scenePath}"));
                            return;
                        }
                    }

                    var savePath = string.IsNullOrEmpty(scenePath) ? scene.path : scenePath;
                    bool saved = EditorSceneManager.SaveScene(scene, savePath);
                    if (!saved)
                    {
                        tcs.TrySetException(new Exception($"保存场景失败：{savePath}"));
                        return;
                    }
                    tcs.TrySetResult(BuildSceneInfo(scene));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "scene.save", result, token);
                _bridge.AddLog("info", $"[Scene] 保存场景: {result.scenePath}");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENE_SAVE_FAILED", ex.Message, token, "scene.save");
            }
        }

        // ── scene.load ────────────────────────────────────────────────────────

        private async Task HandleSceneLoadAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SceneLoadMessage>(json);
            var scenePath = msg?.payload?.scenePath ?? "";
            var modeStr = msg?.payload?.mode ?? "additive";

            if (string.IsNullOrEmpty(scenePath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "scenePath 不能为空", token, "scene.load");
                return;
            }

            var openMode = ParseOpenSceneMode(modeStr);

            var tcs = new TaskCompletionSource<SceneInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    if (!System.IO.File.Exists(scenePath))
                    {
                        tcs.TrySetException(new Exception($"未找到指定的场景资源：{scenePath}"));
                        return;
                    }
                    var scene = EditorSceneManager.OpenScene(scenePath, openMode);
                    tcs.TrySetResult(BuildSceneInfo(scene));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "scene.load", result, token);
                _bridge.AddLog("info", $"[Scene] 加载场景: {scenePath} (mode={modeStr})");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENE_LOAD_FAILED", ex.Message, token, "scene.load");
            }
        }

        // ── scene.setActive ───────────────────────────────────────────────────

        private async Task HandleSceneSetActiveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SceneSetActiveMessage>(json);
            var scenePath = msg?.payload?.scenePath ?? "";

            if (string.IsNullOrEmpty(scenePath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "scenePath 不能为空", token, "scene.setActive");
                return;
            }

            var tcs = new TaskCompletionSource<SceneInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var scene = SceneManager.GetSceneByPath(scenePath);
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        tcs.TrySetException(new Exception($"无法激活尚未加载的场景：{scenePath}"));
                        return;
                    }
                    SceneManager.SetActiveScene(scene);
                    tcs.TrySetResult(BuildSceneInfo(scene));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "scene.setActive", result, token);
                _bridge.AddLog("info", $"[Scene] 设置激活场景: {scenePath}");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENE_SET_ACTIVE_FAILED", ex.Message, token, "scene.setActive");
            }
        }

        // ── scene.list ────────────────────────────────────────────────────────

        private async Task HandleSceneListAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<SceneListResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = new SceneListResultPayload();
                    var activeScene = SceneManager.GetActiveScene();
                    int sceneCount = SceneManager.sceneCount;
                    for (int i = 0; i < sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        result.scenes.Add(BuildSceneInfo(scene));
                    }
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "scene.list", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENE_LIST_FAILED", ex.Message, token, "scene.list");
            }
        }

        // ── scene.unload ──────────────────────────────────────────────────────

        private async Task HandleSceneUnloadAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SceneUnloadMessage>(json);
            var p = msg?.payload ?? new SceneUnloadPayload();

            if (string.IsNullOrEmpty(p.scenePath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "scenePath 不能为空", token, "scene.unload");
                return;
            }

            var tcs = new TaskCompletionSource<SceneInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var scene = SceneManager.GetSceneByPath(p.scenePath);
                    if (!scene.IsValid())
                    {
                        tcs.TrySetException(new Exception($"场景无效或未找到：{p.scenePath}"));
                        return;
                    }

                    if (SceneManager.sceneCount <= 1)
                    {
                        tcs.TrySetException(new Exception("无法卸载唯一的场景"));
                        return;
                    }

                    bool removeScene = p.removeScene == 1;
                    var info = BuildSceneInfo(scene);

                    // CloseScene: if removeScene=true, removes from hierarchy; else just unloads
                    bool success = EditorSceneManager.CloseScene(scene, removeScene);
                    if (!success)
                    {
                        tcs.TrySetException(new Exception($"卸载场景失败：{p.scenePath}"));
                        return;
                    }

                    info.isLoaded = false;
                    tcs.TrySetResult(info);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "scene.unload", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENE_UNLOAD_FAILED", ex.Message, token, "scene.unload");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        internal static SceneInfoPayload BuildSceneInfo(Scene scene)
        {
            var activeScene = SceneManager.GetActiveScene();
            return new SceneInfoPayload
            {
                scenePath  = scene.path,
                sceneName  = scene.name,
                buildIndex = scene.buildIndex,
                isLoaded   = scene.isLoaded,
                isDirty    = scene.isDirty,
                rootCount  = scene.isLoaded ? scene.rootCount : 0,
                isActive   = scene == activeScene,
            };
        }

        private static OpenSceneMode ParseOpenSceneMode(string mode)
        {
            if (string.Equals(mode, "additive", StringComparison.OrdinalIgnoreCase))
                return OpenSceneMode.Additive;
            if (string.Equals(mode, "additivewithoutaloadling", StringComparison.OrdinalIgnoreCase))
                return OpenSceneMode.AdditiveWithoutLoading;
            return OpenSceneMode.Single;
        }
    }
}

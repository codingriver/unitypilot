using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SkillEditor.Editor.UnityPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable]
    internal class HierarchyNodePayload
    {
        public int    instanceId;
        public string name;
        public bool   activeSelf;
        public List<HierarchyNodePayload> children = new();
    }

    [Serializable]
    internal class SceneHierarchyResultPayload
    {
        public List<SceneHierarchyPayload> scenes = new();
    }

    [Serializable]
    internal class SceneHierarchyPayload
    {
        public string sceneName;
        public string scenePath;
        public List<HierarchyNodePayload> rootObjects = new();
    }

    [Serializable]
    internal class ConsoleLogEntryPayload
    {
        public string logType;
        public string message;
        public string stackTrace;
    }

    [Serializable]
    internal class ConsoleLogsResourcePayload
    {
        public List<ConsoleLogEntryPayload> logs = new();
        public int total;
    }

    [Serializable]
    internal class EditorStateResourcePayload
    {
        public string  unityVersion;
        public string  platform;
        public bool    isPlaying;
        public bool    isPaused;
        public bool    isCompiling;
        public string  activeSceneName;
        public string  activeScenePath;
        public string  projectPath;
    }

    [Serializable]
    internal class PackageInfoItemPayload
    {
        public string name;
        public string version;
        public string displayName;
        public string description;
    }

    [Serializable]
    internal class PackagesResourcePayload
    {
        public List<PackageInfoItemPayload> packages = new();
    }

    [Serializable]
    internal class BuildStatusResourcePayload
    {
        public string status; // idle, building
        public string activeBuildTarget;
        public string activeBuildTargetGroup;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotResourceService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotResourceService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("resource.sceneHierarchy", HandleSceneHierarchyAsync);
            _bridge.Router.Register("resource.consoleLogs",    HandleConsoleLogsAsync);
            _bridge.Router.Register("resource.editorState",    HandleEditorStateAsync);
            _bridge.Router.Register("resource.packages",       HandlePackagesAsync);
            _bridge.Router.Register("resource.buildStatus",    HandleBuildStatusAsync);
        }

        // ── resource.sceneHierarchy ─────────────────────────────────────────────

        private async Task HandleSceneHierarchyAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<SceneHierarchyResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = new SceneHierarchyResultPayload();

                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (!scene.isLoaded) continue;

                        var scenePayload = new SceneHierarchyPayload
                        {
                            sceneName = scene.name,
                            scenePath = scene.path,
                        };

                        foreach (var rootGo in scene.GetRootGameObjects())
                        {
                            scenePayload.rootObjects.Add(BuildHierarchyNode(rootGo.transform));
                        }

                        result.scenes.Add(scenePayload);
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "resource.sceneHierarchy", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "RESOURCE_FAILED", ex.Message, token, "resource.sceneHierarchy");
            }
        }

        // ── resource.consoleLogs ────────────────────────────────────────────────

        private async Task HandleConsoleLogsAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<ConsoleLogsResourcePayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = new ConsoleLogsResourcePayload();
                    int maxLogs = 100;

                    // Use LogEntries internal API via reflection
                    var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries")
                                      ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntries");

                    if (logEntriesType != null)
                    {
                        var getCount = logEntriesType.GetMethod("GetCount",
                            BindingFlags.Static | BindingFlags.Public);
                        var startGetting = logEntriesType.GetMethod("StartGettingEntries",
                            BindingFlags.Static | BindingFlags.Public);
                        var endGetting = logEntriesType.GetMethod("EndGettingEntries",
                            BindingFlags.Static | BindingFlags.Public);
                        var getEntry = logEntriesType.GetMethod("GetEntryInternal",
                            BindingFlags.Static | BindingFlags.Public);

                        if (getCount != null)
                        {
                            int total = (int)getCount.Invoke(null, null);
                            result.total = total;

                            if (startGetting != null && endGetting != null)
                            {
                                startGetting.Invoke(null, null);
                                try
                                {
                                    // Get the LogEntry type
                                    var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry")
                                                    ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntry");

                                    int start = Math.Max(0, total - maxLogs);
                                    for (int i = start; i < total; i++)
                                    {
                                        if (logEntryType != null && getEntry != null)
                                        {
                                            var entry = Activator.CreateInstance(logEntryType);
                                            getEntry.Invoke(null, new object[] { i, entry });

                                            var msgField = logEntryType.GetField("message") ?? logEntryType.GetField("condition");
                                            var modeField = logEntryType.GetField("mode");

                                            string fullMsg = msgField?.GetValue(entry)?.ToString() ?? "";
                                            int mode = modeField != null ? (int)modeField.GetValue(entry) : 0;

                                            // Split message and stacktrace
                                            string logMessage = fullMsg;
                                            string stackTrace = "";
                                            int nlIdx = fullMsg.IndexOf('\n');
                                            if (nlIdx >= 0)
                                            {
                                                logMessage = fullMsg.Substring(0, nlIdx);
                                                stackTrace = fullMsg.Substring(nlIdx + 1);
                                            }

                                            string logType = "Log";
                                            if ((mode & (1 << 0)) != 0 || (mode & (1 << 9)) != 0) logType = "Error";
                                            else if ((mode & (1 << 1)) != 0 || (mode & (1 << 8)) != 0) logType = "Assert";
                                            else if ((mode & (1 << 5)) != 0) logType = "Warning";

                                            result.logs.Add(new ConsoleLogEntryPayload
                                            {
                                                logType    = logType,
                                                message    = logMessage,
                                                stackTrace = stackTrace,
                                            });
                                        }
                                    }
                                }
                                finally
                                {
                                    endGetting.Invoke(null, null);
                                }
                            }
                        }
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "resource.consoleLogs", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "RESOURCE_FAILED", ex.Message, token, "resource.consoleLogs");
            }
        }

        // ── resource.editorState ────────────────────────────────────────────────

        private async Task HandleEditorStateAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<EditorStateResourcePayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var scene = SceneManager.GetActiveScene();
                    var payload = new EditorStateResourcePayload
                    {
                        unityVersion    = Application.unityVersion,
                        platform        = Application.platform.ToString(),
                        isPlaying       = EditorApplication.isPlaying,
                        isPaused        = EditorApplication.isPaused,
                        isCompiling     = EditorApplication.isCompiling,
                        activeSceneName = scene.name,
                        activeScenePath = scene.path,
                        projectPath     = Application.dataPath,
                    };
                    tcs.SetResult(payload);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "resource.editorState", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "RESOURCE_FAILED", ex.Message, token, "resource.editorState");
            }
        }

        // ── resource.packages ───────────────────────────────────────────────────

        private async Task HandlePackagesAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<PackagesResourcePayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = new PackagesResourcePayload();
                    var request = UnityEditor.PackageManager.Client.List(true);

                    // Synchronous wait — PackageManager.Client.List is request-based
                    // but we're already on main thread, so we spin-wait
                    while (!request.IsCompleted)
                    {
                        System.Threading.Thread.Sleep(10);
                    }

                    if (request.Status == UnityEditor.PackageManager.StatusCode.Success)
                    {
                        foreach (var pkg in request.Result)
                        {
                            result.packages.Add(new PackageInfoItemPayload
                            {
                                name        = pkg.name,
                                version     = pkg.version,
                                displayName = pkg.displayName,
                                description = pkg.description,
                            });
                        }
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "resource.packages", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "RESOURCE_FAILED", ex.Message, token, "resource.packages");
            }
        }

        // ── resource.buildStatus ────────────────────────────────────────────────

        private async Task HandleBuildStatusAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<BuildStatusResourcePayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var payload = new BuildStatusResourcePayload
                    {
                        status                = BuildPipeline.isBuildingPlayer ? "building" : "idle",
                        activeBuildTarget     = EditorUserBuildSettings.activeBuildTarget.ToString(),
                        activeBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString(),
                    };
                    tcs.SetResult(payload);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "resource.buildStatus", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "RESOURCE_FAILED", ex.Message, token, "resource.buildStatus");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static HierarchyNodePayload BuildHierarchyNode(Transform t)
        {
            var node = new HierarchyNodePayload
            {
                instanceId = t.gameObject.GetInstanceID(),
                name       = t.gameObject.name,
                activeSelf = t.gameObject.activeSelf,
            };

            for (int i = 0; i < t.childCount; i++)
            {
                node.children.Add(BuildHierarchyNode(t.GetChild(i)));
            }

            return node;
        }
    }
}

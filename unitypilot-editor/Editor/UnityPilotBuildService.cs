using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class BuildStartMessage  { public BuildStartPayload payload; }
    [Serializable]
    internal class BuildStartPayload
    {
        public string       buildTarget = "";
        public string       outputPath  = "";
        public List<string> scenes      = new();
    }

    [Serializable]
    internal class BuildStatusPayload
    {
        public string status;        // idle, building, succeeded, failed
        public string buildTarget;
        public string outputPath;
        public int    totalErrors;
        public int    totalWarnings;
        public string summary;
    }

    [Serializable]
    internal class BuildTargetItemPayload
    {
        public string name;
        public string displayName;
    }

    [Serializable]
    internal class BuildTargetsResultPayload
    {
        public List<BuildTargetItemPayload> targets = new();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotBuildService
    {
        private readonly UnityPilotBridge _bridge;

        // Track current build state
        private BuildStatusPayload _lastBuildStatus = new BuildStatusPayload { status = "idle" };
        private volatile bool _isBuildingAsync;
        private CancellationTokenSource _buildCts;

        public UnityPilotBuildService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("build.start",   HandleStartAsync);
            _bridge.Router.Register("build.status",   HandleStatusAsync);
            _bridge.Router.Register("build.cancel",   HandleCancelAsync);
            _bridge.Router.Register("build.targets",  HandleTargetsAsync);
        }

        // ── build.start ─────────────────────────────────────────────────────────

        private async Task HandleStartAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<BuildStartMessage>(json);
            var p   = msg?.payload ?? new BuildStartPayload();

            if (string.IsNullOrEmpty(p.buildTarget))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "buildTarget is required.", token, "build.start");
                return;
            }

            if (string.IsNullOrEmpty(p.outputPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "outputPath is required.", token, "build.start");
                return;
            }

            if (_isBuildingAsync)
            {
                await _bridge.SendErrorAsync(id, "EDITOR_BUSY", "A build is already in progress.", token, "build.start");
                return;
            }

            // Parse build target
            if (!TryParseBuildTarget(p.buildTarget, out BuildTarget target))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", $"Unknown build target: {p.buildTarget}", token, "build.start");
                return;
            }

            // Collect scenes
            string[] scenes;
            if (p.scenes != null && p.scenes.Count > 0)
            {
                scenes = p.scenes.ToArray();
            }
            else
            {
                // Use scenes from build settings
                var buildScenes = new List<string>();
                foreach (var s in EditorBuildSettings.scenes)
                {
                    if (s.enabled) buildScenes.Add(s.path);
                }
                scenes = buildScenes.ToArray();
            }

            if (scenes.Length == 0)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "No scenes specified and no scenes in Build Settings.", token, "build.start");
                return;
            }

            _buildCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _isBuildingAsync = true;

            _lastBuildStatus = new BuildStatusPayload
            {
                status      = "building",
                buildTarget = target.ToString(),
                outputPath  = p.outputPath,
            };

            // Build on main thread
            var tcs = new TaskCompletionSource<BuildStatusPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var options = new BuildPlayerOptions
                    {
                        scenes           = scenes,
                        locationPathName = p.outputPath,
                        target           = target,
                        options          = BuildOptions.None,
                    };

                    var report = BuildPipeline.BuildPlayer(options);
                    var result = new BuildStatusPayload
                    {
                        buildTarget   = target.ToString(),
                        outputPath    = p.outputPath,
                        totalErrors   = report.summary.totalErrors,
                        totalWarnings = report.summary.totalWarnings,
                    };

                    switch (report.summary.result)
                    {
                        case BuildResult.Succeeded:
                            result.status  = "succeeded";
                            result.summary = $"Build succeeded. {report.summary.totalSize} bytes, {report.summary.totalTime.TotalSeconds:F1}s.";
                            break;
                        case BuildResult.Failed:
                            result.status  = "failed";
                            result.summary = $"Build failed with {report.summary.totalErrors} error(s).";
                            break;
                        case BuildResult.Cancelled:
                            result.status  = "failed";
                            result.summary = "Build was cancelled.";
                            break;
                        default:
                            result.status  = "failed";
                            result.summary = $"Build result: {report.summary.result}";
                            break;
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new BuildStatusPayload
                    {
                        status      = "failed",
                        buildTarget = target.ToString(),
                        outputPath  = p.outputPath,
                        summary     = $"Build exception: {ex.Message}",
                    });
                }
            });

            try
            {
                // Wait with overall timeout (600s)
                var timeoutTask = Task.Delay(600000, _buildCts.Token);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completed == timeoutTask && !tcs.Task.IsCompleted)
                {
                    _lastBuildStatus = new BuildStatusPayload
                    {
                        status      = "failed",
                        buildTarget = target.ToString(),
                        outputPath  = p.outputPath,
                        summary     = "Build timed out after 600s.",
                    };
                }
                else
                {
                    _lastBuildStatus = tcs.Task.Result;
                }
            }
            catch (OperationCanceledException)
            {
                _lastBuildStatus = new BuildStatusPayload
                {
                    status      = "failed",
                    buildTarget = target.ToString(),
                    outputPath  = p.outputPath,
                    summary     = "Build was cancelled.",
                };
            }
            finally
            {
                _isBuildingAsync = false;
                _buildCts = null;
            }

            await _bridge.SendResultAsync(id, "build.start", _lastBuildStatus, token);
        }

        // ── build.status ────────────────────────────────────────────────────────

        private async Task HandleStatusAsync(string id, string json, CancellationToken token)
        {
            await _bridge.SendResultAsync(id, "build.status", _lastBuildStatus, token);
        }

        // ── build.cancel ────────────────────────────────────────────────────────

        private async Task HandleCancelAsync(string id, string json, CancellationToken token)
        {
            if (_buildCts != null && !_buildCts.IsCancellationRequested)
            {
                _buildCts.Cancel();
                await _bridge.SendResultAsync(id, "build.cancel", new GenericOkPayload(), token);
            }
            else
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", "No active build to cancel.", token, "build.cancel");
            }
        }

        // ── build.targets ───────────────────────────────────────────────────────

        private async Task HandleTargetsAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<BuildTargetsResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = new BuildTargetsResultPayload();

                    // List installed/available build targets
                    foreach (BuildTarget bt in Enum.GetValues(typeof(BuildTarget)))
                    {
                        // Skip obsolete or negative values
                        if ((int)bt < 0) continue;
                        if (bt == BuildTarget.NoTarget) continue;

                        // Check if the module is installed
                        try
                        {
                            var moduleType = typeof(BuildPipeline).Assembly.GetType("UnityEditor.Modules.ModuleManager");
                            bool isInstalled = true;

                            if (moduleType != null)
                            {
                                var isPlatformInstalled = moduleType.GetMethod("IsPlatformSupportLoaded",
                                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                if (isPlatformInstalled != null)
                                {
                                    // Get module name from build target
                                    var getTargetString = moduleType.GetMethod("GetTargetStringFromBuildTarget",
                                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                    if (getTargetString != null)
                                    {
                                        var targetString = getTargetString.Invoke(null, new object[] { bt }) as string;
                                        if (!string.IsNullOrEmpty(targetString))
                                        {
                                            isInstalled = (bool)isPlatformInstalled.Invoke(null, new object[] { targetString });
                                        }
                                    }
                                }
                            }

                            if (isInstalled)
                            {
                                result.targets.Add(new BuildTargetItemPayload
                                {
                                    name        = bt.ToString(),
                                    displayName = ObjectNames.NicifyVariableName(bt.ToString()),
                                });
                            }
                        }
                        catch
                        {
                            // Skip targets that throw during check
                        }
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "build.targets", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "BUILD_TARGETS_FAILED", ex.Message, token, "build.targets");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static bool TryParseBuildTarget(string name, out BuildTarget target)
        {
            target = BuildTarget.StandaloneWindows64;

            if (string.IsNullOrEmpty(name)) return false;

            // Try exact enum parse
            if (Enum.TryParse(name, true, out target)) return true;

            // Common aliases
            string lower = name.ToLowerInvariant();
            switch (lower)
            {
                case "windows":
                case "win":
                case "win64":
                case "standalonewindows":
                    target = BuildTarget.StandaloneWindows64;
                    return true;
                case "win32":
                    target = BuildTarget.StandaloneWindows;
                    return true;
                case "mac":
                case "macos":
                case "osx":
                    target = BuildTarget.StandaloneOSX;
                    return true;
                case "linux":
                case "linux64":
                    target = BuildTarget.StandaloneLinux64;
                    return true;
                case "android":
                    target = BuildTarget.Android;
                    return true;
                case "ios":
                    target = BuildTarget.iOS;
                    return true;
                case "webgl":
                    target = BuildTarget.WebGL;
                    return true;
                default:
                    return false;
            }
        }
    }
}

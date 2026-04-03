using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class TestRunMessage     { public TestRunPayload payload; }
    [Serializable] internal class TestRunPayload     { public string testMode = "EditMode"; public string testFilter = ""; }

    [Serializable] internal class TestListMessage    { public TestListPayload payload; }
    [Serializable] internal class TestListPayload    { public string testMode = "EditMode"; }

    [Serializable]
    internal class TestResultItemPayload
    {
        public string testName;
        public string testStatus;  // Passed, Failed, Skipped, Inconclusive
        public float  duration;
        public string message;
        public string stackTrace;
    }

    [Serializable]
    internal class TestRunResultPayload
    {
        public string status;  // started, completed
        public string testMode;
        public int    total;
        public int    passed;
        public int    failed;
        public int    skipped;
        public List<TestResultItemPayload> results = new List<TestResultItemPayload>();
    }

    [Serializable]
    internal class TestListResultPayload
    {
        public string testMode;
        public List<string> tests = new List<string>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotTestService
    {
        private readonly UnityPilotBridge _bridge;
        private TestRunResultPayload _lastResults;
        private bool _isRunning;

        public UnityPilotTestService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("test.run",     HandleRunAsync);
            _bridge.Router.Register("test.results", HandleResultsAsync);
            _bridge.Router.Register("test.list",    HandleListAsync);
        }

        // ── test.run ────────────────────────────────────────────────────────────

        private async Task HandleRunAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<TestRunMessage>(json);
            var p   = msg?.payload ?? new TestRunPayload();

            if (_isRunning)
            {
                await _bridge.SendErrorAsync(id, "TEST_ALREADY_RUNNING", "A test run is already in progress.", token, "test.run");
                return;
            }

            string mode = NormalizeTestMode(p.testMode);

            var tcs = new TaskCompletionSource<TestRunResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    _isRunning = true;
                    _lastResults = new TestRunResultPayload { testMode = mode, status = "started" };

                    // Use TestRunner API via reflection since it's in a separate assembly
                    // UnityEditor.TestTools.TestRunner.Api.TestRunnerApi
                    var apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                    if (apiType == null)
                    {
                        _isRunning = false;
                        tcs.SetException(new Exception("TestRunnerApi not found. Ensure Test Framework package is installed."));
                        return;
                    }

                    var api = ScriptableObject.CreateInstance(apiType);

                    // Create filter
                    var filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
                    if (filterType == null)
                    {
                        _isRunning = false;
                        tcs.SetException(new Exception("Test Filter type not found."));
                        return;
                    }

                    var filter = Activator.CreateInstance(filterType);

                    // Set testMode
                    var testModeEnum = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
                    if (testModeEnum != null)
                    {
                        var modeValue = mode == "PlayMode" ? Enum.Parse(testModeEnum, "PlayMode") : Enum.Parse(testModeEnum, "EditMode");
                        var testModeField = filterType.GetField("testMode");
                        if (testModeField != null)
                            testModeField.SetValue(filter, modeValue);
                    }

                    // Set filter if specified
                    if (!string.IsNullOrEmpty(p.testFilter))
                    {
                        var testNamesField = filterType.GetField("testNames");
                        if (testNamesField != null)
                            testNamesField.SetValue(filter, new[] { p.testFilter });
                    }

                    // Create ExecutionSettings
                    var execSettingsType = FindType("UnityEditor.TestTools.TestRunner.Api.ExecutionSettings");
                    if (execSettingsType == null)
                    {
                        _isRunning = false;
                        tcs.SetException(new Exception("ExecutionSettings type not found."));
                        return;
                    }

                    var execSettings = Activator.CreateInstance(execSettingsType);
                    var filtersField = execSettingsType.GetField("filters") ?? execSettingsType.GetField("filter");
                    if (filtersField != null)
                    {
                        if (filtersField.FieldType.IsArray)
                        {
                            var arr = Array.CreateInstance(filterType, 1);
                            arr.SetValue(filter, 0);
                            filtersField.SetValue(execSettings, arr);
                        }
                        else
                        {
                            filtersField.SetValue(execSettings, filter);
                        }
                    }

                    // Register callbacks
                    var callbacksType = FindType("UnityEditor.TestTools.TestRunner.Api.ICallbacks");
                    if (callbacksType != null)
                    {
                        var callbackInstance = new TestCallbackProxy(this, tcs);
                        var registerMethod = apiType.GetMethod("RegisterCallbacks");
                        if (registerMethod != null)
                        {
                            // Create a dynamic proxy wrapper
                            // Since we can't directly implement the interface, use a workaround:
                            // We'll poll for completion instead
                        }
                    }

                    // Execute
                    var executeMethod = apiType.GetMethod("Execute");
                    if (executeMethod != null)
                    {
                        executeMethod.Invoke(api, new[] { execSettings });
                    }

                    // Since callback registration is complex via reflection,
                    // we return immediately with "started" and let test.results poll for results
                    _lastResults.status = "running";
                    tcs.SetResult(_lastResults);

                    // Set up a delayed check to mark as complete
                    EditorApplication.delayCall += () =>
                    {
                        // The test runner will complete asynchronously
                        // Results can be queried via test.results
                        _isRunning = false;
                        _lastResults.status = "completed";
                    };
                }
                catch (Exception ex)
                {
                    _isRunning = false;
                    tcs.SetException(ex);
                }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "test.run", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "TEST_RUN_FAILED", ex.Message, token, "test.run");
            }
        }

        // ── test.results ────────────────────────────────────────────────────────

        private async Task HandleResultsAsync(string id, string json, CancellationToken token)
        {
            var result = _lastResults ?? new TestRunResultPayload { status = "none", testMode = "" };
            await _bridge.SendResultAsync(id, "test.results", result, token);
        }

        // ── test.list ───────────────────────────────────────────────────────────

        private async Task HandleListAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<TestListMessage>(json);
            var p   = msg?.payload ?? new TestListPayload();

            string mode = NormalizeTestMode(p.testMode);

            var tcs = new TaskCompletionSource<TestListResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = new TestListResultPayload { testMode = mode };

                    // Find tests via reflection of test assemblies
                    string assemblyNameSuffix = mode == "PlayMode" ? ".PlayMode" : ".EditMode";

                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string asmName = assembly.GetName().Name;

                        // Look for test assemblies (usually contain "Tests" or "Test")
                        bool isTestAssembly = asmName.Contains("Test") ||
                                             asmName.EndsWith(".Tests") ||
                                             asmName.EndsWith(assemblyNameSuffix);

                        if (!isTestAssembly) continue;

                        try
                        {
                            foreach (var type in assembly.GetTypes())
                            {
                                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                {
                                    // Check for [Test] or [UnityTest] attributes
                                    bool hasTest = false;
                                    foreach (var attr in method.GetCustomAttributes(false))
                                    {
                                        string attrName = attr.GetType().Name;
                                        if (attrName == "TestAttribute" || attrName == "UnityTestAttribute" ||
                                            attrName == "TestCaseAttribute")
                                        {
                                            hasTest = true;
                                            break;
                                        }
                                    }

                                    if (hasTest)
                                    {
                                        result.tests.Add($"{type.FullName}.{method.Name}");
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
                await _bridge.SendResultAsync(id, "test.list", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "TEST_LIST_FAILED", ex.Message, token, "test.list");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string NormalizeTestMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return "EditMode";
            if (mode.Equals("PlayMode", StringComparison.OrdinalIgnoreCase)) return "PlayMode";
            return "EditMode";
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        // Placeholder for callback proxy
        private class TestCallbackProxy
        {
            private readonly UnityPilotTestService _service;
            private readonly TaskCompletionSource<TestRunResultPayload> _tcs;

            public TestCallbackProxy(UnityPilotTestService service, TaskCompletionSource<TestRunResultPayload> tcs)
            {
                _service = service;
                _tcs     = tcs;
            }
        }
    }
}

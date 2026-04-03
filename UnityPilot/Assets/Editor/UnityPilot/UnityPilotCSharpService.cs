using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class CSharpExecuteMessage  { public CSharpExecutePayload payload; }
    [Serializable] internal class CSharpExecutePayload  { public string code = ""; public int timeoutSeconds = 10; }

    [Serializable] internal class CSharpStatusMessage   { public CSharpStatusPayload payload; }
    [Serializable] internal class CSharpStatusPayload   { public string executionId = ""; }

    [Serializable] internal class CSharpAbortMessage    { public CSharpAbortPayload payload; }
    [Serializable] internal class CSharpAbortPayload    { public string executionId = ""; }

    [Serializable]
    internal class CSharpExecuteResultPayload
    {
        public string executionId;
        public string status;      // pending, running, completed, failed, timeout, aborted
        public string result;      // serialized return value or null
        public string error;       // error message or null
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotCSharpService
    {
        private readonly UnityPilotBridge _bridge;

        // Execution tracking
        private readonly ConcurrentDictionary<string, CSharpExecuteResultPayload> _executions = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _execCts = new();

        public UnityPilotCSharpService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("csharp.execute", HandleExecuteAsync);
            _bridge.Router.Register("csharp.status",  HandleStatusAsync);
            _bridge.Router.Register("csharp.abort",   HandleAbortAsync);
        }

        // ── csharp.execute ──────────────────────────────────────────────────────

        private async Task HandleExecuteAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<CSharpExecuteMessage>(json);
            var p   = msg?.payload ?? new CSharpExecutePayload();

            if (string.IsNullOrEmpty(p.code))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "code is required.", token, "csharp.execute");
                return;
            }

            // Sandbox: block dangerous patterns
            if (ContainsDangerousCode(p.code))
            {
                await _bridge.SendErrorAsync(id, "SECURITY_VIOLATION",
                    "Code contains disallowed operations (Process.Start, File I/O outside project, etc.).",
                    token, "csharp.execute");
                return;
            }

            int timeout = Mathf.Clamp(p.timeoutSeconds, 1, 30);
            string execId = Guid.NewGuid().ToString("N").Substring(0, 12);

            var result = new CSharpExecuteResultPayload
            {
                executionId = execId,
                status = "running",
            };
            _executions[execId] = result;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _execCts[execId] = cts;

            // Execute on main thread with timeout
            var tcs = new TaskCompletionSource<string>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        tcs.SetCanceled();
                        return;
                    }

                    string evalResult = EvaluateCSharpCode(p.code);
                    tcs.SetResult(evalResult);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var timeoutTask = Task.Delay(timeout * 1000, cts.Token);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completed == timeoutTask)
                {
                    result.status = "timeout";
                    result.error = $"Execution timed out after {timeout}s.";
                    cts.Cancel();
                }
                else if (tcs.Task.IsCanceled)
                {
                    result.status = "aborted";
                    result.error = "Execution was aborted.";
                }
                else if (tcs.Task.IsFaulted)
                {
                    result.status = "failed";
                    result.error = tcs.Task.Exception?.InnerException?.Message ?? "Unknown error";
                }
                else
                {
                    result.status = "completed";
                    result.result = tcs.Task.Result;
                }
            }
            catch (OperationCanceledException)
            {
                result.status = "aborted";
                result.error = "Execution was aborted.";
            }
            catch (Exception ex)
            {
                result.status = "failed";
                result.error = ex.Message;
            }
            finally
            {
                _execCts.TryRemove(execId, out _);
            }

            _executions[execId] = result;
            await _bridge.SendResultAsync(id, "csharp.execute", result, token);
        }

        // ── csharp.status ───────────────────────────────────────────────────────

        private async Task HandleStatusAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<CSharpStatusMessage>(json);
            var p   = msg?.payload ?? new CSharpStatusPayload();

            if (string.IsNullOrEmpty(p.executionId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "executionId is required.", token, "csharp.status");
                return;
            }

            if (_executions.TryGetValue(p.executionId, out var result))
            {
                await _bridge.SendResultAsync(id, "csharp.status", result, token);
            }
            else
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", $"Execution not found: {p.executionId}", token, "csharp.status");
            }
        }

        // ── csharp.abort ────────────────────────────────────────────────────────

        private async Task HandleAbortAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<CSharpAbortMessage>(json);
            var p   = msg?.payload ?? new CSharpAbortPayload();

            if (string.IsNullOrEmpty(p.executionId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "executionId is required.", token, "csharp.abort");
                return;
            }

            if (_execCts.TryRemove(p.executionId, out var cts))
            {
                cts.Cancel();
                if (_executions.TryGetValue(p.executionId, out var result))
                {
                    result.status = "aborted";
                    result.error = "Execution was aborted by user.";
                }
                await _bridge.SendResultAsync(id, "csharp.abort", new GenericOkPayload(), token);
            }
            else
            {
                await _bridge.SendErrorAsync(id, "NOT_FOUND", $"Execution not found or already completed: {p.executionId}", token, "csharp.abort");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static bool ContainsDangerousCode(string code)
        {
            string lower = code.ToLowerInvariant();
            // Block process execution, raw file I/O outside Unity, and network
            string[] blocked = new[]
            {
                "process.start", "system.diagnostics.process",
                "environment.exit", "application.quit",
                "assembly.load", "activator.createinstance",
                "appdomain",
            };
            foreach (var pattern in blocked)
            {
                if (lower.Contains(pattern)) return true;
            }
            return false;
        }

        private static string EvaluateCSharpCode(string code)
        {
            // Strategy: Use System.Reflection to compile and evaluate via
            // Microsoft.CSharp.CSharpCodeProvider if available, otherwise
            // fallback to Mono's evaluator if available, or simple eval via Reflection.
            //
            // For Unity editor, we use the Mono evaluator approach:
            // Wrap code in a static method, compile via CodeDomProvider, invoke.

            try
            {
                // Try using Mono.CSharp.Evaluator via reflection (available in Unity's Mono)
                var monoEvalType = FindType("Mono.CSharp.Evaluator");
                if (monoEvalType != null)
                {
                    return EvaluateViaMono(monoEvalType, code);
                }

                // Fallback: try CSharpCodeProvider
                var providerType = FindType("Microsoft.CSharp.CSharpCodeProvider");
                if (providerType != null)
                {
                    return EvaluateViaCodeDom(providerType, code);
                }

                return "Error: No C# evaluator available in this Unity runtime.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private static string EvaluateViaMono(Type evalType, string code)
        {
            // Mono.CSharp.Evaluator is a static class with Init + Evaluate methods
            try
            {
                // Try to init the evaluator
                var initMethod = evalType.GetMethod("Init", BindingFlags.Static | BindingFlags.Public);
                if (initMethod != null && initMethod.GetParameters().Length == 0)
                {
                    initMethod.Invoke(null, null);
                }

                // Evaluate the code
                var evalMethod = evalType.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(string) }, null);

                if (evalMethod == null)
                {
                    // Try overload with out parameters
                    var methods = evalType.GetMethods(BindingFlags.Static | BindingFlags.Public);
                    foreach (var m in methods)
                    {
                        if (m.Name == "Evaluate")
                        {
                            var parms = m.GetParameters();
                            if (parms.Length >= 1 && parms[0].ParameterType == typeof(string))
                            {
                                evalMethod = m;
                                break;
                            }
                        }
                    }
                }

                if (evalMethod != null)
                {
                    var parms = evalMethod.GetParameters();
                    object result;
                    if (parms.Length == 1)
                    {
                        result = evalMethod.Invoke(null, new object[] { code });
                    }
                    else
                    {
                        // Overload: Evaluate(string, out object result, out bool result_set)
                        var args = new object[parms.Length];
                        args[0] = code;
                        evalMethod.Invoke(null, args);
                        result = parms.Length > 1 ? args[1] : null;
                    }
                    return result?.ToString() ?? "(null)";
                }

                // Fallback: Run / ReferenceAssembly pattern
                var runMethod = evalType.GetMethod("Run", BindingFlags.Static | BindingFlags.Public);
                if (runMethod != null)
                {
                    runMethod.Invoke(null, new object[] { code });
                    return "(executed, no return value)";
                }

                return "Error: Could not find suitable Evaluate method on Mono.CSharp.Evaluator.";
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        }

        private static string EvaluateViaCodeDom(Type providerType, string code)
        {
            try
            {
                // Wrap user code in a class with a static method
                string wrappedCode = @"
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public static class __CSharpEval {
    public static object Run() {
        " + code + @"
        return null;
    }
}";
                var provider = Activator.CreateInstance(providerType);
                var compileMethod = providerType.GetMethod("CompileAssemblyFromSource");
                if (compileMethod == null) return "Error: CompileAssemblyFromSource not found.";

                // Create compiler parameters via reflection
                var paramsType = FindType("System.CodeDom.Compiler.CompilerParameters");
                if (paramsType == null) return "Error: CompilerParameters not found.";

                var compParams = Activator.CreateInstance(paramsType);
                var refAssemblies = paramsType.GetProperty("ReferencedAssemblies")?.GetValue(compParams) as System.Collections.IList;
                if (refAssemblies != null)
                {
                    refAssemblies.Add("System.dll");
                    refAssemblies.Add("System.Core.dll");
                    // Add Unity assemblies
                    refAssemblies.Add(typeof(UnityEngine.Object).Assembly.Location);
                    refAssemblies.Add(typeof(UnityEditor.Editor).Assembly.Location);
                }
                paramsType.GetProperty("GenerateInMemory")?.SetValue(compParams, true);

                var results = compileMethod.Invoke(provider, new[] { compParams, new[] { wrappedCode } });
                var errorsProperty = results.GetType().GetProperty("Errors");
                var errors = errorsProperty?.GetValue(results) as System.Collections.ICollection;
                if (errors != null && errors.Count > 0)
                {
                    var sb = new System.Text.StringBuilder("Compilation errors:\n");
                    foreach (var err in errors) sb.AppendLine(err.ToString());
                    return sb.ToString();
                }

                var assemblyProp = results.GetType().GetProperty("CompiledAssembly");
                var assembly = assemblyProp?.GetValue(results) as Assembly;
                if (assembly == null) return "Error: Compilation produced no assembly.";

                var evalClass = assembly.GetType("__CSharpEval");
                var runMethod = evalClass?.GetMethod("Run", BindingFlags.Static | BindingFlags.Public);
                var result = runMethod?.Invoke(null, null);
                return result?.ToString() ?? "(null)";
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { /* skip */ }
            }
            return null;
        }
    }
}

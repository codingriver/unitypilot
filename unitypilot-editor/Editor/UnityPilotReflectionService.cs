using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class ReflectionFindMessage  { public ReflectionFindPayload payload; }
    [Serializable]
    internal class ReflectionFindPayload
    {
        public string typeName   = "";
        public string methodName = "";
    }

    [Serializable] internal class ReflectionCallMessage  { public ReflectionCallPayload payload; }
    [Serializable]
    internal class ReflectionCallPayload
    {
        public string typeName          = "";
        public string methodName        = "";
        public string[] parameters;        // string representations of args
        public bool   isStatic          = true;
        public string targetInstancePath = ""; // hierarchy path for instance methods
    }

    [Serializable]
    internal class ReflectionMethodInfoPayload
    {
        public string typeName;
        public string methodName;
        public string returnType;
        public bool   isStatic;
        public List<ReflectionParamInfoPayload> parameters = new();
    }

    [Serializable]
    internal class ReflectionParamInfoPayload
    {
        public string name;
        public string type;
    }

    [Serializable]
    internal class ReflectionFindResultPayload
    {
        public List<ReflectionMethodInfoPayload> methods = new();
    }

    [Serializable]
    internal class ReflectionCallResultPayload
    {
        public string typeName;
        public string methodName;
        public string result;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotReflectionService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotReflectionService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("reflection.find", HandleFindAsync);
            _bridge.Router.Register("reflection.call", HandleCallAsync);
        }

        // ── reflection.find ─────────────────────────────────────────────────────

        private async Task HandleFindAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ReflectionFindMessage>(json);
            var p   = msg?.payload ?? new ReflectionFindPayload();

            if (string.IsNullOrEmpty(p.typeName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "typeName is required.", token, "reflection.find");
                return;
            }

            var tcs = new TaskCompletionSource<ReflectionFindResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var type = FindType(p.typeName);
                    if (type == null)
                    {
                        tcs.SetException(new Exception($"Type not found: {p.typeName}"));
                        return;
                    }

                    var result = new ReflectionFindResultPayload();
                    var flags = BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Static | BindingFlags.Instance;

                    MethodInfo[] methods;
                    if (!string.IsNullOrEmpty(p.methodName))
                    {
                        // Find specific method(s) by name
                        methods = Array.FindAll(type.GetMethods(flags), m => m.Name == p.methodName);
                    }
                    else
                    {
                        methods = type.GetMethods(flags);
                    }

                    foreach (var m in methods)
                    {
                        // Skip compiler-generated and property accessors for cleaner output
                        if (m.IsSpecialName) continue;

                        var info = new ReflectionMethodInfoPayload
                        {
                            typeName   = type.FullName,
                            methodName = m.Name,
                            returnType = m.ReturnType.FullName ?? m.ReturnType.Name,
                            isStatic   = m.IsStatic,
                        };

                        foreach (var param in m.GetParameters())
                        {
                            info.parameters.Add(new ReflectionParamInfoPayload
                            {
                                name = param.Name,
                                type = param.ParameterType.FullName ?? param.ParameterType.Name,
                            });
                        }

                        result.methods.Add(info);
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "reflection.find", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "REFLECTION_FIND_FAILED", ex.Message, token, "reflection.find");
            }
        }

        // ── reflection.call ─────────────────────────────────────────────────────

        private async Task HandleCallAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ReflectionCallMessage>(json);
            var p   = msg?.payload ?? new ReflectionCallPayload();

            if (string.IsNullOrEmpty(p.typeName) || string.IsNullOrEmpty(p.methodName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "typeName and methodName are required.", token, "reflection.call");
                return;
            }

            var tcs = new TaskCompletionSource<ReflectionCallResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var type = FindType(p.typeName);
                    if (type == null)
                    {
                        tcs.SetException(new Exception($"Type not found: {p.typeName}"));
                        return;
                    }

                    var flags = BindingFlags.Public | BindingFlags.NonPublic |
                                (p.isStatic ? BindingFlags.Static : BindingFlags.Instance);

                    // Find the method, accounting for overloads by param count
                    MethodInfo method = null;
                    int paramCount = p.parameters?.Length ?? 0;
                    foreach (var m in type.GetMethods(flags))
                    {
                        if (m.Name == p.methodName && m.GetParameters().Length == paramCount)
                        {
                            method = m;
                            break;
                        }
                    }

                    if (method == null)
                    {
                        tcs.SetException(new Exception($"Method not found: {p.typeName}.{p.methodName} with {paramCount} parameters."));
                        return;
                    }

                    // Convert parameters
                    var methodParams = method.GetParameters();
                    object[] args = new object[paramCount];
                    for (int i = 0; i < paramCount; i++)
                    {
                        args[i] = ConvertParameter(p.parameters[i], methodParams[i].ParameterType);
                    }

                    // Get target instance for non-static calls
                    object target = null;
                    if (!p.isStatic)
                    {
                        target = ResolveInstance(p.targetInstancePath, type);
                        if (target == null)
                        {
                            tcs.SetException(new Exception($"Could not resolve instance at path: {p.targetInstancePath}"));
                            return;
                        }
                    }

                    var result = method.Invoke(target, args);

                    tcs.SetResult(new ReflectionCallResultPayload
                    {
                        typeName   = p.typeName,
                        methodName = p.methodName,
                        result     = result?.ToString() ?? "(null)",
                    });
                }
                catch (TargetInvocationException tie)
                {
                    tcs.SetException(tie.InnerException ?? tie);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "reflection.call", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "REFLECTION_CALL_FAILED", ex.Message, token, "reflection.call");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

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
            // Fallback: search by name without namespace
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == fullName) return t;
                    }
                }
                catch { /* skip */ }
            }
            return null;
        }

        private static object ConvertParameter(string value, Type targetType)
        {
            if (targetType == typeof(string))  return value;
            if (targetType == typeof(int))     return int.Parse(value);
            if (targetType == typeof(float))   return float.Parse(value);
            if (targetType == typeof(double))  return double.Parse(value);
            if (targetType == typeof(bool))    return bool.Parse(value);
            if (targetType == typeof(long))    return long.Parse(value);
            if (targetType.IsEnum)             return Enum.Parse(targetType, value, true);

            // Try JSON deserialization for complex types
            try { return JsonUtility.FromJson(value, targetType); }
            catch { /* fall through */ }

            // Last resort: Convert.ChangeType
            return Convert.ChangeType(value, targetType);
        }

        /// <summary>
        /// Resolve an instance from a hierarchy path like "/Canvas/Button" → find GO → get component of type.
        /// </summary>
        private static object ResolveInstance(string targetInstancePath, Type componentType)
        {
            if (string.IsNullOrEmpty(targetInstancePath)) return null;

            // Try to find the GameObject by hierarchy path
            var go = GameObject.Find(targetInstancePath);
            if (go == null)
            {
                // Try without leading slash
                string trimmed = targetInstancePath.TrimStart('/');
                go = GameObject.Find(trimmed);
            }

            if (go == null) return null;

            // If the target type is a Component subclass, get it from the GO
            if (typeof(Component).IsAssignableFrom(componentType))
            {
                return go.GetComponent(componentType);
            }

            // If the target type is GameObject itself
            if (componentType == typeof(GameObject))
            {
                return go;
            }

            // Try to find any component of that type
            return go.GetComponent(componentType);
        }
    }
}

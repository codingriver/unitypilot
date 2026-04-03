using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── M10 Component DTOs ────────────────────────────────────────────────────

    [Serializable]
    internal class ComponentAddMessage
    {
        public string id;
        public string type;
        public string name;
        public ComponentAddPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class ComponentAddPayload
    {
        public int gameObjectId;
        public string componentType = "";
    }

    [Serializable]
    internal class ComponentRemoveMessage
    {
        public string id;
        public string type;
        public string name;
        public ComponentRemovePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class ComponentRemovePayload
    {
        public int gameObjectId;
        public string componentType = "";
        public int componentIndex;
    }

    [Serializable]
    internal class ComponentGetMessage
    {
        public string id;
        public string type;
        public string name;
        public ComponentGetPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class ComponentGetPayload
    {
        public int gameObjectId;
        public string componentType = "";
        public int componentIndex;
    }

    [Serializable]
    internal class ComponentModifyMessage
    {
        public string id;
        public string type;
        public string name;
        public ComponentModifyPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class ComponentModifyPayload
    {
        public int gameObjectId;
        public string componentType = "";
        public string properties = "{}"; // JSON string because JsonUtility can't handle dict
        public int componentIndex;
    }

    [Serializable]
    internal class ComponentListMessage
    {
        public string id;
        public string type;
        public string name;
        public ComponentListPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class ComponentListPayload
    {
        public int gameObjectId;
    }

    [Serializable]
    internal class ComponentInfoPayload
    {
        public string componentType;
        public int componentIndex;
        public bool enabled;
        public List<ComponentPropertyPayload> properties = new();
    }

    [Serializable]
    internal class ComponentPropertyPayload
    {
        public string name;
        public string type;
        public string value;
    }

    [Serializable]
    internal class ComponentListResultPayload
    {
        public List<ComponentSummaryPayload> components = new();
    }

    [Serializable]
    internal class ComponentSummaryPayload
    {
        public string componentType;
        public int componentIndex;
        public bool enabled;
    }

    // ── M10 Component Service ─────────────────────────────────────────────────

    internal sealed class UnityPilotComponentService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotComponentService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("component.add",    HandleComponentAddAsync);
            _bridge.Router.Register("component.remove", HandleComponentRemoveAsync);
            _bridge.Router.Register("component.get",    HandleComponentGetAsync);
            _bridge.Router.Register("component.modify", HandleComponentModifyAsync);
            _bridge.Router.Register("component.list",   HandleComponentListAsync);
        }

        // ── component.add ─────────────────────────────────────────────────────

        private async Task HandleComponentAddAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ComponentAddMessage>(json);
            var goId = msg?.payload?.gameObjectId ?? 0;
            var typeName = msg?.payload?.componentType ?? "";

            if (string.IsNullOrEmpty(typeName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "componentType 不能为空", token, "component.add");
                return;
            }

            var tcs = new TaskCompletionSource<ComponentInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var go = UnityPilotGameObjectService.FindByInstanceId(goId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到指定的 GameObject (ID: {goId})"));
                        return;
                    }

                    var resolvedType = ResolveComponentType(typeName);
                    if (resolvedType == null)
                    {
                        tcs.TrySetException(new Exception($"无法找到组件类型: {typeName}"));
                        return;
                    }

                    var comp = Undo.AddComponent(go, resolvedType);
                    if (comp == null)
                    {
                        tcs.TrySetException(new Exception($"添加组件失败: {typeName}"));
                        return;
                    }

                    var index = GetComponentIndex(go, comp);
                    tcs.TrySetResult(BuildComponentInfo(comp, index));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "component.add", result, token);
                _bridge.AddLog("info", $"[Component] 添加 {typeName} 到 GameObject({goId})");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "COMPONENT_ADD_FAILED", ex.Message, token, "component.add");
            }
        }

        // ── component.remove ──────────────────────────────────────────────────

        private async Task HandleComponentRemoveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ComponentRemoveMessage>(json);
            var goId = msg?.payload?.gameObjectId ?? 0;
            var typeName = msg?.payload?.componentType ?? "";
            var compIndex = msg?.payload?.componentIndex ?? 0;

            if (string.IsNullOrEmpty(typeName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "componentType 不能为空", token, "component.remove");
                return;
            }

            var tcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var go = UnityPilotGameObjectService.FindByInstanceId(goId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到指定的 GameObject (ID: {goId})"));
                        return;
                    }

                    // Check if trying to remove Transform
                    if (typeName.Equals("Transform", StringComparison.OrdinalIgnoreCase) ||
                        typeName.Equals("RectTransform", StringComparison.OrdinalIgnoreCase))
                    {
                        tcs.TrySetException(new Exception("Transform 组件无法被移除"));
                        return;
                    }

                    var comp = FindComponentByTypeAndIndex(go, typeName, compIndex);
                    if (comp == null)
                    {
                        tcs.TrySetException(new Exception($"组件不存在: {typeName}[{compIndex}]"));
                        return;
                    }

                    Undo.DestroyObjectImmediate(comp);
                    tcs.TrySetResult(new GenericOkPayload { ok = true });
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "component.remove", result, token);
                _bridge.AddLog("info", $"[Component] 移除 {typeName}[{compIndex}] 从 GameObject({goId})");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "COMPONENT_REMOVE_FAILED", ex.Message, token, "component.remove");
            }
        }

        // ── component.get ─────────────────────────────────────────────────────

        private async Task HandleComponentGetAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ComponentGetMessage>(json);
            var goId = msg?.payload?.gameObjectId ?? 0;
            var typeName = msg?.payload?.componentType ?? "";
            var compIndex = msg?.payload?.componentIndex ?? 0;

            if (string.IsNullOrEmpty(typeName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "componentType 不能为空", token, "component.get");
                return;
            }

            var tcs = new TaskCompletionSource<ComponentInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var go = UnityPilotGameObjectService.FindByInstanceId(goId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到指定的 GameObject (ID: {goId})"));
                        return;
                    }

                    var comp = FindComponentByTypeAndIndex(go, typeName, compIndex);
                    if (comp == null)
                    {
                        tcs.TrySetException(new Exception($"组件不存在: {typeName}[{compIndex}]"));
                        return;
                    }

                    tcs.TrySetResult(BuildComponentInfo(comp, compIndex));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "component.get", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "COMPONENT_GET_FAILED", ex.Message, token, "component.get");
            }
        }

        // ── component.modify ──────────────────────────────────────────────────

        private async Task HandleComponentModifyAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ComponentModifyMessage>(json);
            var goId = msg?.payload?.gameObjectId ?? 0;
            var typeName = msg?.payload?.componentType ?? "";
            var propsJson = msg?.payload?.properties ?? "{}";
            var compIndex = msg?.payload?.componentIndex ?? 0;

            if (string.IsNullOrEmpty(typeName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "componentType 不能为空", token, "component.modify");
                return;
            }

            var tcs = new TaskCompletionSource<ComponentInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var go = UnityPilotGameObjectService.FindByInstanceId(goId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到指定的 GameObject (ID: {goId})"));
                        return;
                    }

                    var comp = FindComponentByTypeAndIndex(go, typeName, compIndex);
                    if (comp == null)
                    {
                        tcs.TrySetException(new Exception($"组件不存在: {typeName}[{compIndex}]"));
                        return;
                    }

                    // Parse properties JSON — use a wrapper to get dict-like access
                    ApplyProperties(comp, propsJson);

                    tcs.TrySetResult(BuildComponentInfo(comp, compIndex));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "component.modify", result, token);
                _bridge.AddLog("info", $"[Component] 修改 {typeName}[{compIndex}] on GameObject({goId})");
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "COMPONENT_MODIFY_FAILED", ex.Message, token, "component.modify");
            }
        }

        // ── component.list ────────────────────────────────────────────────────

        private async Task HandleComponentListAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ComponentListMessage>(json);
            var goId = msg?.payload?.gameObjectId ?? 0;

            var tcs = new TaskCompletionSource<ComponentListResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var go = UnityPilotGameObjectService.FindByInstanceId(goId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到指定的 GameObject (ID: {goId})"));
                        return;
                    }

                    var comps = go.GetComponents<Component>();
                    var result = new ComponentListResultPayload();
                    var typeCount = new Dictionary<string, int>();

                    foreach (var c in comps)
                    {
                        if (c == null) continue; // Missing script
                        var cTypeName = c.GetType().Name;
                        if (!typeCount.ContainsKey(cTypeName))
                            typeCount[cTypeName] = 0;
                        var idx = typeCount[cTypeName];
                        typeCount[cTypeName] = idx + 1;

                        var summary = new ComponentSummaryPayload
                        {
                            componentType = cTypeName,
                            componentIndex = idx,
                            enabled = IsComponentEnabled(c),
                        };
                        result.components.Add(summary);
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
                await _bridge.SendResultAsync(id, "component.list", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "COMPONENT_LIST_FAILED", ex.Message, token, "component.list");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Resolve a component type name to System.Type.</summary>
        internal static Type ResolveComponentType(string typeName)
        {
            // Try direct lookup in UnityEngine
            var t = Type.GetType($"UnityEngine.{typeName}, UnityEngine");
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;

            // Try UnityEngine.UI
            t = Type.GetType($"UnityEngine.UI.{typeName}, UnityEngine.UI");
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;

            // Try UnityEditor
            t = Type.GetType($"UnityEditor.{typeName}, UnityEditor");
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var asmType in assembly.GetTypes())
                {
                    if ((asmType.Name == typeName || asmType.FullName == typeName)
                        && typeof(Component).IsAssignableFrom(asmType))
                    {
                        return asmType;
                    }
                }
            }

            return null;
        }

        /// <summary>Find a component on a GameObject by type name and index.</summary>
        internal static Component FindComponentByTypeAndIndex(GameObject go, string typeName, int index)
        {
            var comps = go.GetComponents<Component>();
            int matchIdx = 0;
            foreach (var c in comps)
            {
                if (c == null) continue;
                if (c.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                    || c.GetType().FullName == typeName)
                {
                    if (matchIdx == index)
                        return c;
                    matchIdx++;
                }
            }
            return null;
        }

        /// <summary>Get the index of a component among same-type components.</summary>
        private static int GetComponentIndex(GameObject go, Component target)
        {
            var comps = go.GetComponents<Component>();
            int idx = 0;
            var targetType = target.GetType();
            foreach (var c in comps)
            {
                if (c == null) continue;
                if (c == target) return idx;
                if (c.GetType() == targetType) idx++;
            }
            return 0;
        }

        /// <summary>Check if a component is enabled (for Behaviours and Renderers).</summary>
        private static bool IsComponentEnabled(Component c)
        {
            if (c is Behaviour b) return b.enabled;
            if (c is Renderer r) return r.enabled;
            if (c is Collider col) return col.enabled;
            return true; // Transform etc. are always "enabled"
        }

        /// <summary>Build a ComponentInfoPayload using SerializedObject.</summary>
        private static ComponentInfoPayload BuildComponentInfo(Component comp, int index)
        {
            var info = new ComponentInfoPayload
            {
                componentType = comp.GetType().Name,
                componentIndex = index,
                enabled = IsComponentEnabled(comp),
            };

            var so = new SerializedObject(comp);
            var prop = so.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                // Skip m_Script field
                if (prop.name == "m_Script") continue;

                info.properties.Add(new ComponentPropertyPayload
                {
                    name = prop.name,
                    type = prop.propertyType.ToString(),
                    value = GetSerializedPropertyValue(prop),
                });
            }

            return info;
        }

        /// <summary>Convert a SerializedProperty value to a string representation.</summary>
        private static string GetSerializedPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:       return prop.intValue.ToString();
                case SerializedPropertyType.Boolean:       return prop.boolValue.ToString().ToLower();
                case SerializedPropertyType.Float:         return prop.floatValue.ToString("G");
                case SerializedPropertyType.String:        return prop.stringValue;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return $"{{\"r\":{c.r:G},\"g\":{c.g:G},\"b\":{c.b:G},\"a\":{c.a:G}}}";
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "null";
                case SerializedPropertyType.Enum:          return prop.enumNames[prop.enumValueIndex];
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return $"{{\"x\":{v2.x:G},\"y\":{v2.y:G}}}";
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return $"{{\"x\":{v3.x:G},\"y\":{v3.y:G},\"z\":{v3.z:G}}}";
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return $"{{\"x\":{v4.x:G},\"y\":{v4.y:G},\"z\":{v4.z:G},\"w\":{v4.w:G}}}";
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return $"{{\"x\":{r.x:G},\"y\":{r.y:G},\"width\":{r.width:G},\"height\":{r.height:G}}}";
                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return $"{{\"x\":{q.x:G},\"y\":{q.y:G},\"z\":{q.z:G},\"w\":{q.w:G}}}";
                case SerializedPropertyType.LayerMask:     return prop.intValue.ToString();
                case SerializedPropertyType.ArraySize:     return prop.intValue.ToString();
                default:                                   return $"<{prop.propertyType}>";
            }
        }

        /// <summary>Apply properties from a JSON string to a component via SerializedObject.</summary>
        private static void ApplyProperties(Component comp, string propsJson)
        {
            if (string.IsNullOrEmpty(propsJson) || propsJson == "{}") return;

            var so = new SerializedObject(comp);
            Undo.RecordObject(comp, "Modify Component Properties");

            // Parse the JSON manually — JsonUtility doesn't support dict
            // We parse simple key:value pairs from the flat JSON
            var pairs = ParseSimpleJson(propsJson);

            foreach (var kvp in pairs)
            {
                var prop = so.FindProperty(kvp.Key);
                if (prop == null)
                {
                    Debug.LogWarning($"[UnityPilot] 属性 {kvp.Key} 不存在或不可写入");
                    continue;
                }

                SetSerializedPropertyValue(prop, kvp.Value);
            }

            so.ApplyModifiedProperties();
        }

        /// <summary>Set a SerializedProperty value from a string.</summary>
        private static void SetSerializedPropertyValue(SerializedProperty prop, string value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (int.TryParse(value, out var iv)) prop.intValue = iv;
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = value == "true" || value == "1" || value == "True";
                    break;
                case SerializedPropertyType.Float:
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var fv))
                        prop.floatValue = fv;
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value;
                    break;
                case SerializedPropertyType.Color:
                    TryParseColor(value, out var color);
                    prop.colorValue = color;
                    break;
                case SerializedPropertyType.Enum:
                    // Try by name first, then by index
                    var idx = Array.IndexOf(prop.enumNames, value);
                    if (idx >= 0) prop.enumValueIndex = idx;
                    else if (int.TryParse(value, out var ei)) prop.enumValueIndex = ei;
                    break;
                case SerializedPropertyType.Vector2:
                    TryParseVec2(value, out var vec2);
                    prop.vector2Value = vec2;
                    break;
                case SerializedPropertyType.Vector3:
                    TryParseVec3(value, out var vec3);
                    prop.vector3Value = vec3;
                    break;
                case SerializedPropertyType.Vector4:
                    TryParseVec4(value, out var vec4);
                    prop.vector4Value = vec4;
                    break;
                case SerializedPropertyType.LayerMask:
                    if (int.TryParse(value, out var lm)) prop.intValue = lm;
                    break;
                default:
                    Debug.LogWarning($"[UnityPilot] 不支持修改属性类型: {prop.propertyType} ({prop.name})");
                    break;
            }
        }

        // ── Simple JSON parser for flat key-value pairs ───────────────────────

        /// <summary>
        /// Parse a simple flat JSON object like {"key":"value","num":123} into key-value string pairs.
        /// Does NOT handle nested objects as values (they become raw strings).
        /// </summary>
        internal static Dictionary<string, string> ParseSimpleJson(string json)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(json)) return result;

            // Strip outer braces
            json = json.Trim();
            if (json.StartsWith("{")) json = json.Substring(1);
            if (json.EndsWith("}")) json = json.Substring(0, json.Length - 1);
            json = json.Trim();

            if (string.IsNullOrEmpty(json)) return result;

            int i = 0;
            while (i < json.Length)
            {
                // Skip whitespace and commas
                while (i < json.Length && (json[i] == ' ' || json[i] == ',' || json[i] == '\n' || json[i] == '\r' || json[i] == '\t'))
                    i++;
                if (i >= json.Length) break;

                // Parse key
                var key = ParseJsonString(json, ref i);
                if (key == null) break;

                // Skip colon
                while (i < json.Length && (json[i] == ' ' || json[i] == ':'))
                    i++;
                if (i >= json.Length) break;

                // Parse value
                var val = ParseJsonValue(json, ref i);
                if (val != null)
                    result[key] = val;
            }

            return result;
        }

        private static string ParseJsonString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"') return null;
            i++; // skip opening quote
            int start = i;
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\') i++; // skip escaped char
                i++;
            }
            var str = json.Substring(start, i - start);
            if (i < json.Length) i++; // skip closing quote
            return str;
        }

        private static string ParseJsonValue(string json, ref int i)
        {
            if (i >= json.Length) return null;

            // String value
            if (json[i] == '"')
                return ParseJsonString(json, ref i);

            // Object or array value — capture entire nested structure
            if (json[i] == '{' || json[i] == '[')
            {
                char open = json[i];
                char close = open == '{' ? '}' : ']';
                int depth = 1;
                int start = i;
                i++;
                while (i < json.Length && depth > 0)
                {
                    if (json[i] == open) depth++;
                    else if (json[i] == close) depth--;
                    i++;
                }
                return json.Substring(start, i - start);
            }

            // Number, bool, null
            int valStart = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']'
                   && json[i] != ' ' && json[i] != '\n' && json[i] != '\r')
                i++;
            return json.Substring(valStart, i - valStart);
        }

        // ── Vector/Color parsers ──────────────────────────────────────────────

        private static bool TryParseVec2(string s, out Vector2 v)
        {
            v = Vector2.zero;
            var d = ParseSimpleJson(s);
            if (d.ContainsKey("x") && float.TryParse(d["x"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x))
                v.x = x;
            if (d.ContainsKey("y") && float.TryParse(d["y"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
                v.y = y;
            return true;
        }

        private static bool TryParseVec3(string s, out Vector3 v)
        {
            v = Vector3.zero;
            var d = ParseSimpleJson(s);
            if (d.ContainsKey("x") && float.TryParse(d["x"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x))
                v.x = x;
            if (d.ContainsKey("y") && float.TryParse(d["y"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
                v.y = y;
            if (d.ContainsKey("z") && float.TryParse(d["z"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z))
                v.z = z;
            return true;
        }

        private static bool TryParseVec4(string s, out Vector4 v)
        {
            v = Vector4.zero;
            var d = ParseSimpleJson(s);
            if (d.ContainsKey("x") && float.TryParse(d["x"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x))
                v.x = x;
            if (d.ContainsKey("y") && float.TryParse(d["y"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
                v.y = y;
            if (d.ContainsKey("z") && float.TryParse(d["z"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z))
                v.z = z;
            if (d.ContainsKey("w") && float.TryParse(d["w"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var w))
                v.w = w;
            return true;
        }

        private static bool TryParseColor(string s, out Color c)
        {
            c = Color.white;
            var d = ParseSimpleJson(s);
            if (d.ContainsKey("r") && float.TryParse(d["r"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var r))
                c.r = r;
            if (d.ContainsKey("g") && float.TryParse(d["g"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var g))
                c.g = g;
            if (d.ContainsKey("b") && float.TryParse(d["b"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var b))
                c.b = b;
            if (d.ContainsKey("a") && float.TryParse(d["a"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var a))
                c.a = a;
            return true;
        }
    }
}

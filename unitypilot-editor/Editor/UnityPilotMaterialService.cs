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

    [Serializable] internal class MaterialCreateMessage   { public MaterialCreatePayload payload; }
    [Serializable] internal class MaterialCreatePayload   { public string materialPath = ""; public string shaderName = "Standard"; }

    [Serializable] internal class MaterialModifyMessage   { public MaterialModifyPayload payload; }
    [Serializable] internal class MaterialModifyPayload   { public string materialPath = ""; public string properties = ""; }

    [Serializable] internal class MaterialAssignMessage   { public MaterialAssignPayload payload; }
    [Serializable] internal class MaterialAssignPayload   { public int targetGameObjectId; public string materialPath = ""; public int materialIndex; }

    [Serializable] internal class MaterialGetMessage      { public MaterialGetPayload payload; }
    [Serializable] internal class MaterialGetPayload      { public string materialPath = ""; }

    [Serializable]
    internal class MaterialInfoPayload
    {
        public string materialPath;
        public string shaderName;
        public int    instanceId;
        public List<MaterialPropertyInfoPayload> properties = new List<MaterialPropertyInfoPayload>();
    }

    [Serializable]
    internal class MaterialPropertyInfoPayload
    {
        public string name;
        public string type;   // Float, Color, Vector, Texture, Int
        public string value;
    }

    [Serializable]
    internal class MaterialCreateResultPayload
    {
        public string materialPath;
        public int    instanceId;
        public string shaderName;
    }

    [Serializable]
    internal class ShaderListResultPayload
    {
        public List<string> shaders = new List<string>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotMaterialService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotMaterialService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("material.create",  HandleCreateAsync);
            _bridge.Router.Register("material.modify",  HandleModifyAsync);
            _bridge.Router.Register("material.assign",  HandleAssignAsync);
            _bridge.Router.Register("material.get",     HandleGetAsync);
            _bridge.Router.Register("shader.list",      HandleShaderListAsync);
        }

        // ── material.create ─────────────────────────────────────────────────────

        private async Task HandleCreateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MaterialCreateMessage>(json);
            var p   = msg?.payload ?? new MaterialCreatePayload();

            if (string.IsNullOrEmpty(p.materialPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "materialPath is required.", token, "material.create");
                return;
            }

            var tcs = new TaskCompletionSource<MaterialCreateResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var shader = Shader.Find(p.shaderName);
                    if (shader == null)
                    {
                        tcs.SetException(new Exception($"Shader not found: {p.shaderName}"));
                        return;
                    }

                    var mat = new Material(shader);
                    AssetDatabase.CreateAsset(mat, p.materialPath);
                    AssetDatabase.SaveAssets();

                    tcs.SetResult(new MaterialCreateResultPayload
                    {
                        materialPath = p.materialPath,
                        instanceId   = mat.GetInstanceID(),
                        shaderName   = shader.name,
                    });
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "material.create", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MATERIAL_CREATE_FAILED", ex.Message, token, "material.create");
            }
        }

        // ── material.modify ─────────────────────────────────────────────────────

        private async Task HandleModifyAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MaterialModifyMessage>(json);
            var p   = msg?.payload ?? new MaterialModifyPayload();

            if (string.IsNullOrEmpty(p.materialPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "materialPath is required.", token, "material.modify");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(p.materialPath);
                    if (mat == null)
                    {
                        tcs.SetException(new Exception($"Material not found at: {p.materialPath}"));
                        return;
                    }

                    // Parse properties JSON
                    if (!string.IsNullOrEmpty(p.properties))
                    {
                        var props = UnityPilotComponentService.ParseSimpleJson(p.properties);
                        foreach (var kv in props)
                        {
                            ApplyMaterialProperty(mat, kv.Key, kv.Value);
                        }
                    }

                    EditorUtility.SetDirty(mat);
                    AssetDatabase.SaveAssets();
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "material.modify", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MATERIAL_MODIFY_FAILED", ex.Message, token, "material.modify");
            }
        }

        // ── material.assign ─────────────────────────────────────────────────────

        private async Task HandleAssignAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MaterialAssignMessage>(json);
            var p   = msg?.payload ?? new MaterialAssignPayload();

            if (p.targetGameObjectId == 0 || string.IsNullOrEmpty(p.materialPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "targetGameObjectId and materialPath are required.", token, "material.assign");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var go = EditorUtility.InstanceIDToObject(p.targetGameObjectId) as GameObject;
                    if (go == null)
                    {
                        tcs.SetException(new Exception($"GameObject not found: {p.targetGameObjectId}"));
                        return;
                    }

                    var renderer = go.GetComponent<Renderer>();
                    if (renderer == null)
                    {
                        tcs.SetException(new Exception($"No Renderer component on: {go.name}"));
                        return;
                    }

                    var mat = AssetDatabase.LoadAssetAtPath<Material>(p.materialPath);
                    if (mat == null)
                    {
                        tcs.SetException(new Exception($"Material not found at: {p.materialPath}"));
                        return;
                    }

                    var mats = renderer.sharedMaterials;
                    if (p.materialIndex < 0 || p.materialIndex >= mats.Length)
                    {
                        tcs.SetException(new Exception($"Material index {p.materialIndex} out of range [0, {mats.Length - 1}]"));
                        return;
                    }

                    Undo.RecordObject(renderer, "Assign Material");
                    mats[p.materialIndex] = mat;
                    renderer.sharedMaterials = mats;

                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "material.assign", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MATERIAL_ASSIGN_FAILED", ex.Message, token, "material.assign");
            }
        }

        // ── material.get ────────────────────────────────────────────────────────

        private async Task HandleGetAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<MaterialGetMessage>(json);
            var p   = msg?.payload ?? new MaterialGetPayload();

            if (string.IsNullOrEmpty(p.materialPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "materialPath is required.", token, "material.get");
                return;
            }

            var tcs = new TaskCompletionSource<MaterialInfoPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(p.materialPath);
                    if (mat == null)
                    {
                        tcs.SetException(new Exception($"Material not found at: {p.materialPath}"));
                        return;
                    }

                    var info = new MaterialInfoPayload
                    {
                        materialPath = p.materialPath,
                        shaderName   = mat.shader != null ? mat.shader.name : "Unknown",
                        instanceId   = mat.GetInstanceID(),
                    };

                    // Read shader properties
                    if (mat.shader != null)
                    {
                        int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                        for (int i = 0; i < propCount; i++)
                        {
                            string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                            var propType = ShaderUtil.GetPropertyType(mat.shader, i);
                            string typeStr;
                            string valueStr;

                            switch (propType)
                            {
                                case ShaderUtil.ShaderPropertyType.Color:
                                    typeStr  = "Color";
                                    var c = mat.GetColor(propName);
                                    valueStr = $"({c.r},{c.g},{c.b},{c.a})";
                                    break;
                                case ShaderUtil.ShaderPropertyType.Vector:
                                    typeStr  = "Vector";
                                    var v = mat.GetVector(propName);
                                    valueStr = $"({v.x},{v.y},{v.z},{v.w})";
                                    break;
                                case ShaderUtil.ShaderPropertyType.Float:
                                case ShaderUtil.ShaderPropertyType.Range:
                                    typeStr  = "Float";
                                    valueStr = mat.GetFloat(propName).ToString("G");
                                    break;
                                case ShaderUtil.ShaderPropertyType.TexEnv:
                                    typeStr = "Texture";
                                    var tex = mat.GetTexture(propName);
                                    valueStr = tex != null ? AssetDatabase.GetAssetPath(tex) : "";
                                    break;
                                default:
                                    typeStr  = propType.ToString();
                                    valueStr = "";
                                    break;
                            }

                            info.properties.Add(new MaterialPropertyInfoPayload
                            {
                                name  = propName,
                                type  = typeStr,
                                value = valueStr,
                            });
                        }
                    }

                    tcs.SetResult(info);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "material.get", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "MATERIAL_GET_FAILED", ex.Message, token, "material.get");
            }
        }

        // ── shader.list ─────────────────────────────────────────────────────────

        private async Task HandleShaderListAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<ShaderListResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = new ShaderListResultPayload();

                    // Get all shader names available in the project
                    var shaderInfo = ShaderUtil.GetAllShaderInfo();
                    foreach (var si in shaderInfo)
                    {
                        if (!string.IsNullOrEmpty(si.name) && !si.name.StartsWith("Hidden/"))
                            result.shaders.Add(si.name);
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "shader.list", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SHADER_LIST_FAILED", ex.Message, token, "shader.list");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static void ApplyMaterialProperty(Material mat, string propName, string value)
        {
            if (!mat.HasProperty(propName)) return;

            // Try to detect property type
            int propIdx = -1;
            for (int i = 0; i < ShaderUtil.GetPropertyCount(mat.shader); i++)
            {
                if (ShaderUtil.GetPropertyName(mat.shader, i) == propName)
                {
                    propIdx = i;
                    break;
                }
            }

            if (propIdx < 0) return;

            var propType = ShaderUtil.GetPropertyType(mat.shader, propIdx);
            switch (propType)
            {
                case ShaderUtil.ShaderPropertyType.Color:
                    if (TryParseColor(value, out var color))
                        mat.SetColor(propName, color);
                    break;
                case ShaderUtil.ShaderPropertyType.Vector:
                    if (TryParseVector4(value, out var vec))
                        mat.SetVector(propName, vec);
                    break;
                case ShaderUtil.ShaderPropertyType.Float:
                case ShaderUtil.ShaderPropertyType.Range:
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out float f))
                        mat.SetFloat(propName, f);
                    break;
                case ShaderUtil.ShaderPropertyType.TexEnv:
                    var tex = AssetDatabase.LoadAssetAtPath<Texture>(value);
                    mat.SetTexture(propName, tex); // null clears it
                    break;
            }
        }

        private static bool TryParseColor(string s, out Color color)
        {
            color = Color.white;
            s = s.Trim().Trim('(', ')');
            var parts = s.Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float r)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float g)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float b)) return false;
            float a = 1f;
            if (parts.Length >= 4)
                float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out a);
            color = new Color(r, g, b, a);
            return true;
        }

        private static bool TryParseVector4(string s, out Vector4 vec)
        {
            vec = Vector4.zero;
            s = s.Trim().Trim('(', ')');
            var parts = s.Split(',');
            if (parts.Length < 2) return false;
            float x = 0, y = 0, z = 0, w = 0;
            if (parts.Length > 0) float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out x);
            if (parts.Length > 1) float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out y);
            if (parts.Length > 2) float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out z);
            if (parts.Length > 3) float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out w);
            vec = new Vector4(x, y, z, w);
            return true;
        }
    }
}

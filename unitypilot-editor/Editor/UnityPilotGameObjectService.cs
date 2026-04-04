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
    // ── M08 GameObject DTOs ─────────────────────────────────────────────────

    [Serializable]
    internal class GameObjectCreateMessage
    {
        public string id;
        public string type;
        public string name;
        public GameObjectCreatePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class GameObjectCreatePayload
    {
        public string name = "New GameObject";
        public int parentId;
        public string primitiveType = "";
    }

    [Serializable]
    internal class GameObjectFindMessage
    {
        public string id;
        public string type;
        public string name;
        public GameObjectFindPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class GameObjectFindPayload
    {
        public string name = "";
        public string tag = "";
        public int instanceId;
    }

    [Serializable]
    internal class GameObjectModifyMessage
    {
        public string id;
        public string type;
        public string name;
        public GameObjectModifyPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class GameObjectModifyPayload
    {
        public int instanceId;
        public string name;
        public string tag;
        public int layer = -1;        // -1 = not set
        public int activeSelf = -1;    // -1 = not set, 0 = false, 1 = true (JsonUtility lacks nullable bool)
        public int isStatic = -1;      // -1 = not set, 0 = false, 1 = true
        public int parentId;           // 0 = not set (no reparent)
    }

    [Serializable]
    internal class GameObjectDeleteMessage
    {
        public string id;
        public string type;
        public string name;
        public GameObjectDeletePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class GameObjectDeletePayload
    {
        public int instanceId;
    }

    [Serializable]
    internal class GameObjectMoveMessage
    {
        public string id;
        public string type;
        public string name;
        public GameObjectMovePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class GameObjectMovePayload
    {
        public int instanceId;
        public Vec3Payload position;
        public Vec3Payload rotation;
        public Vec3Payload scale;
    }

    [Serializable]
    internal class GameObjectDuplicateMessage
    {
        public string id;
        public string type;
        public string name;
        public GameObjectDuplicatePayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class GameObjectDuplicatePayload
    {
        public int instanceId;
    }

    [Serializable]
    internal class Vec3Payload
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    internal class GameObjectInfoPayload
    {
        public int instanceId;
        public string name;
        public string tag;
        public int layer;
        public bool activeSelf;
        public bool isStatic;
        public int parentId;
        public TransformPayload transform;
    }

    [Serializable]
    internal class TransformPayload
    {
        public Vec3Payload position;
        public Vec3Payload rotation;
        public Vec3Payload scale;
    }

    [Serializable]
    internal class GameObjectFindResultPayload
    {
        public List<GameObjectInfoPayload> gameObjects = new();
    }

    // ── M08 GameObject Service ──────────────────────────────────────────────

    internal sealed class UnityPilotGameObjectService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotGameObjectService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("gameobject.create", HandleCreateAsync);
            _bridge.Router.Register("gameobject.find", HandleFindAsync);
            _bridge.Router.Register("gameobject.modify", HandleModifyAsync);
            _bridge.Router.Register("gameobject.delete", HandleDeleteAsync);
            _bridge.Router.Register("gameobject.move", HandleMoveAsync);
            _bridge.Router.Register("gameobject.duplicate", HandleDuplicateAsync);
        }

        // ── gameobject.create ────────────────────────────────────────────────

        private async Task HandleCreateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<GameObjectCreateMessage>(json);
            var p = msg?.payload ?? new GameObjectCreatePayload();
            var goName = string.IsNullOrEmpty(p.name) ? "New GameObject" : p.name;

            var tcs = new TaskCompletionSource<GameObjectInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    GameObject go;

                    // Create from primitive type if specified
                    if (!string.IsNullOrEmpty(p.primitiveType) && TryParsePrimitiveType(p.primitiveType, out var pt))
                    {
                        go = GameObject.CreatePrimitive(pt);
                        go.name = goName;
                    }
                    else
                    {
                        go = new GameObject(goName);
                    }

                    // Set parent
                    if (p.parentId != 0)
                    {
                        var parent = FindByInstanceId(p.parentId);
                        if (parent != null)
                            go.transform.SetParent(parent.transform, false);
                    }

                    Undo.RegisterCreatedObjectUndo(go, "Create GameObject");
                    EditorUtility.SetDirty(go);

                    tcs.TrySetResult(BuildInfo(go));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var info = await tcs.Task;
                await _bridge.SendResultAsync(id, "gameobject.create", info, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"创建 GameObject 失败：{ex.Message}", token, "gameobject.create");
            }
        }

        // ── gameobject.find ──────────────────────────────────────────────────

        private async Task HandleFindAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<GameObjectFindMessage>(json);
            var p = msg?.payload ?? new GameObjectFindPayload();

            var tcs = new TaskCompletionSource<GameObjectFindResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = new GameObjectFindResultPayload();

                    // Find by instanceId (exact match, single result)
                    if (p.instanceId != 0)
                    {
                        var go = FindByInstanceId(p.instanceId);
                        if (go != null)
                            result.gameObjects.Add(BuildInfo(go));
                        tcs.TrySetResult(result);
                        return;
                    }

                    // Find by tag
                    if (!string.IsNullOrEmpty(p.tag))
                    {
                        try
                        {
                            var tagged = GameObject.FindGameObjectsWithTag(p.tag);
                            foreach (var go in tagged)
                            {
                                if (!string.IsNullOrEmpty(p.name) && !go.name.Contains(p.name))
                                    continue;
                                result.gameObjects.Add(BuildInfo(go));
                            }
                        }
                        catch
                        {
                            // Tag doesn't exist — return empty
                        }
                        tcs.TrySetResult(result);
                        return;
                    }

                    // Find by name (contains match across all loaded scenes)
                    if (!string.IsNullOrEmpty(p.name))
                    {
                        var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
                        foreach (var go in allObjects)
                        {
                            // Skip hidden/internal objects
                            if (go.hideFlags != HideFlags.None) continue;
                            // Skip prefab assets (only scene objects)
                            if (!go.scene.isLoaded && !go.scene.IsValid()) continue;

                            if (go.name.Contains(p.name))
                                result.gameObjects.Add(BuildInfo(go));
                        }
                    }

                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "gameobject.find", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"查找 GameObject 失败：{ex.Message}", token, "gameobject.find");
            }
        }

        // ── gameobject.modify ────────────────────────────────────────────────

        private async Task HandleModifyAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<GameObjectModifyMessage>(json);
            var p = msg?.payload ?? new GameObjectModifyPayload();

            if (p.instanceId == 0)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "instanceId 不能为 0", token, "gameobject.modify");
                return;
            }

            var tcs = new TaskCompletionSource<GameObjectInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var go = FindByInstanceId(p.instanceId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到 instanceId={p.instanceId} 的对象"));
                        return;
                    }

                    Undo.RecordObject(go, "Modify GameObject");

                    if (!string.IsNullOrEmpty(p.name))
                        go.name = p.name;

                    if (!string.IsNullOrEmpty(p.tag))
                    {
                        try { go.tag = p.tag; }
                        catch { /* tag doesn't exist, ignore */ }
                    }

                    if (p.layer >= 0 && p.layer <= 31)
                        go.layer = p.layer;

                    if (p.activeSelf >= 0)
                        go.SetActive(p.activeSelf == 1);

                    if (p.isStatic >= 0)
                        go.isStatic = p.isStatic == 1;

                    if (p.parentId != 0)
                    {
                        var newParent = FindByInstanceId(p.parentId);
                        if (newParent != null && newParent != go && !newParent.transform.IsChildOf(go.transform))
                        {
                            Undo.SetTransformParent(go.transform, newParent.transform, "Reparent GameObject");
                        }
                    }

                    EditorUtility.SetDirty(go);
                    tcs.TrySetResult(BuildInfo(go));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var info = await tcs.Task;
                await _bridge.SendResultAsync(id, "gameobject.modify", info, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"修改 GameObject 失败：{ex.Message}", token, "gameobject.modify");
            }
        }

        // ── gameobject.delete ────────────────────────────────────────────────

        private async Task HandleDeleteAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<GameObjectDeleteMessage>(json);
            var p = msg?.payload ?? new GameObjectDeletePayload();

            if (p.instanceId == 0)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "instanceId 不能为 0", token, "gameobject.delete");
                return;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var go = FindByInstanceId(p.instanceId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到 instanceId={p.instanceId} 的对象"));
                        return;
                    }

                    Undo.DestroyObjectImmediate(go);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "gameobject.delete", new GenericOkPayload { ok = true }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"删除 GameObject 失败：{ex.Message}", token, "gameobject.delete");
            }
        }

        // ── gameobject.move ──────────────────────────────────────────────────

        private async Task HandleMoveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<GameObjectMoveMessage>(json);
            var p = msg?.payload ?? new GameObjectMovePayload();

            if (p.instanceId == 0)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "instanceId 不能为 0", token, "gameobject.move");
                return;
            }

            var tcs = new TaskCompletionSource<GameObjectInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var go = FindByInstanceId(p.instanceId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到 instanceId={p.instanceId} 的对象"));
                        return;
                    }

                    Undo.RecordObject(go.transform, "Move GameObject");

                    if (p.position != null)
                        go.transform.localPosition = new Vector3(p.position.x, p.position.y, p.position.z);

                    if (p.rotation != null)
                        go.transform.localEulerAngles = new Vector3(p.rotation.x, p.rotation.y, p.rotation.z);

                    if (p.scale != null)
                        go.transform.localScale = new Vector3(p.scale.x, p.scale.y, p.scale.z);

                    EditorUtility.SetDirty(go);
                    tcs.TrySetResult(BuildInfo(go));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var info = await tcs.Task;
                await _bridge.SendResultAsync(id, "gameobject.move", info, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"移动 GameObject 失败：{ex.Message}", token, "gameobject.move");
            }
        }

        // ── gameobject.duplicate ────────────────────────────────────────────

        private async Task HandleDuplicateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<GameObjectDuplicateMessage>(json);
            var p = msg?.payload ?? new GameObjectDuplicatePayload();

            if (p.instanceId == 0)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "instanceId 不能为 0", token, "gameobject.duplicate");
                return;
            }

            var tcs = new TaskCompletionSource<GameObjectInfoPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var go = FindByInstanceId(p.instanceId);
                    if (go == null)
                    {
                        tcs.TrySetException(new Exception($"未找到 instanceId={p.instanceId} 的对象"));
                        return;
                    }

                    var clone = UnityEngine.Object.Instantiate(go, go.transform.parent);
                    clone.name = go.name; // Instantiate appends "(Clone)", restore original name
                    Undo.RegisterCreatedObjectUndo(clone, "Duplicate GameObject");

                    // Place the clone right after the original in the hierarchy
                    int siblingIndex = go.transform.GetSiblingIndex();
                    clone.transform.SetSiblingIndex(siblingIndex + 1);

                    EditorUtility.SetDirty(clone);
                    tcs.TrySetResult(BuildInfo(clone));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var info = await tcs.Task;
                await _bridge.SendResultAsync(id, "gameobject.duplicate", info, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"复制 GameObject 失败：{ex.Message}", token, "gameobject.duplicate");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        internal static GameObject FindByInstanceId(int instanceId)
        {
            return EditorUtility.InstanceIDToObject(instanceId) as GameObject;
        }

        internal static GameObjectInfoPayload BuildInfo(GameObject go)
        {
            var t = go.transform;
            return new GameObjectInfoPayload
            {
                instanceId = go.GetInstanceID(),
                name = go.name,
                tag = go.tag,
                layer = go.layer,
                activeSelf = go.activeSelf,
                isStatic = go.isStatic,
                parentId = t.parent != null ? t.parent.gameObject.GetInstanceID() : 0,
                transform = new TransformPayload
                {
                    position = new Vec3Payload { x = t.localPosition.x, y = t.localPosition.y, z = t.localPosition.z },
                    rotation = new Vec3Payload { x = t.localEulerAngles.x, y = t.localEulerAngles.y, z = t.localEulerAngles.z },
                    scale = new Vec3Payload { x = t.localScale.x, y = t.localScale.y, z = t.localScale.z },
                },
            };
        }

        private static bool TryParsePrimitiveType(string value, out PrimitiveType result)
        {
            // Case-insensitive match
            switch (value.ToLowerInvariant())
            {
                case "cube":     result = PrimitiveType.Cube;     return true;
                case "sphere":   result = PrimitiveType.Sphere;   return true;
                case "capsule":  result = PrimitiveType.Capsule;  return true;
                case "cylinder": result = PrimitiveType.Cylinder; return true;
                case "plane":    result = PrimitiveType.Plane;    return true;
                case "quad":     result = PrimitiveType.Quad;     return true;
                default:         result = PrimitiveType.Cube;     return false;
            }
        }
    }
}

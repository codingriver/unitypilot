// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] internal class AssetFindMessage        { public AssetFindPayload payload; }
    [Serializable] internal class AssetFindPayload        { public string query = ""; public string assetType = ""; }

    [Serializable] internal class AssetCreateFolderMessage  { public AssetCreateFolderPayload payload; }
    [Serializable] internal class AssetCreateFolderPayload  { public string parentFolder = ""; public string newFolderName = ""; }

    [Serializable] internal class AssetPathPairMessage      { public AssetPathPairPayload payload; }
    [Serializable] internal class AssetPathPairPayload      { public string sourcePath = ""; public string destinationPath = ""; }

    [Serializable] internal class AssetSinglePathMessage    { public AssetSinglePathPayload payload; }
    [Serializable] internal class AssetSinglePathPayload    { public string assetPath = ""; }

    [Serializable]
    internal class AssetInfoPayload
    {
        public string assetPath;
        public string assetType;
        public string guid;
        public long   fileSize;
        public long   lastModified;
        public string name;
    }

    [Serializable] internal class AssetFindResultPayload { public List<AssetInfoPayload> assets = new List<AssetInfoPayload>(); }
    [Serializable] internal class AssetFolderResultPayload { public string folderPath; }

    [Serializable] internal class AssetGetDataMessage { public AssetGetDataPayload payload; }
    [Serializable] internal class AssetGetDataPayload { public string assetPath = ""; public int gameObjectId; public string componentType = ""; public int componentIndex; public int maxDepth = 10; }

    [Serializable] internal class AssetModifyDataMessage { public AssetModifyDataPayload payload; }
    [Serializable] internal class AssetModifyDataPayload { public string assetPath = ""; public int gameObjectId; public string componentType = ""; public int componentIndex; public List<SerializedPropertyWrite> properties = new List<SerializedPropertyWrite>(); }

    [Serializable] internal class SerializedPropertyWrite { public string propertyPath = ""; public string value = ""; }

    [Serializable] internal class SerializedPropertyInfo { public string propertyPath; public string type; public string value; public int depth; public bool hasChildren; public bool isArray; public int arraySize; }

    [Serializable] internal class AssetGetDataResultPayload { public string targetType; public List<SerializedPropertyInfo> properties = new List<SerializedPropertyInfo>(); }
    [Serializable] internal class AssetModifyDataResultPayload { public bool ok; public int modifiedCount; public List<string> errors = new List<string>(); }

    [Serializable] internal class AssetFindBuiltInMessage  { public AssetFindBuiltInPayload payload; }
    [Serializable] internal class AssetFindBuiltInPayload  { public string query = ""; public string assetType = ""; }

    [Serializable]
    internal class BuiltInAssetInfoPayload
    {
        public string name;
        public string assetType;
        public string source;
    }

    [Serializable]
    internal class AssetFindBuiltInResultPayload
    {
        public List<BuiltInAssetInfoPayload> assets = new List<BuiltInAssetInfoPayload>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotAssetService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotAssetService(UnityPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("asset.find",         HandleFindAsync);
            _bridge.Router.Register("asset.createFolder",  HandleCreateFolderAsync);
            _bridge.Router.Register("asset.copy",          HandleCopyAsync);
            _bridge.Router.Register("asset.move",          HandleMoveAsync);
            _bridge.Router.Register("asset.delete",        HandleDeleteAsync);
            _bridge.Router.Register("asset.refresh",       HandleRefreshAsync);
            _bridge.Router.Register("asset.getInfo",       HandleGetInfoAsync);
            _bridge.Router.Register("asset.getData",       HandleGetDataAsync);
            _bridge.Router.Register("asset.modifyData",    HandleModifyDataAsync);
            _bridge.Router.Register("asset.findBuiltIn",   HandleFindBuiltInAsync);
        }

        // ── asset.find ──────────────────────────────────────────────────────────

        private async Task HandleFindAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetFindMessage>(json);
            var p   = msg?.payload ?? new AssetFindPayload();

            if (string.IsNullOrEmpty(p.query))
            {
                await _bridge.SendErrorAsync(id, "INVALID_QUERY", "Query string is empty.", token, "asset.find");
                return;
            }

            var tcs = new TaskCompletionSource<AssetFindResultPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    // Build search filter
                    string filter = p.query;
                    if (!string.IsNullOrEmpty(p.assetType))
                        filter += " t:" + p.assetType;

                    string[] guids = AssetDatabase.FindAssets(filter);
                    var result = new AssetFindResultPayload();

                    foreach (var guid in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        result.assets.Add(BuildAssetInfo(path, guid));
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.find", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_FIND_FAILED", ex.Message, token, "asset.find");
            }
        }

        // ── asset.createFolder ──────────────────────────────────────────────────

        private async Task HandleCreateFolderAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetCreateFolderMessage>(json);
            var p   = msg?.payload ?? new AssetCreateFolderPayload();

            if (string.IsNullOrEmpty(p.parentFolder) || string.IsNullOrEmpty(p.newFolderName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "parentFolder and newFolderName are required.", token, "asset.createFolder");
                return;
            }

            var tcs = new TaskCompletionSource<string>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    string targetPath = p.parentFolder.TrimEnd('/') + "/" + p.newFolderName;
                    if (AssetDatabase.IsValidFolder(targetPath))
                    {
                        tcs.SetResult(targetPath); // already exists, return it
                        return;
                    }

                    string guid = AssetDatabase.CreateFolder(p.parentFolder, p.newFolderName);
                    if (string.IsNullOrEmpty(guid))
                    {
                        tcs.SetException(new Exception($"Failed to create folder: {targetPath}"));
                        return;
                    }

                    tcs.SetResult(AssetDatabase.GUIDToAssetPath(guid));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var folderPath = await tcs.Task;
                var payload = new AssetFolderResultPayload { folderPath = folderPath };
                await _bridge.SendResultAsync(id, "asset.createFolder", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "CREATE_FOLDER_FAILED", ex.Message, token, "asset.createFolder");
            }
        }

        // ── asset.copy ──────────────────────────────────────────────────────────

        private async Task HandleCopyAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetPathPairMessage>(json);
            var p   = msg?.payload ?? new AssetPathPairPayload();

            if (string.IsNullOrEmpty(p.sourcePath) || string.IsNullOrEmpty(p.destinationPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "sourcePath and destinationPath are required.", token, "asset.copy");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    if (!File.Exists(p.sourcePath) && !Directory.Exists(p.sourcePath))
                    {
                        tcs.SetException(new Exception($"Source does not exist: {p.sourcePath}"));
                        return;
                    }

                    bool ok = AssetDatabase.CopyAsset(p.sourcePath, p.destinationPath);
                    if (!ok)
                    {
                        tcs.SetException(new Exception($"CopyAsset failed: {p.sourcePath} -> {p.destinationPath}"));
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
                await _bridge.SendResultAsync(id, "asset.copy", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_COPY_FAILED", ex.Message, token, "asset.copy");
            }
        }

        // ── asset.move ──────────────────────────────────────────────────────────

        private async Task HandleMoveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetPathPairMessage>(json);
            var p   = msg?.payload ?? new AssetPathPairPayload();

            if (string.IsNullOrEmpty(p.sourcePath) || string.IsNullOrEmpty(p.destinationPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "sourcePath and destinationPath are required.", token, "asset.move");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    string err = AssetDatabase.MoveAsset(p.sourcePath, p.destinationPath);
                    if (!string.IsNullOrEmpty(err))
                    {
                        tcs.SetException(new Exception($"MoveAsset failed: {err}"));
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
                await _bridge.SendResultAsync(id, "asset.move", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_MOVE_FAILED", ex.Message, token, "asset.move");
            }
        }

        // ── asset.delete ────────────────────────────────────────────────────────

        private async Task HandleDeleteAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetSinglePathMessage>(json);
            var p   = msg?.payload ?? new AssetSinglePathPayload();

            if (string.IsNullOrEmpty(p.assetPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "assetPath is required.", token, "asset.delete");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    bool ok = AssetDatabase.DeleteAsset(p.assetPath);
                    if (!ok)
                    {
                        tcs.SetException(new Exception($"DeleteAsset failed: {p.assetPath}"));
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
                await _bridge.SendResultAsync(id, "asset.delete", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_DELETE_FAILED", ex.Message, token, "asset.delete");
            }
        }

        // ── asset.refresh ───────────────────────────────────────────────────────

        private async Task HandleRefreshAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    AssetDatabase.Refresh();
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.refresh", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_REFRESH_FAILED", ex.Message, token, "asset.refresh");
            }
        }

        // ── asset.getInfo ───────────────────────────────────────────────────────

        private async Task HandleGetInfoAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetSinglePathMessage>(json);
            var p   = msg?.payload ?? new AssetSinglePathPayload();

            if (string.IsNullOrEmpty(p.assetPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "assetPath is required.", token, "asset.getInfo");
                return;
            }

            var tcs = new TaskCompletionSource<AssetInfoPayload>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    string guid = AssetDatabase.AssetPathToGUID(p.assetPath);
                    if (string.IsNullOrEmpty(guid))
                    {
                        tcs.SetException(new Exception($"Asset not found: {p.assetPath}"));
                        return;
                    }

                    tcs.SetResult(BuildAssetInfo(p.assetPath, guid));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.getInfo", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_INFO_FAILED", ex.Message, token, "asset.getInfo");
            }
        }

        // ── asset.getData ───────────────────────────────────────────────────────

        private async Task HandleGetDataAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetGetDataMessage>(json);
            var p   = msg?.payload ?? new AssetGetDataPayload();

            var tcs = new TaskCompletionSource<AssetGetDataResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    SerializedObject so;
                    if (p.gameObjectId != 0)
                    {
                        var go = EditorUtility.InstanceIDToObject(p.gameObjectId) as GameObject;
                        if (go == null)
                            throw new Exception($"GameObject not found: {p.gameObjectId}");

                        if (string.IsNullOrEmpty(p.componentType))
                            throw new Exception("componentType is required when gameObjectId is provided.");

                        var comp = UnityPilotComponentService.FindComponentByTypeAndIndex(go, p.componentType, p.componentIndex);
                        if (comp == null)
                            throw new Exception($"Component not found: {p.componentType}[{p.componentIndex}]");

                        so = new SerializedObject(comp);
                    }
                    else if (!string.IsNullOrEmpty(p.assetPath))
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(p.assetPath);
                        if (asset == null)
                            throw new Exception($"Asset not found: {p.assetPath}");

                        so = new SerializedObject(asset);
                    }
                    else
                    {
                        throw new Exception("Either assetPath or gameObjectId+componentType must be provided.");
                    }

                    var result = new AssetGetDataResultPayload
                    {
                        targetType = so.targetObject != null ? so.targetObject.GetType().Name : "Unknown",
                    };

                    var iterator = so.GetIterator();
                    bool enterChildren = true;
                    while (iterator.NextVisible(enterChildren))
                    {
                        enterChildren = false;
                        var prop = iterator.Copy();
                        if (prop.depth > p.maxDepth)
                            continue;

                        result.properties.Add(new SerializedPropertyInfo
                        {
                            propertyPath = prop.propertyPath,
                            type = prop.propertyType.ToString(),
                            value = GetSerializedPropertyDisplayValue(prop),
                            depth = prop.depth,
                            hasChildren = prop.hasChildren,
                            isArray = prop.isArray,
                            arraySize = prop.isArray ? prop.arraySize : 0,
                        });
                    }

                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.getData", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_GET_DATA_FAILED", ex.Message, token, "asset.getData");
            }
        }

        // ── asset.modifyData ───────────────────────────────────────────────────

        private async Task HandleModifyDataAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetModifyDataMessage>(json);
            var p   = msg?.payload ?? new AssetModifyDataPayload();

            var tcs = new TaskCompletionSource<AssetModifyDataResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    SerializedObject so;
                    if (p.gameObjectId != 0)
                    {
                        var go = EditorUtility.InstanceIDToObject(p.gameObjectId) as GameObject;
                        if (go == null)
                            throw new Exception($"GameObject not found: {p.gameObjectId}");

                        if (string.IsNullOrEmpty(p.componentType))
                            throw new Exception("componentType is required when gameObjectId is provided.");

                        var comp = UnityPilotComponentService.FindComponentByTypeAndIndex(go, p.componentType, p.componentIndex);
                        if (comp == null)
                            throw new Exception($"Component not found: {p.componentType}[{p.componentIndex}]");

                        so = new SerializedObject(comp);
                    }
                    else if (!string.IsNullOrEmpty(p.assetPath))
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(p.assetPath);
                        if (asset == null)
                            throw new Exception($"Asset not found: {p.assetPath}");

                        so = new SerializedObject(asset);
                    }
                    else
                    {
                        throw new Exception("Either assetPath or gameObjectId+componentType must be provided.");
                    }

                    var result = new AssetModifyDataResultPayload { ok = true };

                    so.Update();
                    if (p.properties != null)
                    {
                        foreach (var write in p.properties)
                        {
                            if (write == null || string.IsNullOrEmpty(write.propertyPath))
                            {
                                result.errors.Add("Invalid property write: empty propertyPath.");
                                continue;
                            }

                            var prop = so.FindProperty(write.propertyPath);
                            if (prop == null)
                            {
                                result.errors.Add($"Property not found: {write.propertyPath}");
                                continue;
                            }

                            try
                            {
                                SetSerializedPropertyValue(prop, write.value ?? string.Empty);
                                result.modifiedCount++;
                            }
                            catch (Exception ex)
                            {
                                result.errors.Add($"{write.propertyPath}: {ex.Message}");
                            }
                        }
                    }

                    so.ApplyModifiedProperties();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.modifyData", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_MODIFY_DATA_FAILED", ex.Message, token, "asset.modifyData");
            }
        }

        // ── asset.findBuiltIn ───────────────────────────────────────────────────

        private async Task HandleFindBuiltInAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetFindBuiltInMessage>(json);
            var p   = msg?.payload ?? new AssetFindBuiltInPayload();

            var tcs = new TaskCompletionSource<AssetFindBuiltInResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = new AssetFindBuiltInResultPayload();
                    var sources = new[] { "Resources/unity_builtin_extra", "Library/unity default resources" };

                    foreach (var source in sources)
                    {
                        var allAssets = AssetDatabase.LoadAllAssetsAtPath(source);
                        if (allAssets == null) continue;

                        foreach (var asset in allAssets)
                        {
                            if (asset == null) continue;
                            string assetName = asset.name;
                            string typeName  = asset.GetType().Name;

                            // Filter by name (contains, case-insensitive)
                            if (!string.IsNullOrEmpty(p.query))
                            {
                                if (assetName.IndexOf(p.query, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                            }

                            // Filter by type (exact, case-insensitive)
                            if (!string.IsNullOrEmpty(p.assetType))
                            {
                                if (!string.Equals(typeName, p.assetType, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }

                            result.assets.Add(new BuiltInAssetInfoPayload
                            {
                                name      = assetName,
                                assetType = typeName,
                                source    = source,
                            });
                        }
                    }

                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.findBuiltIn", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_FIND_BUILTIN_FAILED", ex.Message, token, "asset.findBuiltIn");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static AssetInfoPayload BuildAssetInfo(string assetPath, string guid)
        {
            var info = new AssetInfoPayload
            {
                assetPath = assetPath,
                guid      = guid,
                name      = Path.GetFileName(assetPath),
            };

            var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            info.assetType = assetType != null ? assetType.Name : "Unknown";

            string fullPath = Path.GetFullPath(assetPath);
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                info.fileSize     = fi.Length;
                info.lastModified = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            }
            else if (Directory.Exists(fullPath))
            {
                info.assetType = "Folder";
            }

            return info;
        }

        private static string GetSerializedPropertyDisplayValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue.ToString();
                case SerializedPropertyType.Boolean:
                    return prop.boolValue.ToString();
                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return $"{c.r},{c.g},{c.b},{c.a}";
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return $"{v2.x},{v2.y}";
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return $"{v3.x},{v3.y},{v3.z}";
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return $"{v4.x},{v4.y},{v4.z},{v4.w}";
                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return $"{q.x},{q.y},{q.z},{q.w}";
                case SerializedPropertyType.Enum:
                    return prop.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null
                        ? $"{prop.objectReferenceValue.name} ({prop.objectReferenceValue.GetType().Name})"
                        : "null";
                case SerializedPropertyType.ArraySize:
                    return prop.intValue.ToString();
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return $"{r.x},{r.y},{r.width},{r.height}";
                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return $"center:{b.center.x},{b.center.y},{b.center.z};size:{b.size.x},{b.size.y},{b.size.z}";
                case SerializedPropertyType.LayerMask:
                    return prop.intValue.ToString();
                default:
                    return "(unsupported)";
            }
        }

        private static void SetSerializedPropertyValue(SerializedProperty prop, string value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = bool.Parse(value);
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value;
                    break;
                case SerializedPropertyType.Color:
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 4) throw new Exception("Color must be 'r,g,b,a'.");
                        prop.colorValue = new Color(
                            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 2) throw new Exception("Vector2 must be 'x,y'.");
                        prop.vector2Value = new Vector2(
                            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 3) throw new Exception("Vector3 must be 'x,y,z'.");
                        prop.vector3Value = new Vector3(
                            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 4) throw new Exception("Vector4 must be 'x,y,z,w'.");
                        prop.vector4Value = new Vector4(
                            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                    }
                case SerializedPropertyType.Enum:
                    prop.enumValueIndex = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = string.IsNullOrEmpty(value)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value);
                    break;
                case SerializedPropertyType.ArraySize:
                    prop.arraySize = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new Exception($"Unsupported property type: {prop.propertyType}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    internal sealed class UnityPilotDragDropService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotDragDropService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("dragdrop.execute", HandleDragDropExecuteAsync);
        }

        private async Task HandleDragDropExecuteAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<DragDropMessage>(json);
            var payload = msg?.payload ?? new DragDropPayload();

            var tcs = new TaskCompletionSource<DragDropResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = ExecuteDragDrop(payload);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var resultPayload = await tcs.Task;
                await _bridge.SendResultAsync(id, "dragdrop.execute", resultPayload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"执行拖放失败：{ex.Message}", token, "dragdrop.execute");
            }
        }

        private static DragDropResultPayload ExecuteDragDrop(DragDropPayload payload)
        {
            var source = UnityPilotPlayInputService.FindTargetWindow(payload.sourceWindow);
            if (source == null)
            {
                return new DragDropResultPayload
                {
                    ok = false,
                    state = $"SOURCE_WINDOW_NOT_AVAILABLE:{payload.sourceWindow}",
                    dragType = payload.dragType,
                    visualMode = DragAndDrop.visualMode.ToString(),
                };
            }

            var target = UnityPilotPlayInputService.FindTargetWindow(payload.targetWindow);
            if (target == null)
            {
                return new DragDropResultPayload
                {
                    ok = false,
                    state = $"TARGET_WINDOW_NOT_AVAILABLE:{payload.targetWindow}",
                    dragType = payload.dragType,
                    visualMode = DragAndDrop.visualMode.ToString(),
                };
            }

            var mods = UnityPilotPlayInputService.ParseModifiers(payload.modifiers);
            var from = new Vector2(payload.fromX, payload.fromY);
            var to = new Vector2(payload.toX, payload.toY);

            source.Focus();
            DragAndDrop.PrepareStartDrag();

            var dragType = (payload.dragType ?? "custom").ToLowerInvariant();
            switch (dragType)
            {
                case "asset":
                {
                    var paths = payload.assetPaths ?? Array.Empty<string>();
                    DragAndDrop.paths = paths;

                    var objects = new List<UnityEngine.Object>();
                    foreach (var path in paths)
                    {
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                        if (obj != null) objects.Add(obj);
                    }

                    DragAndDrop.objectReferences = objects.ToArray();
                    break;
                }
                case "gameobject":
                {
                    DragAndDrop.paths = Array.Empty<string>();

                    var ids = payload.gameObjectIds ?? Array.Empty<int>();
                    var objects = new List<UnityEngine.Object>();
                    foreach (var objectId in ids)
                    {
                        var go = EditorUtility.InstanceIDToObject(objectId) as GameObject;
                        if (go != null) objects.Add(go);
                    }

                    DragAndDrop.objectReferences = objects.ToArray();
                    break;
                }
                default:
                {
                    DragAndDrop.paths = Array.Empty<string>();
                    DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
                    if (!string.IsNullOrEmpty(payload.customData))
                        DragAndDrop.SetGenericData("customData", payload.customData);
                    dragType = "custom";
                    break;
                }
            }

            DragAndDrop.StartDrag($"unitypilot:{dragType}");

            source.SendEvent(new Event
            {
                type = EventType.MouseDown,
                mousePosition = from,
                button = 0,
                modifiers = mods,
            });

            source.SendEvent(new Event
            {
                type = EventType.MouseDrag,
                mousePosition = from,
                button = 0,
                modifiers = mods,
            });

            target.Focus();

            target.SendEvent(new Event
            {
                type = EventType.DragUpdated,
                mousePosition = to,
                button = 0,
                modifiers = mods,
            });

            target.SendEvent(new Event
            {
                type = EventType.DragPerform,
                mousePosition = to,
                button = 0,
                modifiers = mods,
            });

            return new DragDropResultPayload
            {
                ok = true,
                state = $"dragdrop:{dragType}:{payload.sourceWindow}->{payload.targetWindow}",
                dragType = dragType,
                visualMode = DragAndDrop.visualMode.ToString(),
            };
        }
    }
}

using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text;

namespace SkillEditor.Editor.UnityPilot
{
    internal sealed class UnityPilotUIToolkitService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotUIToolkitService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("uitoolkit.dump", HandleDumpAsync);
            _bridge.Router.Register("uitoolkit.query", HandleQueryAsync);
            _bridge.Router.Register("uitoolkit.event", HandleEventAsync);
        }

        private async Task HandleDumpAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<UIToolkitDumpMessage>(json);
            var payload = msg?.payload ?? new UIToolkitDumpPayload();

            var targetWindow = payload.targetWindow ?? string.Empty;
            var maxDepth = payload.maxDepth <= 0 ? 10 : payload.maxDepth;

            var tcs = new TaskCompletionSource<UIToolkitDumpResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = DumpVisualTree(targetWindow, maxDepth);
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
                await _bridge.SendResultAsync(id, "uitoolkit.dump", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"导出 UIToolkit 树失败：{ex.Message}", token, "uitoolkit.dump");
            }
        }

        private async Task HandleQueryAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<UIToolkitQueryMessage>(json);
            var payload = msg?.payload ?? new UIToolkitQueryPayload();

            var tcs = new TaskCompletionSource<UIToolkitQueryResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = QueryElements(payload);
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
                await _bridge.SendResultAsync(id, "uitoolkit.query", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"查询 UIToolkit 元素失败：{ex.Message}", token, "uitoolkit.query");
            }
        }

        private async Task HandleEventAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<UIToolkitEventMessage>(json);
            var payload = msg?.payload ?? new UIToolkitEventPayload();

            var eventType = (payload.eventType ?? string.Empty).Trim().ToLowerInvariant();
            var validEvent = eventType == "click" || eventType == "keydown" || eventType == "keyup" ||
                             eventType == "mousedown" || eventType == "mouseup" ||
                             eventType == "focus" || eventType == "blur";

            if (!validEvent)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", $"非法 eventType：{payload.eventType}", token, "uitoolkit.event");
                return;
            }

            var tcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = DispatchEvent(payload, eventType);
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
                await _bridge.SendResultAsync(id, "uitoolkit.event", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"派发 UIToolkit 事件失败：{ex.Message}", token, "uitoolkit.event");
            }
        }

        private static UIToolkitDumpResultPayload DumpVisualTree(string targetWindow, int maxDepth)
        {
            var result = new UIToolkitDumpResultPayload
            {
                ok = false,
                targetWindow = targetWindow,
            };

            var window = UnityPilotPlayInputService.FindTargetWindow(targetWindow);
            if (window == null) return result;

            var root = window.rootVisualElement;
            if (root == null) return result;

            var elements = BuildFlatTree(root, maxDepth);
            result.ok = true;
            result.totalElements = elements.Count;
            result.elements = elements;
            return result;
        }

        private static UIToolkitQueryResultPayload QueryElements(UIToolkitQueryPayload payload)
        {
            var result = new UIToolkitQueryResultPayload { ok = false };

            var window = UnityPilotPlayInputService.FindTargetWindow(payload.targetWindow ?? string.Empty);
            if (window == null) return result;

            var root = window.rootVisualElement;
            if (root == null) return result;

            var all = BuildFlatTree(root, int.MaxValue);
            foreach (var info in all)
            {
                if (!IsMatch(info, payload)) continue;
                result.matches.Add(info);
            }

            result.ok = true;
            result.matchCount = result.matches.Count;
            return result;
        }

        private static GenericOkPayload DispatchEvent(UIToolkitEventPayload payload, string eventType)
        {
            var window = UnityPilotPlayInputService.FindTargetWindow(payload.targetWindow ?? string.Empty);
            if (window == null)
                return new GenericOkPayload { ok = false, state = $"WINDOW_NOT_AVAILABLE:{payload.targetWindow}" };

            var root = window.rootVisualElement;
            if (root == null)
                return new GenericOkPayload { ok = false, state = $"NO_UITOOLKIT_ROOT:{payload.targetWindow}" };

            window.Focus();

            var target = ResolveTargetElement(root, payload.elementName, payload.elementIndex);
            if (target == null)
                return new GenericOkPayload { ok = false, state = "TARGET_ELEMENT_NOT_FOUND" };

            var mods = UnityPilotPlayInputService.ParseModifiers(payload.modifiers ?? Array.Empty<string>());
            var mousePos = new Vector2(payload.mouseX, payload.mouseY);

            switch (eventType)
            {
                case "click":
                    using (var evt = ClickEvent.GetPooled())
                    {
                        evt.target = target;
                        target.SendEvent(evt);
                    }
                    break;
                case "keydown":
                {
                    var imguiEvt = new Event
                    {
                        type = EventType.KeyDown,
                        keyCode = ParseKeyCode(payload.keyCode),
                        character = ParseCharacter(payload.character),
                        modifiers = mods,
                    };
                    using (var evt = KeyDownEvent.GetPooled(imguiEvt))
                    {
                        target.SendEvent(evt);
                    }
                    break;
                }
                case "keyup":
                {
                    var imguiEvt = new Event
                    {
                        type = EventType.KeyUp,
                        keyCode = ParseKeyCode(payload.keyCode),
                        character = ParseCharacter(payload.character),
                        modifiers = mods,
                    };
                    using (var evt = KeyUpEvent.GetPooled(imguiEvt))
                    {
                        target.SendEvent(evt);
                    }
                    break;
                }
                case "mousedown":
                {
                    var imguiEvt = new Event
                    {
                        type = EventType.MouseDown,
                        mousePosition = mousePos,
                        button = payload.mouseButton,
                        modifiers = mods,
                    };
                    using (var evt = MouseDownEvent.GetPooled(imguiEvt))
                    {
                        target.SendEvent(evt);
                    }
                    break;
                }
                case "mouseup":
                {
                    var imguiEvt = new Event
                    {
                        type = EventType.MouseUp,
                        mousePosition = mousePos,
                        button = payload.mouseButton,
                        modifiers = mods,
                    };
                    using (var evt = MouseUpEvent.GetPooled(imguiEvt))
                    {
                        target.SendEvent(evt);
                    }
                    break;
                }
                case "focus":
                    target.Focus();
                    break;
                case "blur":
                    target.Blur();
                    break;
            }

            return new GenericOkPayload { ok = true, state = $"{eventType}:{target.name}" };
        }

        private static List<UIToolkitElementInfo> BuildFlatTree(VisualElement root, int maxDepth)
        {
            var result = new List<UIToolkitElementInfo>();
            Flatten(root, -1, 0, maxDepth, result);
            return result;
        }

        private static void Flatten(VisualElement element, int parentIndex, int depth, int maxDepth,
            List<UIToolkitElementInfo> result)
        {
            if (element == null) return;

            var index = result.Count;
            result.Add(ToElementInfo(element, index, parentIndex, depth));

            if (depth >= maxDepth) return;

            foreach (var child in element.hierarchy.Children())
            {
                Flatten(child, index, depth + 1, maxDepth, result);
            }
        }

        private static UIToolkitElementInfo ToElementInfo(VisualElement element, int index, int parentIndex, int depth)
        {
            var rect = element.worldBound;
            var classes = string.Join(" ", element.GetClasses().ToArray());
            var text = element is TextElement textElement ? textElement.text : string.Empty;

            return new UIToolkitElementInfo
            {
                index = index,
                parentIndex = parentIndex,
                depth = depth,
                typeName = element.GetType().Name,
                name = element.name,
                classes = classes,
                worldBoundX = rect.x,
                worldBoundY = rect.y,
                worldBoundWidth = rect.width,
                worldBoundHeight = rect.height,
                visible = element.visible,
                enabled = element.enabledSelf,
                childCount = element.hierarchy.childCount,
                text = text,
            };
        }

        private static bool IsMatch(UIToolkitElementInfo info, UIToolkitQueryPayload payload)
        {
            if (!string.IsNullOrEmpty(payload.nameFilter) && info.name != payload.nameFilter)
                return false;

            if (!string.IsNullOrEmpty(payload.classFilter))
            {
                var classParts = (info.classes ?? string.Empty)
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (Array.IndexOf(classParts, payload.classFilter) < 0)
                    return false;
            }

            if (!string.IsNullOrEmpty(payload.typeFilter) && info.typeName != payload.typeFilter)
                return false;

            if (!string.IsNullOrEmpty(payload.textFilter))
            {
                if (string.IsNullOrEmpty(info.text) ||
                    info.text.IndexOf(payload.textFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }

        private static VisualElement ResolveTargetElement(VisualElement root, string elementName, int elementIndex)
        {
            if (!string.IsNullOrEmpty(elementName))
            {
                var byName = root.Q(name: elementName);
                if (byName != null) return byName;
            }

            if (elementIndex >= 0)
            {
                var all = new List<VisualElement>();
                CollectElements(root, all);
                if (elementIndex < all.Count)
                    return all[elementIndex];
            }

            return null;
        }

        private static void CollectElements(VisualElement element, List<VisualElement> all)
        {
            if (element == null) return;
            all.Add(element);
            foreach (var child in element.hierarchy.Children())
            {
                CollectElements(child, all);
            }
        }

        private static KeyCode ParseKeyCode(string keyCodeStr)
        {
            if (!string.IsNullOrEmpty(keyCodeStr) && Enum.TryParse<KeyCode>(keyCodeStr, true, out var keyCode))
                return keyCode;
            return KeyCode.None;
        }

        private static char ParseCharacter(string character)
        {
            if (string.IsNullOrEmpty(character)) return '\0';
            return character[0];
        }
    }
}

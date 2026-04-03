// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text;

namespace codingriver.unity.pilot
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
            _bridge.Router.Register("uitoolkit.scroll", HandleScrollAsync);
            _bridge.Router.Register("uitoolkit.setValue", HandleSetValueAsync);
            _bridge.Router.Register("uitoolkit.interact", HandleInteractAsync);
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

        private async Task HandleScrollAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<UIToolkitScrollMessage>(json);
            var payload = msg?.payload ?? new UIToolkitScrollPayload();

            var tcs = new TaskCompletionSource<UIToolkitScrollResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try { tcs.TrySetResult(PerformScroll(payload)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "uitoolkit.scroll", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"ScrollView 滚动失败：{ex.Message}", token, "uitoolkit.scroll");
            }
        }

        private static UIToolkitScrollResultPayload PerformScroll(UIToolkitScrollPayload payload)
        {
            var window = UnityPilotPlayInputService.FindTargetWindow(payload.targetWindow ?? string.Empty);
            if (window == null)
                return new UIToolkitScrollResultPayload { ok = false, state = $"WINDOW_NOT_AVAILABLE:{payload.targetWindow}" };

            var root = window.rootVisualElement;
            if (root == null)
                return new UIToolkitScrollResultPayload { ok = false, state = "NO_UITOOLKIT_ROOT" };

            // Find ScrollView — by element name, element index, or first ScrollView found
            ScrollView scrollView = null;
            if (!string.IsNullOrEmpty(payload.elementName))
                scrollView = root.Q<ScrollView>(name: payload.elementName);

            if (scrollView == null && payload.elementIndex >= 0)
            {
                var allSv = root.Query<ScrollView>().ToList();
                if (payload.elementIndex < allSv.Count)
                    scrollView = allSv[payload.elementIndex];
            }

            if (scrollView == null)
                scrollView = root.Q<ScrollView>();

            if (scrollView == null)
                return new UIToolkitScrollResultPayload { ok = false, state = "SCROLLVIEW_NOT_FOUND" };

            if (payload.mode == "delta")
            {
                scrollView.scrollOffset += new Vector2(payload.deltaX, payload.deltaY);
            }
            else
            {
                var offset = scrollView.scrollOffset;
                if (payload.scrollToX >= 0) offset.x = payload.scrollToX;
                if (payload.scrollToY >= 0) offset.y = payload.scrollToY;
                scrollView.scrollOffset = offset;
            }

            var final = scrollView.scrollOffset;
            return new UIToolkitScrollResultPayload
            {
                ok = true,
                state = "scrolled",
                scrollOffsetX = final.x,
                scrollOffsetY = final.y,
            };
        }

        private async Task HandleSetValueAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<UIToolkitSetValueMessage>(json);
            var payload = msg?.payload ?? new UIToolkitSetValuePayload();

            var tcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try { tcs.TrySetResult(PerformSetValue(payload)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "uitoolkit.setValue", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"设置元素值失败：{ex.Message}", token, "uitoolkit.setValue");
            }
        }

        private static GenericOkPayload PerformSetValue(UIToolkitSetValuePayload payload)
        {
            var window = UnityPilotPlayInputService.FindTargetWindow(payload.targetWindow ?? "");
            if (window == null)
                return new GenericOkPayload { ok = false, state = $"WINDOW_NOT_AVAILABLE:{payload.targetWindow}" };

            var root = window.rootVisualElement;
            if (root == null)
                return new GenericOkPayload { ok = false, state = "NO_UITOOLKIT_ROOT" };

            var target = ResolveTargetElement(root, payload.elementName, payload.elementIndex);
            if (target == null)
                return new GenericOkPayload { ok = false, state = "TARGET_ELEMENT_NOT_FOUND" };

            var v = payload.value ?? "";

            switch (target)
            {
                case TextField tf:
                    tf.value = v;
                    return new GenericOkPayload { ok = true, state = $"set:TextField:{v}" };
                case Toggle tg:
                    tg.value = v == "true" || v == "True" || v == "1";
                    return new GenericOkPayload { ok = true, state = $"set:Toggle:{tg.value}" };
                case SliderInt si:
                    if (int.TryParse(v, out var iv)) si.value = iv;
                    return new GenericOkPayload { ok = true, state = $"set:SliderInt:{si.value}" };
                case Slider sl:
                    if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv)) sl.value = fv;
                    return new GenericOkPayload { ok = true, state = $"set:Slider:{sl.value}" };
                case DropdownField dd:
                    dd.value = v;
                    return new GenericOkPayload { ok = true, state = $"set:DropdownField:{v}" };
                case IntegerField intf:
                    if (int.TryParse(v, out var ifv)) intf.value = ifv;
                    return new GenericOkPayload { ok = true, state = $"set:IntegerField:{intf.value}" };
                case FloatField ff:
                    if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ffv)) ff.value = ffv;
                    return new GenericOkPayload { ok = true, state = $"set:FloatField:{ff.value}" };
                case Foldout fo:
                    fo.value = v == "true" || v == "True" || v == "1";
                    return new GenericOkPayload { ok = true, state = $"set:Foldout:{fo.value}" };
                default:
                    return new GenericOkPayload { ok = false, state = $"UNSUPPORTED_ELEMENT_TYPE:{target.GetType().Name}" };
            }
        }

        private async Task HandleInteractAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<UIToolkitInteractMessage>(json);
            var payload = msg?.payload ?? new UIToolkitInteractPayload();

            var tcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try { tcs.TrySetResult(PerformInteract(payload)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "uitoolkit.interact", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"元素交互失败：{ex.Message}", token, "uitoolkit.interact");
            }
        }

        private static GenericOkPayload PerformInteract(UIToolkitInteractPayload payload)
        {
            var window = UnityPilotPlayInputService.FindTargetWindow(payload.targetWindow ?? "");
            if (window == null)
                return new GenericOkPayload { ok = false, state = $"WINDOW_NOT_AVAILABLE:{payload.targetWindow}" };

            var root = window.rootVisualElement;
            if (root == null)
                return new GenericOkPayload { ok = false, state = "NO_UITOOLKIT_ROOT" };

            window.Focus();

            var target = ResolveTargetElement(root, payload.elementName, payload.elementIndex);
            if (target == null)
                return new GenericOkPayload { ok = false, state = "TARGET_ELEMENT_NOT_FOUND" };

            var action = (payload.action ?? "click").ToLowerInvariant();

            switch (action)
            {
                case "click":
                    var center = target.worldBound.center;
                    var imguiDown = new Event { type = EventType.MouseDown, mousePosition = center, button = 0 };
                    using (var down = MouseDownEvent.GetPooled(imguiDown)) { target.SendEvent(down); }
                    var imguiUp = new Event { type = EventType.MouseUp, mousePosition = center, button = 0 };
                    using (var up = MouseUpEvent.GetPooled(imguiUp)) { target.SendEvent(up); }
                    using (var click = ClickEvent.GetPooled()) { click.target = target; target.SendEvent(click); }
                    return new GenericOkPayload { ok = true, state = $"clicked:{target.name}:{target.GetType().Name}" };
                case "focus":
                    target.Focus();
                    return new GenericOkPayload { ok = true, state = $"focused:{target.name}" };
                case "blur":
                    target.Blur();
                    return new GenericOkPayload { ok = true, state = $"blurred:{target.name}" };
                default:
                    return new GenericOkPayload { ok = false, state = $"UNKNOWN_ACTION:{action}" };
            }
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
            var localRect = element.localBound;
            var classes = string.Join(" ", element.GetClasses().ToArray());
            var text = element is TextElement textElement ? textElement.text : string.Empty;
            ExtractValue(element, out var value, out var valueType, out var interactable);

            var isFocused = false;
            try { isFocused = element.focusController?.focusedElement == element; } catch { }

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
                localBoundX = localRect.x,
                localBoundY = localRect.y,
                visible = element.visible,
                enabled = element.enabledSelf,
                childCount = element.hierarchy.childCount,
                text = text,
                value = value,
                valueType = valueType,
                interactable = interactable,
                isFocused = isFocused,
            };
        }

        private static void ExtractValue(VisualElement element, out string value, out string valueType, out bool interactable)
        {
            value = "";
            valueType = "";
            interactable = false;

            switch (element)
            {
                case TextField tf:
                    value = tf.value ?? "";
                    valueType = "string";
                    interactable = true;
                    break;
                case Toggle tg:
                    value = tg.value.ToString();
                    valueType = "bool";
                    interactable = true;
                    break;
                case SliderInt si:
                    value = si.value.ToString();
                    valueType = "int";
                    interactable = true;
                    break;
                case Slider sl:
                    value = sl.value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    valueType = "float";
                    interactable = true;
                    break;
                case MinMaxSlider mms:
                    value = $"{mms.minValue},{mms.maxValue}";
                    valueType = "minmax";
                    interactable = true;
                    break;
                case DropdownField dd:
                    value = dd.value ?? "";
                    valueType = "dropdown";
                    interactable = true;
                    break;
                case EnumField ef:
                    value = ef.value?.ToString() ?? "";
                    valueType = "enum";
                    interactable = true;
                    break;
                case Foldout fo:
                    value = fo.value.ToString();
                    valueType = "bool";
                    interactable = true;
                    break;
                case IntegerField intf:
                    value = intf.value.ToString();
                    valueType = "int";
                    interactable = true;
                    break;
                case FloatField ff:
                    value = ff.value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    valueType = "float";
                    interactable = true;
                    break;
                case ProgressBar pb:
                    value = pb.value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    valueType = "float";
                    break;
                case Button _:
                    valueType = "button";
                    interactable = true;
                    break;
                default:
                    if (element is TextElement te && !string.IsNullOrEmpty(te.text))
                    {
                        value = te.text;
                        valueType = "label";
                    }
                    break;
            }
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

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkillEditor.Editor.UnityPilot
{
    internal sealed class UnityPilotPlayInputService
    {
        /// <summary>Must be called from the main thread.</summary>
        public GenericOkPayload SetPlayMode(string action)
        {
            if (action == "play")
            {
                if (!EditorApplication.isPlaying)
                    EditorApplication.isPlaying = true;
                return new GenericOkPayload { ok = true, state = "play" };
            }

            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
            return new GenericOkPayload { ok = true, state = "edit" };
        }

        public PlayModeChangedPayload CurrentPlayModeChangedPayload()
        {
            if (EditorApplication.isPaused)
                return new PlayModeChangedPayload { state = "pause" };
            return new PlayModeChangedPayload { state = EditorApplication.isPlaying ? "play" : "edit" };
        }

        /// <summary>
        /// Injects a mouse event into the specified editor window.
        /// Auto-routes to UIToolkit synthetic events when the window has a rootVisualElement.
        /// Must be called from the main thread.
        /// </summary>
        public GenericOkPayload HandleMouseEvent(MouseEventPayload payload)
        {
            var window = FindTargetWindow(payload.targetWindow);
            if (window == null)
            {
                return new GenericOkPayload
                {
                    ok = false,
                    state = $"WINDOW_NOT_AVAILABLE:{payload.targetWindow}",
                };
            }

            window.Focus();

            int button = payload.button switch
            {
                "middle" => 2,
                "right" => 1,
                _ => 0,
            };

            var pos = new Vector2(payload.x, payload.y);
            var uiRoot = window.rootVisualElement;

            // S4: auto-coordinate from elementName
            if (!string.IsNullOrEmpty(payload.elementName) || payload.elementIndex >= 0)
            {
                if (uiRoot != null)
                {
                    VisualElement target = null;
                    if (!string.IsNullOrEmpty(payload.elementName))
                        target = uiRoot.Q(name: payload.elementName);
                    if (target == null && payload.elementIndex >= 0)
                    {
                        var allElems = new System.Collections.Generic.List<VisualElement>();
                        CollectAllElements(uiRoot, allElems);
                        if (payload.elementIndex < allElems.Count)
                            target = allElems[payload.elementIndex];
                    }
                    if (target != null)
                    {
                        var center = target.worldBound.center;
                        pos = center;
                    }
                }
            }

            var mods = ParseModifiers(payload.modifiers);

            // Auto-route: if window has UIToolkit rootVisualElement, use synthetic UIToolkit events
            if (uiRoot != null && uiRoot.childCount > 0)
            {
                var sent = SendUIToolkitMouseEvent(uiRoot, payload.action, pos, button, mods, payload.scrollDeltaX, payload.scrollDeltaY);
                if (sent)
                    return new GenericOkPayload { ok = true, state = $"{payload.action}:{payload.targetWindow}:uitoolkit" };
            }

            // Fallback: IMGUI SendEvent
            switch (payload.action)
            {
                case "down":
                    window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = pos, button = button, modifiers = mods });
                    break;
                case "up":
                    window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = pos, button = button, modifiers = mods });
                    break;
                case "drag":
                    window.SendEvent(new Event { type = EventType.MouseDrag, mousePosition = pos, button = button, modifiers = mods });
                    break;
                case "move":
                    window.SendEvent(new Event { type = EventType.MouseMove, mousePosition = pos, modifiers = mods });
                    break;
                case "click":
                    window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = pos, button = button, modifiers = mods });
                    window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = pos, button = button, modifiers = mods });
                    break;
                case "doubleclick":
                    window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = pos, button = button, clickCount = 2, modifiers = mods });
                    window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = pos, button = button, clickCount = 2, modifiers = mods });
                    break;
                case "scroll":
                    var delta = new Vector2(payload.scrollDeltaX, payload.scrollDeltaY);
                    window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = pos, delta = delta, modifiers = mods });
                    break;
                default:
                    return new GenericOkPayload { ok = false, state = $"unknown_action:{payload.action}" };
            }

            return new GenericOkPayload { ok = true, state = $"{payload.action}:{payload.targetWindow}" };
        }

        private static bool SendUIToolkitMouseEvent(VisualElement root, string action, Vector2 pos, int button, EventModifiers mods, float scrollDx, float scrollDy)
        {
            var imguiBase = new Event { mousePosition = pos, button = button, modifiers = mods };

            switch (action)
            {
                case "down":
                    imguiBase.type = EventType.MouseDown;
                    using (var evt = MouseDownEvent.GetPooled(imguiBase)) { root.panel.visualTree.SendEvent(evt); }
                    return true;
                case "up":
                    imguiBase.type = EventType.MouseUp;
                    using (var evt = MouseUpEvent.GetPooled(imguiBase)) { root.panel.visualTree.SendEvent(evt); }
                    return true;
                case "drag":
                case "move":
                    imguiBase.type = EventType.MouseMove;
                    using (var evt = MouseMoveEvent.GetPooled(imguiBase)) { root.panel.visualTree.SendEvent(evt); }
                    return true;
                case "click":
                    imguiBase.type = EventType.MouseDown;
                    using (var down = MouseDownEvent.GetPooled(imguiBase)) { root.panel.visualTree.SendEvent(down); }
                    imguiBase.type = EventType.MouseUp;
                    using (var up = MouseUpEvent.GetPooled(imguiBase)) { root.panel.visualTree.SendEvent(up); }
                    return true;
                case "doubleclick":
                    imguiBase.type = EventType.MouseDown;
                    imguiBase.clickCount = 2;
                    using (var down = MouseDownEvent.GetPooled(imguiBase)) { root.panel.visualTree.SendEvent(down); }
                    imguiBase.type = EventType.MouseUp;
                    using (var up = MouseUpEvent.GetPooled(imguiBase)) { root.panel.visualTree.SendEvent(up); }
                    return true;
                case "scroll":
                    imguiBase.type = EventType.ScrollWheel;
                    imguiBase.delta = new Vector2(scrollDx, scrollDy);
                    using (var evt = WheelEvent.GetPooled(imguiBase)) { root.panel.visualTree.SendEvent(evt); }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Parses string modifier names into Unity EventModifiers flags.
        /// Accepted values: shift, control/ctrl, alt, command/cmd
        /// </summary>
        internal static EventModifiers ParseModifiers(string[] modifiers)
        {
            if (modifiers == null || modifiers.Length == 0) return EventModifiers.None;

            var result = EventModifiers.None;
            foreach (var m in modifiers)
            {
                switch (m?.ToLowerInvariant())
                {
                    case "shift":
                        result |= EventModifiers.Shift;
                        break;
                    case "control":
                    case "ctrl":
                        result |= EventModifiers.Control;
                        break;
                    case "alt":
                        result |= EventModifiers.Alt;
                        break;
                    case "command":
                    case "cmd":
                        result |= EventModifiers.Command;
                        break;
                }
            }
            return result;
        }

        /// <summary>
        /// Finds an EditorWindow by alias, short type name, full type name, title, or instanceId.
        /// </summary>
        internal static EditorWindow FindTargetWindow(string targetWindow)
        {
            if (string.IsNullOrEmpty(targetWindow)) return null;

            // 1. Built-in aliases
            string aliased = targetWindow switch
            {
                "game"      => "GameView",
                "hierarchy" => "SceneHierarchyWindow",
                "inspector" => "InspectorWindow",
                "scene"     => "SceneView",
                "project"   => "ProjectBrowser",
                "console"   => "ConsoleWindow",
                _           => null,
            };

            if (aliased == "SceneView")
            {
                return SceneView.lastActiveSceneView
                    ?? SceneView.currentDrawingSceneView
                    ?? Resources.FindObjectsOfTypeAll<SceneView>().FirstOrDefault();
            }

            var all = Resources.FindObjectsOfTypeAll<EditorWindow>();

            // 2. Short type name (aliased or raw)
            var typeName = aliased ?? targetWindow;
            var byShort = all.FirstOrDefault(w => w.GetType().Name == typeName);
            if (byShort != null) return byShort;

            // 3. Full type name (e.g. "SkillEditor.Editor.SkillEditorWindow")
            var byFull = all.FirstOrDefault(w =>
                string.Equals(w.GetType().FullName, targetWindow, StringComparison.Ordinal));
            if (byFull != null) return byFull;

            // 4. Instance ID (numeric string)
            if (int.TryParse(targetWindow, out int instanceId))
            {
                var byId = all.FirstOrDefault(w => w.GetInstanceID() == instanceId);
                if (byId != null) return byId;
            }

            // 5. Exact title match
            var byTitle = all.FirstOrDefault(w =>
                string.Equals(w.titleContent?.text, targetWindow, StringComparison.Ordinal));
            if (byTitle != null) return byTitle;

            // 6. Case-insensitive title contains
            var byTitleContains = all.FirstOrDefault(w =>
                w.titleContent?.text != null &&
                w.titleContent.text.IndexOf(targetWindow, StringComparison.OrdinalIgnoreCase) >= 0);
            if (byTitleContains != null) return byTitleContains;

            // 7. Case-insensitive short type name contains (partial match)
            var byTypeContains = all.FirstOrDefault(w =>
                w.GetType().Name.IndexOf(targetWindow, StringComparison.OrdinalIgnoreCase) >= 0);
            return byTypeContains;
        }

        private static void CollectAllElements(VisualElement element, System.Collections.Generic.List<VisualElement> all)
        {
            if (element == null) return;
            all.Add(element);
            foreach (var child in element.hierarchy.Children())
                CollectAllElements(child, all);
        }
    }
}

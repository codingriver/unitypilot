using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

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

            // Focus the target window before injecting (per M05 §6 requirement)
            window.Focus();

            int button = payload.button switch
            {
                "middle" => 2,
                "right" => 1,
                _ => 0, // left
            };

            var pos = new Vector2(payload.x, payload.y);
            var mods = ParseModifiers(payload.modifiers);

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
                    // Expand click into down+up (per M05 §10 boundary spec)
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
        /// Finds an EditorWindow by targetWindow name.
        /// Accepted values: scene, game, hierarchy, inspector, project, console, or a custom type name.
        /// </summary>
        internal static EditorWindow FindTargetWindow(string targetWindow)
        {
            string typeName = targetWindow switch
            {
                "game"      => "GameView",
                "hierarchy" => "SceneHierarchyWindow",
                "inspector" => "InspectorWindow",
                "scene"     => "SceneView",
                "project"   => "ProjectBrowser",
                "console"   => "ConsoleWindow",
                _           => targetWindow,
            };

            // SceneView has a dedicated static accessor
            if (typeName == "SceneView")
            {
                return SceneView.lastActiveSceneView
                    ?? SceneView.currentDrawingSceneView
                    ?? Resources.FindObjectsOfTypeAll<SceneView>().FirstOrDefault();
            }

            // For other window types look up by class name
            return Resources
                .FindObjectsOfTypeAll<EditorWindow>()
                .FirstOrDefault(w => w.GetType().Name == typeName);
        }
    }
}

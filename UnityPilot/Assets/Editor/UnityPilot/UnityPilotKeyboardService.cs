using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    internal sealed class UnityPilotKeyboardService
    {
        private static readonly string[] SupportedActions = { "keydown", "keyup", "keypress", "type" };

        private readonly UnityPilotBridge _bridge;

        public UnityPilotKeyboardService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("keyboard.event", HandleKeyboardEventAsync);
        }

        private async Task HandleKeyboardEventAsync(string id, string json, CancellationToken token)
        {
            var command = JsonUtility.FromJson<KeyboardEventMessage>(json);
            var keyboardPayload = command?.payload ?? new KeyboardEventPayload();
            var action = keyboardPayload.action ?? string.Empty;

            if (Array.IndexOf(SupportedActions, action) < 0)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", $"非法键盘动作：{action}", token, "keyboard.event");
                return;
            }

            KeyCode parsedKeyCode = KeyCode.None;
            if (action != "type")
            {
                var keyCodeStr = keyboardPayload.keyCode ?? string.Empty;
                if (!Enum.TryParse<KeyCode>(keyCodeStr, true, out parsedKeyCode))
                {
                    await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", $"非法键盘键值：{keyCodeStr}", token, "keyboard.event");
                    return;
                }
            }

            var resultTcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = HandleKeyboardEvent(keyboardPayload, action, parsedKeyCode);
                    resultTcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    resultTcs.TrySetException(ex);
                }
            });

            try
            {
                var keyboardResult = await resultTcs.Task;
                await _bridge.SendResultAsync(id, "keyboard.event", keyboardResult, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"键盘事件执行失败：{ex.Message}", token, "keyboard.event");
            }
        }

        private static GenericOkPayload HandleKeyboardEvent(KeyboardEventPayload payload, string action, KeyCode parsedKeyCode)
        {
            var window = UnityPilotPlayInputService.FindTargetWindow(payload.targetWindow);
            if (window == null)
            {
                return new GenericOkPayload
                {
                    ok = false,
                    state = $"WINDOW_NOT_AVAILABLE:{payload.targetWindow}",
                };
            }

            window.Focus();

            var mods = UnityPilotPlayInputService.ParseModifiers(payload.modifiers);

            switch (action)
            {
                case "keydown":
                    SendKeyDown(window, parsedKeyCode, payload.character, mods);
                    break;
                case "keyup":
                    SendKeyUp(window, parsedKeyCode, payload.character, mods);
                    break;
                case "keypress":
                    SendKeyDown(window, parsedKeyCode, payload.character, mods);
                    SendKeyUp(window, parsedKeyCode, payload.character, mods);
                    break;
                case "type":
                    var text = payload.text ?? string.Empty;
                    foreach (var ch in text)
                    {
                        window.SendEvent(new Event
                        {
                            type = EventType.KeyDown,
                            keyCode = KeyCode.None,
                            character = ch,
                            modifiers = mods,
                        });
                        window.SendEvent(new Event
                        {
                            type = EventType.KeyUp,
                            keyCode = KeyCode.None,
                            character = ch,
                            modifiers = mods,
                        });
                    }
                    break;
            }

            return new GenericOkPayload { ok = true, state = $"{action}:{payload.targetWindow}" };
        }

        private static void SendKeyDown(EditorWindow window, KeyCode keyCode, char character, EventModifiers modifiers)
        {
            window.SendEvent(new Event
            {
                type = EventType.KeyDown,
                keyCode = keyCode,
                character = character,
                modifiers = modifiers,
            });
        }

        private static void SendKeyUp(EditorWindow window, KeyCode keyCode, char character, EventModifiers modifiers)
        {
            window.SendEvent(new Event
            {
                type = EventType.KeyUp,
                keyCode = keyCode,
                character = character,
                modifiers = modifiers,
            });
        }
    }
}

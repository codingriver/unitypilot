// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    // ── Editor DTOs ─────────────────────────────────────────────────────────

    [Serializable] internal class EditorUndoMessage       { public EditorUndoPayload payload; }
    [Serializable] internal class EditorUndoPayload       { public int steps = 1; }

    [Serializable] internal class EditorRedoMessage       { public EditorRedoPayload payload; }
    [Serializable] internal class EditorRedoPayload       { public int steps = 1; }

    [Serializable] internal class EditorExecuteCommandMessage  { public EditorExecuteCommandPayload payload; }
    [Serializable] internal class EditorExecuteCommandPayload  { public string commandName = ""; }

    [Serializable] internal class SceneViewNavigateMessage   { public SceneViewNavigatePayload payload; }
    [Serializable]
    internal class SceneViewNavigatePayload
    {
        public int lookAtInstanceId;           // LookAt a specific GameObject
        public Vec3Payload pivot;              // Set pivot directly
        public float size = -1;                // Camera size / zoom (-1 = not set)
        public Vec3Payload rotation;           // Euler angles for camera rotation
        public int orthographic = -1;          // -1 = not set, 0 = perspective, 1 = orthographic
        public int in2DMode = -1;              // -1 = not set, 0 = 3D, 1 = 2D
    }

    [Serializable]
    internal class SceneViewStatePayload
    {
        public Vec3Payload pivot;
        public Vec3Payload rotation;
        public float size;
        public bool orthographic;
        public bool in2DMode;
        public Vec3Payload cameraPosition;
    }

    // ── Editor Service ──────────────────────────────────────────────────────

    internal sealed class UnityPilotEditorService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotEditorService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("editor.undo",           HandleUndoAsync);
            _bridge.Router.Register("editor.redo",           HandleRedoAsync);
            _bridge.Router.Register("editor.executeCommand", HandleExecuteCommandAsync);
            _bridge.Router.Register("sceneview.navigate",    HandleSceneViewNavigateAsync);
        }

        // ── editor.undo ─────────────────────────────────────────────────────

        private async Task HandleUndoAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorUndoMessage>(json);
            var p = msg?.payload ?? new EditorUndoPayload();
            int steps = Mathf.Max(1, p.steps);

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    for (int i = 0; i < steps; i++)
                        Undo.PerformUndo();
                    tcs.TrySetResult(steps);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var count = await tcs.Task;
                await _bridge.SendResultAsync(id, "editor.undo", new GenericOkPayload { ok = true }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "UNDO_FAILED", ex.Message, token, "editor.undo");
            }
        }

        // ── editor.redo ─────────────────────────────────────────────────────

        private async Task HandleRedoAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorRedoMessage>(json);
            var p = msg?.payload ?? new EditorRedoPayload();
            int steps = Mathf.Max(1, p.steps);

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    for (int i = 0; i < steps; i++)
                        Undo.PerformRedo();
                    tcs.TrySetResult(steps);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var count = await tcs.Task;
                await _bridge.SendResultAsync(id, "editor.redo", new GenericOkPayload { ok = true }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "REDO_FAILED", ex.Message, token, "editor.redo");
            }
        }

        // ── editor.executeCommand ───────────────────────────────────────────

        private async Task HandleExecuteCommandAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorExecuteCommandMessage>(json);
            var p = msg?.payload ?? new EditorExecuteCommandPayload();

            if (string.IsNullOrEmpty(p.commandName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "commandName 不能为空", token, "editor.executeCommand");
                return;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    // EditorApplication.ExecuteMenuItem covers menu commands
                    // For non-menu commands, use EditorWindow.focusedWindow.SendEvent
                    bool result = EditorApplication.ExecuteMenuItem(p.commandName);
                    if (!result)
                    {
                        tcs.TrySetException(new Exception($"命令执行失败或不存在：{p.commandName}"));
                        return;
                    }
                    tcs.TrySetResult(true);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "editor.executeCommand", new GenericOkPayload { ok = true }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "EXECUTE_COMMAND_FAILED", ex.Message, token, "editor.executeCommand");
            }
        }

        // ── sceneview.navigate ──────────────────────────────────────────────

        private async Task HandleSceneViewNavigateAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SceneViewNavigateMessage>(json);
            var p = msg?.payload ?? new SceneViewNavigatePayload();

            var tcs = new TaskCompletionSource<SceneViewStatePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv == null)
                    {
                        tcs.TrySetException(new Exception("没有活跃的 SceneView 窗口"));
                        return;
                    }

                    // LookAt a specific GameObject
                    if (p.lookAtInstanceId != 0)
                    {
                        var go = EditorUtility.InstanceIDToObject(p.lookAtInstanceId) as GameObject;
                        if (go != null)
                        {
                            sv.LookAt(go.transform.position);
                        }
                        else
                        {
                            tcs.TrySetException(new Exception($"未找到 instanceId={p.lookAtInstanceId} 的对象"));
                            return;
                        }
                    }

                    // Set pivot
                    if (p.pivot != null)
                        sv.pivot = new Vector3(p.pivot.x, p.pivot.y, p.pivot.z);

                    // Set rotation
                    if (p.rotation != null)
                        sv.rotation = Quaternion.Euler(p.rotation.x, p.rotation.y, p.rotation.z);

                    // Set size
                    if (p.size >= 0)
                        sv.size = p.size;

                    // Set orthographic
                    if (p.orthographic >= 0)
                        sv.orthographic = p.orthographic == 1;

                    // Set 2D mode
                    if (p.in2DMode >= 0)
                        sv.in2DMode = p.in2DMode == 1;

                    sv.Repaint();

                    // Build result
                    var cam = sv.camera;
                    tcs.TrySetResult(new SceneViewStatePayload
                    {
                        pivot = new Vec3Payload { x = sv.pivot.x, y = sv.pivot.y, z = sv.pivot.z },
                        rotation = new Vec3Payload
                        {
                            x = sv.rotation.eulerAngles.x,
                            y = sv.rotation.eulerAngles.y,
                            z = sv.rotation.eulerAngles.z,
                        },
                        size = sv.size,
                        orthographic = sv.orthographic,
                        in2DMode = sv.in2DMode,
                        cameraPosition = cam != null
                            ? new Vec3Payload { x = cam.transform.position.x, y = cam.transform.position.y, z = cam.transform.position.z }
                            : new Vec3Payload(),
                    });
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "sceneview.navigate", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCENEVIEW_NAVIGATE_FAILED", ex.Message, token, "sceneview.navigate");
            }
        }
    }
}

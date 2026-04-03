using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable]
    internal class ScreenshotMessage
    {
        public ScreenshotPayload payload;
    }

    [Serializable]
    internal class ScreenshotPayload
    {
        public int    width   = 1280;
        public int    height  = 720;
        public string format  = "png";
        public int    quality = 75;
    }

    [Serializable]
    internal class ScreenshotCameraMessage
    {
        public ScreenshotCameraPayload payload;
    }

    [Serializable]
    internal class ScreenshotCameraPayload
    {
        public string cameraName = "";
        public int    width      = 1280;
        public int    height     = 720;
        public string format     = "png";
        public int    quality    = 75;
    }

    [Serializable]
    internal class ScreenshotResultPayload
    {
        public string imageData;   // Base64
        public int    width;
        public int    height;
        public string format;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    internal class UnityPilotScreenshotService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotScreenshotService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("screenshot.gameView",  HandleGameViewAsync);
            _bridge.Router.Register("screenshot.sceneView", HandleSceneViewAsync);
            _bridge.Router.Register("screenshot.camera",    HandleCameraAsync);
        }

        // ── screenshot.gameView ─────────────────────────────────────────────────

        private async Task HandleGameViewAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScreenshotMessage>(json);
            var p   = msg?.payload ?? new ScreenshotPayload();

            int w = Clamp(p.width,  1, 4096, 1280);
            int h = Clamp(p.height, 1, 4096, 720);
            string fmt = NormalizeFormat(p.format);
            int qual   = Clamp(p.quality, 1, 100, 75);

            var tcs = new TaskCompletionSource<string>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    // Render the Game view camera to a RenderTexture
                    var cam = GetGameViewCamera();
                    if (cam == null)
                    {
                        tcs.SetResult(null);
                        return;
                    }

                    string base64 = RenderCameraToBase64(cam, w, h, fmt, qual);
                    tcs.SetResult(base64);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            string result;
            try
            {
                result = await tcs.Task;
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.gameView");
                return;
            }

            if (result == null)
            {
                await _bridge.SendErrorAsync(id, "NO_CAMERA", "No camera found for Game view capture.", token, "screenshot.gameView");
                return;
            }

            var payload = new ScreenshotResultPayload { imageData = result, width = w, height = h, format = fmt };
            await _bridge.SendResultAsync(id, "screenshot.gameView", payload, token);
        }

        // ── screenshot.sceneView ────────────────────────────────────────────────

        private async Task HandleSceneViewAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScreenshotMessage>(json);
            var p   = msg?.payload ?? new ScreenshotPayload();

            int w = Clamp(p.width,  1, 4096, 1280);
            int h = Clamp(p.height, 1, 4096, 720);
            string fmt = NormalizeFormat(p.format);
            int qual   = Clamp(p.quality, 1, 100, 75);

            var tcs = new TaskCompletionSource<string>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView == null || sceneView.camera == null)
                    {
                        tcs.SetResult(null);
                        return;
                    }

                    string base64 = RenderCameraToBase64(sceneView.camera, w, h, fmt, qual);
                    tcs.SetResult(base64);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            string result;
            try
            {
                result = await tcs.Task;
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.sceneView");
                return;
            }

            if (result == null)
            {
                await _bridge.SendErrorAsync(id, "NO_SCENE_VIEW", "No active Scene view found.", token, "screenshot.sceneView");
                return;
            }

            var payload = new ScreenshotResultPayload { imageData = result, width = w, height = h, format = fmt };
            await _bridge.SendResultAsync(id, "screenshot.sceneView", payload, token);
        }

        // ── screenshot.camera ───────────────────────────────────────────────────

        private async Task HandleCameraAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScreenshotCameraMessage>(json);
            var p   = msg?.payload ?? new ScreenshotCameraPayload();

            string camName = p.cameraName ?? "";
            int w = Clamp(p.width,  1, 4096, 1280);
            int h = Clamp(p.height, 1, 4096, 720);
            string fmt = NormalizeFormat(p.format);
            int qual   = Clamp(p.quality, 1, 100, 75);

            var tcs = new TaskCompletionSource<string>();
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    Camera cam = null;
                    if (!string.IsNullOrEmpty(camName))
                    {
                        // Find camera by name in the scene
                        foreach (var c in Camera.allCameras)
                        {
                            if (string.Equals(c.name, camName, StringComparison.OrdinalIgnoreCase))
                            {
                                cam = c;
                                break;
                            }
                        }
                        if (cam == null)
                        {
                            // Also check disabled cameras via FindObjectsOfType(true)
                            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                            {
                                if (string.Equals(c.name, camName, StringComparison.OrdinalIgnoreCase))
                                {
                                    cam = c;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        cam = Camera.main;
                    }

                    if (cam == null)
                    {
                        tcs.SetResult(null);
                        return;
                    }

                    string base64 = RenderCameraToBase64(cam, w, h, fmt, qual);
                    tcs.SetResult(base64);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            string result;
            try
            {
                result = await tcs.Task;
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.camera");
                return;
            }

            if (result == null)
            {
                string errMsg = string.IsNullOrEmpty(camName)
                    ? "No main camera found in the scene."
                    : $"Camera '{camName}' not found.";
                await _bridge.SendErrorAsync(id, "CAMERA_NOT_FOUND", errMsg, token, "screenshot.camera");
                return;
            }

            var payload = new ScreenshotResultPayload { imageData = result, width = w, height = h, format = fmt };
            await _bridge.SendResultAsync(id, "screenshot.camera", payload, token);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>Render a camera to RenderTexture → Texture2D → Base64 string.</summary>
        private static string RenderCameraToBase64(Camera cam, int w, int h, string format, int quality)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 1;

            var prevRT  = cam.targetTexture;
            var prevActive = RenderTexture.active;

            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                byte[] bytes;
                if (format == "jpg")
                    bytes = tex.EncodeToJPG(quality);
                else
                    bytes = tex.EncodeToPNG();

                UnityEngine.Object.DestroyImmediate(tex);
                return Convert.ToBase64String(bytes);
            }
            finally
            {
                cam.targetTexture  = prevRT;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// <summary>Get the main camera used for the Game view.</summary>
        private static Camera GetGameViewCamera()
        {
            // Prefer Camera.main, then fall back to first enabled camera
            if (Camera.main != null) return Camera.main;
            var allCams = Camera.allCameras;
            return allCams.Length > 0 ? allCams[0] : null;
        }

        private static string NormalizeFormat(string fmt)
        {
            if (string.IsNullOrEmpty(fmt)) return "png";
            fmt = fmt.ToLowerInvariant().Trim();
            return fmt == "jpg" || fmt == "jpeg" ? "jpg" : "png";
        }

        private static int Clamp(int value, int min, int max, int defaultValue)
        {
            if (value <= 0) return defaultValue;
            return value < min ? min : (value > max ? max : value);
        }
    }
}

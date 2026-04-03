using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    /// <summary>
    /// 全窗口级布局诊断：每一区域的宽度、内容最大宽度、横向溢出检测；
    /// 供 MCP resource / tool 读取，用于自动化验收。
    /// </summary>
    [InitializeOnLoad]
    internal static class UnityPilotWindowDiagnostics
    {
        private const string DomainReloadTsKey = "UnityPilot.DomainReloadTimestamp";

        static UnityPilotWindowDiagnostics()
        {
            SessionState.SetInt(DomainReloadTsKey, (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000));
        }

        public static int DomainReloadEpoch => SessionState.GetInt(DomainReloadTsKey, 0);

        // ── Per-section snapshots ────────────────────────────────────────────

        public static float  WindowWidth;
        public static float  WindowHeight;
        public static int    ActiveTab;
        public static long   UpdatedUnixMs;
        public static bool   WindowOpen;

        public static readonly Dictionary<string, SectionSnapshot> Sections = new();

        public struct SectionSnapshot
        {
            public float DesiredWidth;
            public float AllocatedWidth;
            public bool  OverflowRisk;
        }

        public static void RecordWindow(float w, float h, int tab)
        {
            WindowWidth   = w;
            WindowHeight  = h;
            ActiveTab     = tab;
            WindowOpen    = true;
            UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static void RecordSection(string name, float desiredW, float allocatedW)
        {
            Sections[name] = new SectionSnapshot
            {
                DesiredWidth  = desiredW,
                AllocatedWidth = allocatedW,
                OverflowRisk  = desiredW > allocatedW + 1f,
            };
        }

        public static void OnWindowClosed()
        {
            WindowOpen = false;
            Sections.Clear();
        }

        // ── Health score ─────────────────────────────────────────────────────

        public static string ComputeHealthScore()
        {
            if (!WindowOpen) return "unknown";
            foreach (var kv in Sections)
            {
                if (kv.Value.OverflowRisk) return "fail";
            }
            return "ok";
        }

        // ── Code version (assembly) ──────────────────────────────────────────

        private static string _cachedCodeVersion;

        public static string CodeVersion
        {
            get
            {
                if (_cachedCodeVersion != null) return _cachedCodeVersion;
                var asm = typeof(UnityPilotWindowDiagnostics).Assembly;
                var name = asm.GetName();
                _cachedCodeVersion = $"{name.Name}@{name.Version}";
                return _cachedCodeVersion;
            }
        }

        // ── Editor window screenshot (Windows) ──────────────────────────────

        public static string CaptureEditorWindowBase64(string windowTitle)
        {
#if UNITY_EDITOR_WIN
            return CaptureEditorWindowWin(windowTitle);
#else
            return null;
#endif
        }

#if UNITY_EDITOR_WIN
        private static string CaptureEditorWindowWin(string windowTitle)
        {
            try
            {
                var win = UnityPilotPlayInputService.FindTargetWindow(windowTitle);
                if (win == null) return null;

                win.Focus();
                win.Repaint();

                var pos = win.position;
                int px = (int)pos.x;
                int py = (int)pos.y;
                int pw = Mathf.Max(1, (int)pos.width);
                int ph = Mathf.Max(1, (int)pos.height);

                Color[] pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(
                    new Vector2(px, py), pw, ph);

                var tex = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
                tex.SetPixels(pixels);
                tex.Apply();
                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);
                return Convert.ToBase64String(png);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityPilot] Editor window capture failed: {ex.Message}");
                return null;
            }
        }
#endif
    }
}

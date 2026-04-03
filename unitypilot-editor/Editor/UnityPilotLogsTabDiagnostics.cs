// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using UnityEngine;

namespace codingriver.unity.pilot
{
    /// <summary>
    /// 供 MCP resource 读取：诊断日志标签页的最近一次布局参数（主线程写入）。
    /// </summary>
    internal static class UnityPilotLogsTabDiagnostics
    {
        public static bool   SnapshotValid;
        public static int    ActiveTab;
        public static float  WindowWidth;
        public static float  ScrollViewportWidth;
        public static float  LabelMaxWidth;
        public static float  ScrollX;
        public static float  ScrollY;
        public static long   UpdatedUnixMs;

        public static void RecordLogsTab(float winW, float viewportW, float labelW, Vector2 scroll)
        {
            SnapshotValid         = true;
            ActiveTab             = 1;
            WindowWidth           = winW;
            ScrollViewportWidth   = viewportW;
            LabelMaxWidth         = labelW;
            ScrollX               = scroll.x;
            ScrollY               = scroll.y;
            UpdatedUnixMs         = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static void ClearNotOnLogsTab()
        {
            SnapshotValid = false;
            ActiveTab     = 0;
        }
    }
}

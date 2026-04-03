// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    /// <summary>
    /// 将 UnityPilot 编辑器日志写入项目根目录下 <c>logs/unitypilot-editor.log</c>。
    /// 每次打开 Unity 编辑器（新会话）首次写入前会清空并写入会话头；同一会话内脚本域重载不清空，继续追加。
    /// </summary>
    internal static class UnityPilotEditorLog
    {
        private const string SessionInitKey = "UnityPilot.EditorLog.SessionStarted";

        private static readonly object FileLock = new object();
        private static          bool   _sessionPrepared;

        public static string ProjectLogsDirectory
        {
            get
            {
                var root = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
                return Path.Combine(root, "logs");
            }
        }

        public static string LogFilePath => Path.Combine(ProjectLogsDirectory, "unitypilot-editor.log");

        private static void EnsureSessionLogFile()
        {
            lock (FileLock)
            {
                if (_sessionPrepared)
                    return;

                try
                {
                    Directory.CreateDirectory(ProjectLogsDirectory);

                    if (!SessionState.GetBool(SessionInitKey, false))
                    {
                        SessionState.SetBool(SessionInitKey, true);
                        var header = new StringBuilder();
                        header.AppendLine($"=== UnityPilot editor log — session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {Application.productName} ===");
                        File.WriteAllText(LogFilePath, header.ToString(), Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UnityPilot] 无法准备日志文件: {ex.Message}");
                }
                finally
                {
                    _sessionPrepared = true;
                }
            }
        }

        public static void AppendLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            EnsureSessionLogFile();
            lock (FileLock)
            {
                try
                {
                    Directory.CreateDirectory(ProjectLogsDirectory);
                    File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 避免日志失败反噬编辑器
                }
            }
        }

        internal static string FormatLine(in BridgeLogEntry e)
        {
            var lvl = string.IsNullOrEmpty(e.Level) ? "INFO" : e.Level.ToUpperInvariant();
            if (!e.IsWireStructured)
                return $"[{lvl}] {e.Time:yyyy-MM-dd HH:mm:ss.fff} | {e.Message}";

            var ts = e.WireEnvelopeUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(e.WireEnvelopeUnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                : e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff");

            var tag = e.WireDirection == "TX" ? "send" : "recv";
            var body = e.WireIsRaw ? UnityPilotWireJson.StripEnvelopeForDisplay(e.WireDetail) : e.WireDetail;
            return $"[{lvl}] {tag} {ts} | sessionId={e.WireSessionId} | name={e.WireName} | type={e.WireType} | id={e.WireId} | {body}";
        }

        public static void RevealLogFile()
        {
            try
            {
                EnsureSessionLogFile();
                Directory.CreateDirectory(ProjectLogsDirectory);
                if (!File.Exists(LogFilePath))
                    File.WriteAllText(LogFilePath, "", Encoding.UTF8);
                EditorUtility.RevealInFinder(LogFilePath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("UnityPilot", $"无法打开日志: {ex.Message}", "确定");
            }
        }
    }
}

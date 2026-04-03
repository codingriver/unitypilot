using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    // ── M07 Console DTOs ──────────────────────────────────────────────────────

    [Serializable]
    internal class ConsoleLogsGetMessage
    {
        public string id;
        public string type;
        public string name;
        public ConsoleLogsGetPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    internal class ConsoleLogsGetPayload
    {
        public string logType;
        public int count = 100;
    }

    [Serializable]
    internal class ConsoleLogEntry
    {
        public string logType;
        public string message;
        public string stackTrace;
        public long timestamp;
        public int count;
    }

    [Serializable]
    internal class ConsoleLogsResultPayload
    {
        public List<ConsoleLogEntry> logs = new();
        public int total;
    }

    // ── M07 Console Service ───────────────────────────────────────────────────

    internal sealed class UnityPilotConsoleService
    {
        private readonly UnityPilotBridge _bridge;

        public UnityPilotConsoleService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("console.logs.get", HandleConsoleLogsGetAsync);
            _bridge.Router.Register("console.clear", HandleConsoleClearAsync);
        }

        private async Task HandleConsoleLogsGetAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ConsoleLogsGetMessage>(json);
            var logType = msg?.payload?.logType ?? "";
            var count = msg?.payload?.count ?? 100;
            if (count <= 0) count = 1;
            if (count > 1000) count = 1000;

            var tcs = new TaskCompletionSource<ConsoleLogsResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    var result = GetConsoleLogs(logType, count);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "console.logs.get", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"读取控制台日志失败：{ex.Message}", token, "console.logs.get");
            }
        }

        private async Task HandleConsoleClearAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    ClearConsole();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "console.clear", new GenericOkPayload { ok = true }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"清空控制台失败：{ex.Message}", token, "console.clear");
            }
        }

        // ── Internal API via reflection on LogEntries ─────────────────────────

        private static ConsoleLogsResultPayload GetConsoleLogs(string logType, int count)
        {
            var result = new ConsoleLogsResultPayload();

            // Use internal LogEntries API
            var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
            if (logEntriesType == null)
            {
                // Fallback: try alternate name
                logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntries");
            }
            if (logEntriesType == null) return result;

            // Get total count
            var getCountMethod = logEntriesType.GetMethod("StartGettingEntries",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var endMethod = logEntriesType.GetMethod("EndGettingEntries",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            // Alternative: use GetCountsByType + row-based approach
            var getCount = logEntriesType.GetMethod("GetCount",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (getCount == null) return result;

            int totalCount = (int)getCount.Invoke(null, null);
            if (totalCount == 0) return result;

            // Start getting entries
            if (getCountMethod != null)
                getCountMethod.Invoke(null, null);

            try
            {
                // LogEntry type
                var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntryType == null)
                    logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntry");

                if (logEntryType == null || getEntryMethod == null)
                {
                    // Fallback: use GetEntryAtIndex or similar
                    return FallbackGetLogs(logEntriesType, logType, count, totalCount);
                }

                var entry = Activator.CreateInstance(logEntryType);
                var messageField = logEntryType.GetField("message") ?? logEntryType.GetField("condition");
                var modeField = logEntryType.GetField("mode");

                var collected = new List<ConsoleLogEntry>();

                for (int i = totalCount - 1; i >= 0 && collected.Count < count; i--)
                {
                    bool ok = (bool)getEntryMethod.Invoke(null, new object[] { i, entry });
                    if (!ok) continue;

                    int mode = modeField != null ? (int)modeField.GetValue(entry) : 0;
                    string entryType = ModeToLogType(mode);

                    if (!string.IsNullOrEmpty(logType) &&
                        !string.Equals(entryType, logType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string msg = messageField?.GetValue(entry)?.ToString() ?? "";
                    // Split message and stacktrace (Unity stores them together separated by \n)
                    string stackTrace = "";
                    int nlIndex = msg.IndexOf('\n');
                    if (nlIndex >= 0)
                    {
                        stackTrace = msg.Substring(nlIndex + 1);
                        msg = msg.Substring(0, nlIndex);
                    }

                    collected.Add(new ConsoleLogEntry
                    {
                        logType = entryType,
                        message = msg,
                        stackTrace = stackTrace,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        count = 1
                    });
                }

                result.logs = collected;
                result.total = collected.Count;
            }
            finally
            {
                if (endMethod != null)
                    endMethod.Invoke(null, null);
            }

            return result;
        }

        private static ConsoleLogsResultPayload FallbackGetLogs(System.Type logEntriesType, string logType, int count, int totalCount)
        {
            var result = new ConsoleLogsResultPayload();

            // Try ConsoleWindow approach
            var getEntryAtIndex = logEntriesType.GetMethod("GetEntryStringAtIndex",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (getEntryAtIndex == null) return result;

            for (int i = totalCount - 1; i >= 0 && result.logs.Count < count; i--)
            {
                string msg = getEntryAtIndex.Invoke(null, new object[] { i })?.ToString() ?? "";
                result.logs.Add(new ConsoleLogEntry
                {
                    logType = "Log",
                    message = msg,
                    stackTrace = "",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    count = 1
                });
            }
            result.total = result.logs.Count;
            return result;
        }

        private static string ModeToLogType(int mode)
        {
            // Unity LogEntry mode flags:
            // bit 0 = Error, bit 1 = Assert, bit 2 = Log, bit 3 = Fatal (Exception)
            // bit 4 = DontPreprocessCondition, bit 5 = AssetImportError, ...
            // bit 8 = ScriptingError, bit 9 = ScriptingWarning, bit 10 = ScriptingLog
            // bit 11 = ScriptCompileError, bit 12 = ScriptCompileWarning
            if ((mode & (1 << 0)) != 0) return "Error";
            if ((mode & (1 << 1)) != 0) return "Assert";
            if ((mode & (1 << 3)) != 0) return "Exception";
            if ((mode & (1 << 8)) != 0) return "Error";
            if ((mode & (1 << 11)) != 0) return "Error";
            if ((mode & (1 << 9)) != 0) return "Warning";
            if ((mode & (1 << 12)) != 0) return "Warning";
            return "Log";
        }

        private static void ClearConsole()
        {
            var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
            if (logEntriesType == null)
                logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntries");
            if (logEntriesType == null) return;

            var clearMethod = logEntriesType.GetMethod("Clear",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            clearMethod?.Invoke(null, null);
        }
    }
}

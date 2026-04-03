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
        private const int RingBufferCapacity = 500;

        private static readonly object _ringLock = new();
        private static readonly List<ConsoleLogEntry> _ringBuffer = new(RingBufferCapacity);
        private static bool _subscribed;

        private readonly UnityPilotBridge _bridge;

        public UnityPilotConsoleService(UnityPilotBridge bridge)
        {
            _bridge = bridge;
            EnsureLogSubscription();
        }

        private static void EnsureLogSubscription()
        {
            if (_subscribed) return;
            _subscribed = true;
            UnityEngine.Application.logMessageReceived += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, UnityEngine.LogType logType)
        {
            var entry = new ConsoleLogEntry
            {
                logType = LogTypeToString(logType),
                message = condition ?? "",
                stackTrace = stackTrace ?? "",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                count = 1,
            };
            lock (_ringLock)
            {
                if (_ringBuffer.Count >= RingBufferCapacity)
                    _ringBuffer.RemoveAt(0);
                _ringBuffer.Add(entry);
            }
        }

        private static string LogTypeToString(UnityEngine.LogType t) => t switch
        {
            UnityEngine.LogType.Error => "Error",
            UnityEngine.LogType.Assert => "Assert",
            UnityEngine.LogType.Warning => "Warning",
            UnityEngine.LogType.Log => "Log",
            UnityEngine.LogType.Exception => "Exception",
            _ => "Log",
        };

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

        // ── Internal API — ring buffer primary, reflection fallback ────────────

        private static ConsoleLogsResultPayload GetConsoleLogs(string logType, int count)
        {
            // Primary: read from Application.logMessageReceived ring buffer
            var result = GetLogsFromRingBuffer(logType, count);
            if (result.logs.Count > 0)
                return result;

            // Fallback: reflection on internal LogEntries API
            return GetLogsViaReflection(logType, count);
        }

        private static ConsoleLogsResultPayload GetLogsFromRingBuffer(string logType, int count)
        {
            var result = new ConsoleLogsResultPayload();
            lock (_ringLock)
            {
                for (int i = _ringBuffer.Count - 1; i >= 0 && result.logs.Count < count; i--)
                {
                    var entry = _ringBuffer[i];
                    if (!string.IsNullOrEmpty(logType) &&
                        !string.Equals(entry.logType, logType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.logs.Add(entry);
                }
            }
            result.total = result.logs.Count;
            return result;
        }

        private static ConsoleLogsResultPayload GetLogsViaReflection(string logType, int count)
        {
            var result = new ConsoleLogsResultPayload();

            var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries")
                              ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntries");
            if (logEntriesType == null) return result;

            var startMethod = logEntriesType.GetMethod("StartGettingEntries",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var endMethod = logEntriesType.GetMethod("EndGettingEntries",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var getCount = logEntriesType.GetMethod("GetCount",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (getCount == null) return result;

            int totalCount = (int)getCount.Invoke(null, null);
            if (totalCount == 0) return result;

            startMethod?.Invoke(null, null);

            try
            {
                var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry")
                                ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntry");

                if (logEntryType == null || getEntryMethod == null)
                    return FallbackGetLogs(logEntriesType, logType, count, totalCount);

                var entry = Activator.CreateInstance(logEntryType);
                var messageField = logEntryType.GetField("message") ?? logEntryType.GetField("condition");
                var modeField = logEntryType.GetField("mode");

                for (int i = totalCount - 1; i >= 0 && result.logs.Count < count; i--)
                {
                    bool ok = (bool)getEntryMethod.Invoke(null, new object[] { i, entry });
                    if (!ok) continue;

                    int mode = modeField != null ? (int)modeField.GetValue(entry) : 0;
                    string entryType = ModeToLogType(mode);

                    if (!string.IsNullOrEmpty(logType) &&
                        !string.Equals(entryType, logType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string msg = messageField?.GetValue(entry)?.ToString() ?? "";
                    string stackTrace = "";
                    int nlIndex = msg.IndexOf('\n');
                    if (nlIndex >= 0)
                    {
                        stackTrace = msg.Substring(nlIndex + 1);
                        msg = msg.Substring(0, nlIndex);
                    }

                    result.logs.Add(new ConsoleLogEntry
                    {
                        logType = entryType,
                        message = msg,
                        stackTrace = stackTrace,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        count = 1
                    });
                }

                result.total = result.logs.Count;
            }
            finally
            {
                endMethod?.Invoke(null, null);
            }

            return result;
        }

        private static ConsoleLogsResultPayload FallbackGetLogs(System.Type logEntriesType, string logType, int count, int totalCount)
        {
            var result = new ConsoleLogsResultPayload();

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

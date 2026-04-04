// -----------------------------------------------------------------------
// UnityPilot Editor — https://github.com/codingriver/unitypilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    // ── Operation step record ────────────────────────────────────────────────
    internal struct OperationStepRecord
    {
        public DateTime Time;
        public string   Step;
        public string   Detail;
    }

    // ── Operation log entry ──────────────────────────────────────────────────
    internal class OperationLogEntry
    {
        public string   CommandId;
        public string   CommandName;
        public string   Description;
        public DateTime ReceivedAt;
        public DateTime? CompletedAt;
        public long     ElapsedMs;
        public string   Phase;        // received / queued / executing / waiting / completed / failed / stuck / reported
        public string   CurrentStep;
        public int      Progress = -1; // 0-100, -1 = N/A
        public string   ErrorCode;
        public string   ErrorMessage;
        public bool     ResultReported;
        public bool     IsStuck;
        public readonly List<OperationStepRecord> Steps = new();

        public void AddStep(string step, string detail = null)
        {
            var rec = new OperationStepRecord { Time = DateTime.Now, Step = step, Detail = detail };
            lock (Steps) Steps.Add(rec);
            CurrentStep = step;
        }
    }

    // ── Operation context (per-command, handler 可用) ────────────────────────
    internal sealed class OperationContext
    {
        private readonly OperationLogEntry _entry;
        private readonly UnityPilotOperationTracker _tracker;

        internal OperationContext(OperationLogEntry entry, UnityPilotOperationTracker tracker)
        {
            _entry   = entry;
            _tracker = tracker;
        }

        public string CommandId   => _entry.CommandId;
        public string CommandName => _entry.CommandName;

        public void Step(string step, string detail = null)
        {
            _entry.AddStep(step, detail);
            _tracker.WriteLogLine("STEP", _entry.CommandName, _entry.CommandId, step + (detail != null ? $" | {detail}" : ""));
        }

        public void Progress(int percent, string detail = null)
        {
            _entry.Progress = percent < 0 ? 0 : percent > 100 ? 100 : percent;
            _entry.AddStep($"进度 {percent}%", detail);
            _tracker.WriteLogLine("PROGRESS", _entry.CommandName, _entry.CommandId,
                $"{percent}%" + (detail != null ? $" {detail}" : ""));
        }

        public void Warn(string message)
        {
            _entry.AddStep("⚠ " + message);
            _tracker.WriteLogLine("WARN", _entry.CommandName, _entry.CommandId, message);
        }

        public void Complete(string summary = null)
        {
            _entry.CompletedAt = DateTime.Now;
            _entry.ElapsedMs   = (long)(DateTime.Now - _entry.ReceivedAt).TotalMilliseconds;
            _entry.Phase       = "completed";
            _entry.CurrentStep = summary ?? "完成";
            _entry.AddStep("完成", summary);
            _tracker.WriteLogLine("DONE", _entry.CommandName, _entry.CommandId,
                $"elapsed={_entry.ElapsedMs}ms" + (summary != null ? $" | {summary}" : ""));
        }

        public void Fail(string code, string message)
        {
            _entry.CompletedAt  = DateTime.Now;
            _entry.ElapsedMs    = (long)(DateTime.Now - _entry.ReceivedAt).TotalMilliseconds;
            _entry.Phase        = "failed";
            _entry.ErrorCode    = code;
            _entry.ErrorMessage = message;
            _entry.CurrentStep  = $"失败: {code}";
            _entry.AddStep($"失败: {code}", message);
            _tracker.WriteLogLine("FAIL", _entry.CommandName, _entry.CommandId,
                $"{code}: {message} | elapsed={_entry.ElapsedMs}ms");
        }

        public void MarkReported(bool isError = false)
        {
            _entry.ResultReported = true;
            var tag = isError ? "error已发送" : "result已发送";
            _entry.AddStep(tag);
            _tracker.WriteLogLine("REPORT", _entry.CommandName, _entry.CommandId, tag);
        }
    }

    // ── Main tracker service (singleton) ─────────────────────────────────────
    internal sealed class UnityPilotOperationTracker
    {
        private const int MaxEntries = 500;
        private const int WatchdogQueuedThresholdMs   = 10_000;
        private const int WatchdogExecThresholdMs     = 30_000;
        private const int WatchdogWaitingThresholdMs  = 120_000;
        private const int WatchdogAbsoluteThresholdMs = 300_000;

        private static readonly Lazy<UnityPilotOperationTracker> Lazy = new(() => new UnityPilotOperationTracker());
        public static UnityPilotOperationTracker Instance => Lazy.Value;

        private readonly ConcurrentDictionary<string, OperationContext> _activeContexts = new();
        private readonly object _listLock = new();
        private readonly List<OperationLogEntry> _entries = new(MaxEntries);

        // ── File logging ─────────────────────────────────────────────────────
        private static readonly object FileLock = new();
        private static bool _fileSessionPrepared;
        private const string SessionInitKey = "UnityPilot.OpLog.SessionStarted";

        public static string LogFilePath =>
            Path.Combine(UnityPilotEditorLog.ProjectLogsDirectory, "unitypilot-operations.log");

        private void EnsureLogFile()
        {
            lock (FileLock)
            {
                if (_fileSessionPrepared) return;
                try
                {
                    Directory.CreateDirectory(UnityPilotEditorLog.ProjectLogsDirectory);
                    if (!SessionState.GetBool(SessionInitKey, false))
                    {
                        SessionState.SetBool(SessionInitKey, true);
                        File.WriteAllText(LogFilePath,
                            $"=== UnityPilot operations log — session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {Application.productName} ==={Environment.NewLine}",
                            Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UnityPilot] 无法准备操作日志文件: {ex.Message}");
                }
                finally { _fileSessionPrepared = true; }
            }
        }

        internal void WriteLogLine(string tag, string commandName, string commandId, string detail)
        {
            EnsureLogFile();
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag,-8}] {commandName} id={commandId} | {detail}";
            lock (FileLock)
            {
                try { File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8); }
                catch { /* swallow */ }
            }
        }

        // ── System events (connection lifecycle, not per-command) ────────────

        /// <summary>
        /// 记录系统级事件（启动/停止/连接/断开/认证等），显示在操作日志 UI 和文件中，但不创建 OperationContext。
        /// </summary>
        public void RecordSystemEvent(string eventName, string description, string detail = null, string phase = "system")
        {
            var entry = new OperationLogEntry
            {
                CommandId   = $"sys-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                CommandName = eventName,
                Description = description,
                ReceivedAt  = DateTime.Now,
                CompletedAt = DateTime.Now,
                ElapsedMs   = 0,
                Phase       = phase,
                CurrentStep = description,
                ResultReported = true,
            };
            if (!string.IsNullOrEmpty(detail))
                entry.AddStep(description, detail);
            else
                entry.AddStep(description);

            lock (_listLock)
            {
                if (_entries.Count >= MaxEntries) _entries.RemoveAt(0);
                _entries.Add(entry);
            }

            var logDetail = detail != null ? $"{description} | {detail}" : description;
            WriteLogLine("SYSTEM", eventName, entry.CommandId, logDetail);
        }

        // ── Begin / Get / End ────────────────────────────────────────────────

        public OperationContext BeginOperation(string commandId, string commandName)
        {
            var desc = GetDescription(commandName);
            var entry = new OperationLogEntry
            {
                CommandId   = commandId,
                CommandName = commandName,
                Description = desc,
                ReceivedAt  = DateTime.Now,
                Phase       = "received",
                CurrentStep = "收到命令",
            };
            entry.AddStep("收到命令");

            lock (_listLock)
            {
                if (_entries.Count >= MaxEntries)
                    _entries.RemoveAt(0);
                _entries.Add(entry);
            }

            var ctx = new OperationContext(entry, this);
            _activeContexts[commandId] = ctx;

            WriteLogLine("RECV", commandName, commandId, $"({desc})");
            return ctx;
        }

        public OperationContext GetContext(string commandId)
        {
            if (string.IsNullOrEmpty(commandId)) return null;
            _activeContexts.TryGetValue(commandId, out var ctx);
            return ctx;
        }

        public void EndOperation(string commandId)
        {
            _activeContexts.TryRemove(commandId, out _);
        }

        /// <summary>Agent 侧上报的异常，写入操作日志。</summary>
        public void IngestAgentError(string source, string errorType, string message, string relatedCommandId, string context)
        {
            var detail = $"来源={source} 类型={errorType} | {message}";
            if (!string.IsNullOrEmpty(context))
                detail += $" | 上下文={context}";

            WriteLogLine("AGENT", "agent.reportError", relatedCommandId ?? "N/A", detail);

            if (!string.IsNullOrEmpty(relatedCommandId))
            {
                var ctx = GetContext(relatedCommandId);
                ctx?.Warn($"Agent上报: {message}");
            }

            // Also add as a standalone entry for UI display
            var entry = new OperationLogEntry
            {
                CommandId   = relatedCommandId ?? $"agent-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                CommandName = "agent.reportError",
                Description = "Agent异常上报",
                ReceivedAt  = DateTime.Now,
                CompletedAt = DateTime.Now,
                Phase       = "agent_error",
                CurrentStep = message,
                ErrorCode   = errorType,
                ErrorMessage = message,
                ResultReported = true,
            };
            entry.AddStep($"Agent上报: [{errorType}] {message}", context);
            lock (_listLock)
            {
                if (_entries.Count >= MaxEntries) _entries.RemoveAt(0);
                _entries.Add(entry);
            }
        }

        // ── Snapshot for UI ──────────────────────────────────────────────────

        public List<OperationLogEntry> GetEntriesCopy()
        {
            lock (_listLock) { return new List<OperationLogEntry>(_entries); }
        }

        public void ClearEntries()
        {
            lock (_listLock) { _entries.Clear(); }
            _activeContexts.Clear();
        }

        public int ActiveCount   => _activeContexts.Count;
        public int TotalCount    { get { lock (_listLock) return _entries.Count; } }
        public int FailedCount   { get { lock (_listLock) return _entries.Count(e => e.Phase == "failed"); } }
        public int StuckCount    { get { lock (_listLock) return _entries.Count(e => e.IsStuck); } }

        // ── Watchdog ─────────────────────────────────────────────────────────

        /// <summary>由 EditorApplication.update 定期调用。返回命令名列表（如果有超时临界的操作）。</summary>
        public List<string> RunWatchdog()
        {
            var stuckCommands = new List<string>();
            var now = DateTime.Now;

            foreach (var kvp in _activeContexts)
            {
                var ctx = kvp.Value;
                var id = kvp.Key;
                OperationLogEntry entry;
                lock (_listLock) { entry = _entries.LastOrDefault(e => e.CommandId == id); }
                if (entry == null || entry.CompletedAt.HasValue) continue;

                var elapsed = (long)(now - entry.ReceivedAt).TotalMilliseconds;
                bool shouldWarn = false;

                switch (entry.Phase)
                {
                    case "queued":
                        shouldWarn = elapsed > WatchdogQueuedThresholdMs;
                        break;
                    case "executing":
                        shouldWarn = elapsed > WatchdogExecThresholdMs;
                        break;
                    case "waiting":
                        shouldWarn = elapsed > WatchdogWaitingThresholdMs;
                        break;
                    default:
                        shouldWarn = elapsed > WatchdogAbsoluteThresholdMs;
                        break;
                }

                if (shouldWarn && !entry.IsStuck)
                {
                    entry.IsStuck = true;
                    entry.AddStep($"⚠ 卡住检测: {entry.CurrentStep}, 已{elapsed / 1000}s");
                    WriteLogLine("STUCK", entry.CommandName, entry.CommandId,
                        $"卡住: {entry.CurrentStep}, 已{elapsed}ms, phase={entry.Phase}");
                    stuckCommands.Add(entry.CommandName);
                }
            }

            return stuckCommands;
        }

        /// <summary>返回超过绝对超时阈值的操作 ID 列表（用于触发强制重启）。</summary>
        public List<string> GetCriticallyStuckCommandIds()
        {
            var result = new List<string>();
            var now = DateTime.Now;
            foreach (var kvp in _activeContexts)
            {
                OperationLogEntry entry;
                lock (_listLock) { entry = _entries.LastOrDefault(e => e.CommandId == kvp.Key); }
                if (entry == null || entry.CompletedAt.HasValue) continue;
                var elapsed = (long)(now - entry.ReceivedAt).TotalMilliseconds;
                if (elapsed > WatchdogAbsoluteThresholdMs)
                    result.Add(kvp.Key);
            }
            return result;
        }

        // ── Chinese descriptions ────────────────────────────────────────────

        private static readonly Dictionary<string, string> CommandDescriptions = new()
        {
            // compile
            { "compile.request",       "触发编译" },
            { "compile.wait",          "等待编译完成" },
            { "compile.errors.get",    "获取编译错误" },
            // playmode / input
            { "playmode.set",          "设置播放模式" },
            { "mouse.event",           "鼠标事件" },
            { "keyboard.event",        "键盘事件" },
            { "editor.state",          "获取编辑器状态" },
            // gameobject
            { "gameobject.create",     "创建游戏对象" },
            { "gameobject.find",       "查找游戏对象" },
            { "gameobject.modify",     "修改游戏对象" },
            { "gameobject.delete",     "删除游戏对象" },
            { "gameobject.move",       "移动游戏对象" },
            { "gameobject.duplicate",  "复制游戏对象" },
            // scene
            { "scene.create",          "创建场景" },
            { "scene.open",            "打开场景" },
            { "scene.save",            "保存场景" },
            { "scene.load",            "加载场景" },
            { "scene.setActive",       "设置活跃场景" },
            { "scene.list",            "列出场景" },
            { "scene.unload",          "卸载场景" },
            { "scene.ensureTest",      "确保测试场景" },
            // component
            { "component.add",         "添加组件" },
            { "component.remove",      "移除组件" },
            { "component.get",         "获取组件" },
            { "component.modify",      "修改组件" },
            { "component.list",        "列出组件" },
            // asset
            { "asset.find",            "查找资源" },
            { "asset.createFolder",    "创建文件夹" },
            { "asset.copy",            "复制资源" },
            { "asset.move",            "移动资源" },
            { "asset.delete",          "删除资源" },
            { "asset.refresh",         "刷新资源" },
            { "asset.getInfo",         "获取资源信息" },
            { "asset.getData",         "获取资源数据" },
            { "asset.modifyData",      "修改资源数据" },
            { "asset.findBuiltIn",     "查找内置资源" },
            // screenshot
            { "screenshot.gameView",     "截图游戏视图" },
            { "screenshot.sceneView",    "截图场景视图" },
            { "screenshot.camera",       "截图摄像机" },
            { "screenshot.editorWindow", "截图编辑器窗口" },
            // prefab
            { "prefab.create",         "创建预制体" },
            { "prefab.instantiate",    "实例化预制体" },
            { "prefab.open",           "打开预制体" },
            { "prefab.close",          "关闭预制体" },
            { "prefab.save",           "保存预制体" },
            // material / shader
            { "material.create",       "创建材质" },
            { "material.modify",       "修改材质" },
            { "material.assign",       "分配材质" },
            { "material.get",          "获取材质" },
            { "shader.list",           "列出着色器" },
            // script
            { "script.read",           "读取脚本" },
            { "script.create",         "创建脚本" },
            { "script.update",         "更新脚本" },
            { "script.delete",         "删除脚本" },
            // console
            { "console.logs.get",      "获取控制台日志" },
            { "console.clear",         "清除控制台" },
            // menu
            { "menu.execute",          "执行菜单命令" },
            { "menu.list",             "列出菜单" },
            // package
            { "package.add",           "添加包" },
            { "package.remove",        "移除包" },
            { "package.list",          "列出包" },
            { "package.search",        "搜索包" },
            // test
            { "test.run",              "运行测试" },
            { "test.results",          "获取测试结果" },
            { "test.list",             "列出测试" },
            // selection
            { "selection.get",         "获取选中对象" },
            { "selection.set",         "设置选中对象" },
            { "selection.clear",       "清除选中" },
            // reflection
            { "reflection.find",       "反射查找" },
            { "reflection.call",       "反射调用" },
            // batch
            { "batch.execute",         "批量执行" },
            { "batch.cancel",          "取消批量" },
            { "batch.results",         "批量结果" },
            // build
            { "build.start",           "开始构建" },
            { "build.status",          "构建状态" },
            { "build.cancel",          "取消构建" },
            { "build.targets",         "构建目标列表" },
            // editor
            { "editor.undo",           "撤销" },
            { "editor.redo",           "重做" },
            { "editor.executeCommand", "执行编辑器命令" },
            { "sceneview.navigate",    "场景视图导航" },
            // drag & drop
            { "dragdrop.execute",      "执行拖放" },
            // csharp
            { "csharp.execute",        "执行C#代码" },
            { "csharp.status",         "C#执行状态" },
            { "csharp.abort",          "中止C#执行" },
            // UIToolkit
            { "uitoolkit.dump",        "导出UI元素树" },
            { "uitoolkit.query",       "查询UI元素" },
            { "uitoolkit.event",       "UI事件" },
            { "uitoolkit.scroll",      "UI滚动" },
            { "uitoolkit.setValue",    "设置UI值" },
            { "uitoolkit.interact",    "UI交互" },
            // window
            { "editor.windows.list",   "列出编辑器窗口" },
            // resource
            { "resource.sceneHierarchy",    "获取场景层级" },
            { "resource.consoleLogs",       "获取控制台日志(资源)" },
            { "resource.editorState",       "获取编辑器状态(资源)" },
            { "resource.packages",          "获取包列表(资源)" },
            { "resource.buildStatus",       "获取构建状态(资源)" },
            { "resource.unityPilotLogsTab", "获取日志标签页(资源)" },
            { "resource.windowDiagnostics", "获取窗口诊断(资源)" },
            { "resource.consoleSummary",    "获取控制台摘要(资源)" },
            // agent
            { "agent.reportError",     "Agent异常上报" },
            // force restart
            { "editor.forceRestart",   "强制重启编辑器" },
            // system lifecycle events
            { "sys.bridge.start",      "Bridge启动" },
            { "sys.bridge.stop",       "Bridge停止" },
            { "sys.bridge.restart",    "Bridge重启" },
            { "sys.ws.connected",      "WS连接成功" },
            { "sys.ws.disconnected",   "WS连接断开" },
            { "sys.ws.close.received", "收到服务端关闭帧" },
            { "sys.auth.success",      "认证成功" },
            { "sys.auth.lost",         "认证丢失" },
            { "sys.domain.reload.start", "Domain Reload开始" },
            { "sys.domain.reload.done",  "Domain Reload完成" },
            { "sys.compile.start",     "Unity编译开始" },
            { "sys.compile.done",      "Unity编译完成" },
            { "sys.playmode.changed",  "PlayMode状态变更" },
            { "sys.force.restart",     "强制重启Unity" },
        };

        public static string GetDescription(string commandName)
        {
            if (string.IsNullOrEmpty(commandName)) return "未知命令";
            return CommandDescriptions.TryGetValue(commandName, out var desc) ? desc : commandName;
        }

        // ── Reveal log file ─────────────────────────────────────────────────

        public void RevealLogFile()
        {
            try
            {
                EnsureLogFile();
                if (!File.Exists(LogFilePath))
                    File.WriteAllText(LogFilePath, "", Encoding.UTF8);
                EditorUtility.RevealInFinder(LogFilePath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("UnityPilot", $"无法打开操作日志: {ex.Message}", "确定");
            }
        }
    }
}

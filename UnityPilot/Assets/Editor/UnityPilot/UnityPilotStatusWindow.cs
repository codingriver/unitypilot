using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    internal class UnityPilotStatusWindow : EditorWindow
    {
        // ── Volatile state written by background threads ───────────────────────
        private volatile string _diagResult  = "";
        private volatile bool   _diagRunning = false;
        private long            _diagResultAtMs;

        // ── Layout ────────────────────────────────────────────────────────────
        private Vector2 _logScroll;
        private Vector2 _diagScroll;
        private bool    _autoScroll = true;
        private double  _lastRepaint;

        // ── GUI event-stable snapshots (Layout -> Repaint) ───────────────────
        private BridgeStatus         _guiStatusSnapshot;
        private List<BridgeLogEntry> _guiLogsSnapshot = new();
        private string               _guiDiagResultSnapshot = "";
        private bool                 _guiDiagRunningSnapshot;
        private long                 _guiDiagResultAtMsSnapshot;

        // ── Log filter ────────────────────────────────────────────────────────
        private bool _showInfo    = true;
        private bool _showWarn    = true;
        private bool _showError   = true;
        private bool _showCompile = true;
        private bool _showNetwork = true;

        // ── UI state ──────────────────────────────────────────────────────────
        private int  _activeTab;
        private bool _skipKillConfirm;
        private string _toastMessage = "";
        private MessageType _toastType = MessageType.Info;
        private double _toastExpireAt;

        private string _wsHostInput = "127.0.0.1";
        private int _wsPortInput = 8765;

        // ── Styles (lazy-init on main thread) ─────────────────────────────────
        private GUIStyle _styleInfo;
        private GUIStyle _styleWarn;
        private GUIStyle _styleError;
        private GUIStyle _styleBox;
        private GUIStyle _styleLogCard;
        private GUIStyle _styleLogSelInfo;
        private GUIStyle _styleLogSelWarn;
        private GUIStyle _styleLogSelError;
        private bool     _stylesInit;

        [MenuItem("UnityPilot/UnityPilot", false, 200)]
        public static void Open()
        {
            var win = GetWindow<UnityPilotStatusWindow>("UnityPilot");
            win.minSize = new Vector2(400, 540);
            win.Show();
        }

        private void OnEnable()
        {
            var bridge = UnityPilotBridge.Instance;
            _wsHostInput = bridge.WsHost;
            _wsPortInput = bridge.WsPort;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            PersistEndpointInputsIfIdle();
        }

        /// <summary>
        /// 将 IP/端口写入 <see cref="UnityPilotBridge"/>（内部使用 EditorPrefs），Bridge 未运行时关闭窗口也会保存。
        /// </summary>
        private void PersistEndpointInputsIfIdle()
        {
            var bridge = UnityPilotBridge.Instance;
            if (bridge.GetStatus().IsStarted)
                return;

            var host = string.IsNullOrWhiteSpace(_wsHostInput) ? "127.0.0.1" : _wsHostInput.Trim();
            var port = _wsPortInput <= 0 ? 8765 : _wsPortInput;
            if (host == bridge.WsHost && port == bridge.WsPort)
                return;

            bridge.SetWsEndpoint(host, port);
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastRepaint > 0.1)
            {
                _lastRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _styleBox = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 6, 6),
                margin  = new RectOffset(4, 4, 2, 2),
            };
            _styleInfo = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                wordWrap = true,
                normal   = { textColor = EditorGUIUtility.isProSkin ? new Color(0.75f, 0.75f, 0.75f) : Color.black },
            };
            _styleWarn  = new GUIStyle(_styleInfo) { normal = { textColor = new Color(1f, 0.78f, 0.15f) } };
            _styleError = new GUIStyle(_styleInfo) { normal = { textColor = new Color(1f, 0.35f, 0.35f) } };

            _styleLogCard = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin  = new RectOffset(6, 6, 0, 12),
            };

            _styleLogSelInfo  = CreatePassiveLogSelectable(_styleInfo.normal.textColor);
            _styleLogSelWarn  = CreatePassiveLogSelectable(_styleWarn.normal.textColor);
            _styleLogSelError = CreatePassiveLogSelectable(_styleError.normal.textColor);
        }

        private static GUIStyle CreatePassiveLogSelectable(Color textColor)
        {
            var s = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                richText = false,
            };
            s.normal.textColor = textColor;
            MirrorSelectableStatesFromNormal(s);
            return s;
        }

        private static void MirrorSelectableStatesFromNormal(GUIStyle s)
        {
            void Mirror(GUIStyleState to)
            {
                if (to == null || s.normal == null) return;
                to.background = s.normal.background;
                to.textColor = s.normal.textColor;
                to.scaledBackgrounds = s.normal.scaledBackgrounds;
            }

            Mirror(s.focused);
            Mirror(s.active);
            Mirror(s.hover);
            Mirror(s.onFocused);
            Mirror(s.onActive);
            Mirror(s.onHover);
        }

        // ─────────────────────────────────── GUI ──────────────────────────────
        // IMPORTANT: snapshot all background-thread-written state here once,
        // so the Layout pass and Repaint pass see identical control counts.

        private void OnGUI()
        {
            InitStyles();

            var bridge = UnityPilotBridge.Instance;

            if (Event.current.type == EventType.Layout || _guiLogsSnapshot.Count == 0)
            {
                _guiStatusSnapshot       = bridge.GetStatus();
                _guiLogsSnapshot         = bridge.GetLogsCopy();
                _guiDiagResultSnapshot   = _diagResult;
                _guiDiagRunningSnapshot  = _diagRunning;
                _guiDiagResultAtMsSnapshot = _diagResultAtMs;
            }

            DrawTopStatusOverview(_guiStatusSnapshot);
            DrawToastIfAny();

            EditorGUILayout.Space(4);
            _activeTab = GUILayout.Toolbar(_activeTab, new[] { "运行状态", "诊断日志" });
            EditorGUILayout.Space(4);

            if (_activeTab == 0)
            {
                DrawRuntimeTab(bridge, _guiStatusSnapshot, _guiDiagResultSnapshot, _guiDiagRunningSnapshot, _guiDiagResultAtMsSnapshot);
            }
            else
            {
                DrawLogsTab(bridge, _guiLogsSnapshot);
            }
        }

        private void DrawRuntimeTab(UnityPilotBridge bridge, BridgeStatus status, string diagResult, bool diagRunning, long diagResultAtMs)
        {
            DrawEnableSection(bridge, status);
            EditorGUILayout.Space(4);
            DrawConnectionSection(status);
            EditorGUILayout.Space(4);
            DrawEndpointSection(bridge, status);
            EditorGUILayout.Space(4);
            DrawDiagnosticsSection(bridge, diagResult, diagRunning, diagResultAtMs);
        }

        private void DrawLogsTab(UnityPilotBridge bridge, List<BridgeLogEntry> logs)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                DrawLogToolbar(logs, bridge);

                float logHeight = Mathf.Clamp(position.height * 0.68f, 220f, 640f);
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(logHeight));

                var contentW = Mathf.Max(120f, position.width - 52f);

                foreach (var entry in logs)
                {
                    if (!ShouldShowLogEntry(entry)) continue;

                    var fullText = BuildLogEntryDisplayText(entry);
                    var selStyle = GetLogSelectableStyle(entry);
                    var blockH = selStyle.CalcHeight(new GUIContent(fullText), contentW);
                    blockH = Mathf.Max(blockH, EditorGUIUtility.singleLineHeight * 2f);

                    using (new EditorGUILayout.VerticalScope(_styleLogCard))
                    {
                        EditorGUILayout.SelectableLabel(fullText, selStyle, GUILayout.Width(contentW), GUILayout.Height(blockH));

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("复制本条", EditorStyles.miniButton, GUILayout.Width(52)))
                            {
                                GUIUtility.systemCopyBuffer = fullText;
                                ShowToast("已复制本条日志");
                            }
                        }
                    }
                }

                EditorGUILayout.EndScrollView();

                if (_autoScroll && logs.Count > 0)
                    _logScroll = new Vector2(0, float.MaxValue);
            }
        }

        private GUIStyle GetLogSelectableStyle(BridgeLogEntry entry)
        {
            if (entry.Level == "warn") return _styleLogSelWarn;
            if (entry.Level == "error") return _styleLogSelError;
            return _styleLogSelInfo;
        }

        private static string BuildLogEntryDisplayText(BridgeLogEntry entry)
        {
            if (!entry.IsWireStructured)
                return $"[{entry.Time:yyyy-MM-dd HH:mm:ss.fff}] {entry.Message}";

            var ts = entry.WireEnvelopeUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(entry.WireEnvelopeUnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                : entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var meta =
                $"{ts}  |  sessionId={entry.WireSessionId}  |  name={entry.WireName}  |  type={entry.WireType}  |  id={entry.WireId}";
            var body = (entry.WireIsRaw ? "[RAW] " : "") + entry.WireDetail;
            return meta + Environment.NewLine + body;
        }

        private void DrawTopStatusOverview(BridgeStatus status)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("状态总览", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("?", GUILayout.Width(22), GUILayout.Height(18)))
                        UnityPilotTroubleshootWindow.OpenWindow();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawBadge("Bridge", status.IsStarted ? "运行中" : "已停止", status.IsStarted);
                    DrawBadge("WS", status.IsWsOpen ? "已连接" : "未连接", status.IsWsOpen);
                    DrawBadge("Auth", status.IsAuthenticated ? "已认证" : "未认证", status.IsAuthenticated);
                    DrawBadge("Compile", status.IsCompiling ? "编译中" : "空闲", !status.IsCompiling);
                    DrawBadge("Errors", status.LastErrorCount.ToString(), status.LastErrorCount == 0);
                }

                EditorGUILayout.Space(4);
                var play = string.IsNullOrEmpty(status.PlayModeState) ? "—" : status.PlayModeState;
                EditorGUILayout.LabelField(
                    $"编辑器状态：{(status.IsCompiling ? "编译中" : "空闲")}　·　编译错误 {status.LastErrorCount}　·　PlayMode {play}",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawBadge(string label, string value, bool ok)
        {
            var prev = GUI.color;
            GUI.color = ok ? new Color(0.2f, 0.75f, 0.3f, 0.15f) : new Color(0.95f, 0.3f, 0.3f, 0.18f);
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.MinWidth(72), GUILayout.MaxWidth(120));
            GUI.color = prev;
            GUILayout.Label(label, EditorStyles.miniBoldLabel);
            GUILayout.Label(value, EditorStyles.miniLabel);
            GUILayout.EndVertical();
        }

        private void DrawToastIfAny()
        {
            if (string.IsNullOrEmpty(_toastMessage)) return;
            if (EditorApplication.timeSinceStartup > _toastExpireAt)
            {
                _toastMessage = string.Empty;
                return;
            }

            EditorGUILayout.HelpBox(_toastMessage, _toastType);
        }

        private void ShowToast(string message, MessageType type = MessageType.Info, double seconds = 2.6)
        {
            _toastMessage = message;
            _toastType = type;
            _toastExpireAt = EditorApplication.timeSinceStartup + seconds;
        }

        // ── Enable / Disable ──────────────────────────────────────────────────

        private void DrawEnableSection(UnityPilotBridge bridge, BridgeStatus status)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("UnityPilot 开关", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool enabled    = UnityPilotBootstrap.IsEnabled;
                    bool newEnabled = EditorGUILayout.Toggle("自动启动", enabled);
                    if (newEnabled != enabled)
                        UnityPilotBootstrap.IsEnabled = newEnabled;

                    GUILayout.FlexibleSpace();
                    var dotColor = status.IsStarted
                        ? (status.IsWsOpen ? Color.green : new Color(1f, 0.6f, 0f))
                        : Color.gray;
                    var prev = GUI.color;
                    GUI.color = dotColor;
                    GUILayout.Label("●", GUILayout.Width(18));
                    GUI.color = prev;
                    GUILayout.Label(
                        status.IsStarted ? (status.IsWsOpen ? "运行中" : "连接中…") : "已停止",
                        GUILayout.Width(56));
                }

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("启动", GUILayout.Height(24)))
                    {
                        bridge.EnsureStarted();
                        ShowToast("Bridge 启动中");
                    }
                    if (GUILayout.Button("停止", GUILayout.Height(24)))
                    {
                        bridge.Stop();
                        ShowToast("Bridge 已停止", MessageType.Warning);
                    }
                    if (GUILayout.Button("重启", GUILayout.Height(24)))
                    {
                        bridge.Restart();
                        ShowToast("Bridge 重启中");
                    }
                }

                var debugWire = bridge.DebugWireLogsEnabled;
                var newDebugWire = EditorGUILayout.ToggleLeft("调试通信日志（收发命令）", debugWire);
                if (newDebugWire != debugWire)
                    bridge.DebugWireLogsEnabled = newDebugWire;
            }
        }

        private void DrawEndpointSection(UnityPilotBridge bridge, BridgeStatus status)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("WS 连接配置（EditorPrefs 持久化）", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "地址按项目保存在本机 EditorPrefs（键名带路径哈希后缀，例如 UnityPilot.WsHost." +
                    UnityPilotBridge.WsEndpointEditorPrefsKeySuffix + "），重启 Unity 后仍有效。",
                    MessageType.None);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("IP", GUILayout.Width(22));
                    EditorGUI.BeginChangeCheck();
                    _wsHostInput = EditorGUILayout.TextField(_wsHostInput);
                    GUILayout.Space(6);
                    EditorGUILayout.LabelField("端口", GUILayout.Width(30));
                    _wsPortInput = EditorGUILayout.IntField(_wsPortInput, GUILayout.Width(84));
                    if (EditorGUI.EndChangeCheck() && !status.IsStarted)
                        PersistEndpointInputsIfIdle();
                }

                using (new EditorGUI.DisabledScope(status.IsStarted))
                {
                    if (GUILayout.Button("应用连接地址", GUILayout.Height(22)))
                    {
                        if (_wsPortInput <= 0) _wsPortInput = 8765;
                        if (string.IsNullOrWhiteSpace(_wsHostInput)) _wsHostInput = "127.0.0.1";
                        bridge.SetWsEndpoint(_wsHostInput, _wsPortInput);
                        ShowToast($"已应用 ws://{_wsHostInput}:{_wsPortInput}");
                    }
                }

                if (status.IsStarted)
                    EditorGUILayout.HelpBox("Bridge 运行中，先停止再修改连接地址。", MessageType.Info);
            }
        }

        // ── Connection status ─────────────────────────────────────────────────

        private void DrawConnectionSection(BridgeStatus status)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("连接状态", EditorStyles.boldLabel);
                DrawRow("WS 连接",    status.IsWsOpen        ? "✓ 已连接" : "✗ 未连接", status.IsWsOpen);
                DrawRow("已认证",      status.IsAuthenticated ? "✓ 是"     : "✗ 否",     status.IsAuthenticated);
                DrawRow("Session ID", string.IsNullOrEmpty(status.SessionId) ? "—" : status.SessionId, true);

                string hbText;
                bool hbOk;
                if (!status.IsStarted)
                {
                    hbText = "未启动";
                    hbOk = false;
                }
                else if (!status.IsWsOpen)
                {
                    hbText = "未连接";
                    hbOk = false;
                }
                else if (status.LastHeartbeatSentAt <= 0)
                {
                    hbText = "等待首个心跳";
                    hbOk = true;
                }
                else
                {
                    var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - status.LastHeartbeatSentAt;
                    if (elapsedMs < 1000)
                        hbText = "< 1 s 前";
                    else
                        hbText = $"{elapsedMs / 1000.0:F1} s 前";
                    hbOk = elapsedMs < 5000;
                }
                DrawRow("上次心跳", hbText, hbOk);
            }
        }

        // ── Diagnostics ───────────────────────────────────────────────────────

        private void DrawDiagnosticsSection(UnityPilotBridge bridge, string diagResult, bool diagRunning, long diagResultAtMs)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("诊断 / 通信测试", EditorStyles.boldLabel);
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(diagRunning))
                    {
                        if (GUILayout.Button("测试服务器", GUILayout.Height(22)))
                        {
                            var snap = bridge.GetStatus();
                            RunDiag(() => Task.FromResult(BuildServerTestResult(snap, bridge.WsHost, bridge.WsPort)));
                            ShowToast("已执行服务器测试");
                        }
                    }
                }
                
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(diagRunning))
                    {
                        if (GUILayout.Button("检查服务器", GUILayout.Height(22)))
                        {
                            RunDiag(CheckPythonMcpProcess);
                            ShowToast("已执行服务器检查");
                        }
                    }
                }
                
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(diagRunning))
                    {
                        var prev = GUI.color;
                        GUI.color = new Color(1f, 0.6f, 0.6f);
                        if (GUILayout.Button("杀掉所有服务器", GUILayout.Height(22)))
                        {
                            bool confirmed = _skipKillConfirm || EditorUtility.DisplayDialog("确认操作", "确定要杀掉所有疑似服务器进程吗？", "确定", "取消");
                            if (confirmed)
                            {
                                RunDiag(KillPythonMcpProcesses);
                                ShowToast("已执行杀进程操作", MessageType.Warning);
                            }
                        }
                        GUI.color = prev;
                    }
                }
                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(diagResult))
                {
                    EditorGUILayout.Space(2);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("诊断日志", EditorStyles.miniBoldLabel, GUILayout.Width(48));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("复制", EditorStyles.miniButton, GUILayout.Width(36)))
                        {
                            GUIUtility.systemCopyBuffer = diagResult;
                            ShowToast("已复制诊断结果");
                        }
                    }

                    _diagScroll = EditorGUILayout.BeginScrollView(_diagScroll, GUILayout.Height(92));
                    DrawDiagLines(diagResult, diagResultAtMs);
                    EditorGUILayout.EndScrollView();
                }

                if (diagRunning)
                    EditorGUILayout.LabelField("正在测试…", EditorStyles.centeredGreyMiniLabel);
            }
        }

        private void DrawLogToolbar(List<BridgeLogEntry> logs, UnityPilotBridge bridge)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("通信日志", EditorStyles.boldLabel, GUILayout.Width(52));
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("打开日志文件", EditorStyles.miniButton, GUILayout.Width(88)))
                {
                    UnityPilotEditorLog.RevealLogFile();
                    ShowToast("已定位到 " + UnityPilotEditorLog.LogFilePath);
                }

                var debugWire = bridge.DebugWireLogsEnabled;
                var newDebugWire = GUILayout.Toggle(debugWire, "调试通信", EditorStyles.miniButton, GUILayout.Width(70));
                if (newDebugWire != debugWire)
                {
                    bridge.DebugWireLogsEnabled = newDebugWire;
                    ShowToast(newDebugWire ? "已开启调试通信日志（含实时 RAW）" : "已关闭调试通信日志");
                }

                _showInfo    = GUILayout.Toggle(_showInfo,    "Info",    EditorStyles.miniButtonLeft, GUILayout.Width(40));
                _showWarn    = GUILayout.Toggle(_showWarn,    "Warn",    EditorStyles.miniButtonMid,  GUILayout.Width(40));
                _showError   = GUILayout.Toggle(_showError,   "Error",   EditorStyles.miniButtonMid,  GUILayout.Width(44));
                _showCompile = GUILayout.Toggle(_showCompile, "编译",    EditorStyles.miniButtonMid,  GUILayout.Width(44));
                _showNetwork = GUILayout.Toggle(_showNetwork, "网络",    EditorStyles.miniButtonRight, GUILayout.Width(44));

                GUILayout.Space(4);
                _autoScroll = GUILayout.Toggle(_autoScroll, "自动滚动", EditorStyles.miniButton, GUILayout.Width(60));
                GUILayout.Space(4);
                if (GUILayout.Button("复制全部", EditorStyles.miniButton, GUILayout.Width(52)))
                {
                    CopyFilteredLogs(logs);
                    ShowToast("已复制过滤后的日志");
                }
                GUILayout.Space(2);
                if (GUILayout.Button("清除", EditorStyles.miniButton, GUILayout.Width(36)))
                {
                    bridge.ClearLogs();
                    ShowToast("日志已清除");
                }
            }
        }

        private bool ShouldShowLogEntry(BridgeLogEntry entry)
        {
            if (entry.Level == "info"  && !_showInfo)  return false;
            if (entry.Level == "warn"  && !_showWarn)  return false;
            if (entry.Level == "error" && !_showError) return false;

            var msg = entry.Message ?? string.Empty;
            if (!_showCompile && msg.IndexOf("编译", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (!_showNetwork)
            {
                var isNetwork = msg.IndexOf("WS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                msg.IndexOf("session.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                msg.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                msg.IndexOf("heartbeat", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isNetwork) return false;
            }

            return true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void DrawRow(string label, string value, bool ok)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(96));
                var prev = GUI.color;
                GUI.color = ok ? Color.white : new Color(1f, 0.6f, 0.4f);
                EditorGUILayout.SelectableLabel(value,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                GUI.color = prev;
            }
        }

        private void CopyFilteredLogs(List<BridgeLogEntry> logs)
        {
            var sb = new StringBuilder();
            var first = true;
            foreach (var e in logs)
            {
                if (!ShouldShowLogEntry(e)) continue;
                if (!first) sb.AppendLine();
                first = false;
                sb.AppendLine(BuildLogEntryDisplayText(e));
            }
            GUIUtility.systemCopyBuffer = sb.ToString();
        }

        private void DrawDiagLines(string diagResult, long diagResultAtMs)
        {
            var lines = diagResult.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var ts = diagResultAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(diagResultAtMs).LocalDateTime
                : DateTime.Now;
            foreach (var raw in lines)
            {
                var line = raw ?? string.Empty;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var style = line.StartsWith("✗") || line.StartsWith("异常") || line.StartsWith("检查失败")
                    ? _styleError
                    : line.StartsWith("✓")
                        ? _styleInfo
                        : _styleWarn;

                EditorGUILayout.LabelField($"[{ts:HH:mm:ss.fff}] {line}", style);
            }
        }

        private void RunDiag(Func<Task<string>> taskFactory)
        {
            _diagRunning = true;
            _diagResult  = "";
            _diagResultAtMs = 0;
            Task.Run(async () =>
            {
                try
                {
                    _diagResult = await taskFactory();
                    _diagResultAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                catch (Exception ex)
                {
                    _diagResult = $"异常: {ex.Message}";
                    _diagResultAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                finally { _diagRunning = false; }
            });
        }

        private static string BuildServerTestResult(BridgeStatus status, string wsHost, int wsPort)
        {
            if (!status.IsStarted) return "✗ Bridge 未启动";
            if (!status.IsWsOpen) return "✗ WS 未连接";
            if (!status.IsAuthenticated) return "✗ 尚未认证";

            var hbText = "未发送";
            if (status.LastHeartbeatSentAt > 0)
            {
                var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - status.LastHeartbeatSentAt;
                hbText = elapsedMs < 1000 ? "<1s" : $"{elapsedMs / 1000.0:F1}s";
            }

            return "✓ 服务器测试通过\n" +
                   $"  WS      : ws://{wsHost}:{wsPort}\n" +
                   $"  认证状态: 已认证\n" +
                   $"  Session : {status.SessionId}\n" +
                   $"  心跳延迟: {hbText}\n" +
                   $"  编译中  : {(status.IsCompiling ? "是" : "否")}\n" +
                   $"  编译错误: {status.LastErrorCount}";
        }

        private static Task<string> CheckPythonMcpProcess()
        {
            try
            {
                var p1 = System.Diagnostics.Process.GetProcessesByName("python");
                var p2 = System.Diagnostics.Process.GetProcessesByName("python3");
                var all = new List<System.Diagnostics.Process>(p1.Length + p2.Length);
                all.AddRange(p1);
                all.AddRange(p2);

                if (all.Count == 0)
                    return Task.FromResult("✗ 未检测到 python / python3 进程\n  请先启动 MCP 服务");

                var portsByPid = SafeGetListeningPortsByPid(out var portsFetched);

                int unityPilotLikeCount = 0;
                var summary = new StringBuilder();
                var detail = new StringBuilder();

                summary.AppendLine($"检测到 {all.Count} 个 Python 进程（简要）:");
                detail.AppendLine("[UnityPilot] Python 进程诊断（详细）");

                foreach (var p in all)
                {
                    string exePath = SafeGetExePath(p);
                    string cmdLine = SafeGetCommandLine(p.Id);
                    int parentPid = SafeGetParentProcessId(p.Id);
                    string parentName = SafeGetProcessNameById(parentPid);

                    bool isUnityPilotLike = IsUnityPilotMcpLike(cmdLine);

                    string listenPortText = "unkown";
                    if (portsFetched && portsByPid.TryGetValue(p.Id, out var listenPorts) && listenPorts.Count > 0)
                        listenPortText = string.Join(",", listenPorts);

                    if (isUnityPilotLike) unityPilotLikeCount++;

                    var tag = isUnityPilotLike ? "✓" : "?";
                    summary.AppendLine($"[{tag}] PID={p.Id}  PPID={parentPid}({parentName})  PORT={listenPortText}");

                    detail.AppendLine($"[{tag}] PID={p.Id}  PPID={parentPid}({parentName})");
                    detail.AppendLine($"    PORT: {listenPortText}");
                    detail.AppendLine($"    EXE: {exePath}");
                    detail.AppendLine($"    CMD: {cmdLine}");
                }

                if (!portsFetched)
                    summary.AppendLine("! 监听端口读取失败，已显示为 unkown");

                if (unityPilotLikeCount == 0)
                {
                    summary.AppendLine("✗ 未发现明显的 UnityPilot MCP 进程特征");
                    summary.AppendLine("  请确认命令行包含 run_unitypilot_mcp.py 或 unitypilot_mcp");
                }
                else
                {
                    summary.AppendLine($"✓ 疑似 UnityPilot MCP 进程数量: {unityPilotLikeCount}");
                }

                summary.AppendLine("(详细命令行已输出到 Console 的 Warning 日志)");
                Debug.LogWarning(detail.ToString().TrimEnd());
                return Task.FromResult(summary.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"检查失败: {ex.Message}");
            }
        }

        private static Task<string> KillPythonMcpProcesses()
        {
            try
            {
                var p1 = System.Diagnostics.Process.GetProcessesByName("python");
                var p2 = System.Diagnostics.Process.GetProcessesByName("python3");
                var all = new List<System.Diagnostics.Process>(p1.Length + p2.Length);
                all.AddRange(p1);
                all.AddRange(p2);

                var targets = new List<System.Diagnostics.Process>();
                foreach (var p in all)
                {
                    var cmd = SafeGetCommandLine(p.Id);
                    if (IsUnityPilotMcpLike(cmd))
                        targets.Add(p);
                }

                if (targets.Count == 0)
                    return Task.FromResult("✓ 未发现需要终止的服务器进程");

                int killed = 0;
                var sb = new StringBuilder();
                sb.AppendLine($"准备终止 {targets.Count} 个疑似服务器进程：");

                foreach (var p in targets)
                {
                    try
                    {
                        int pid = p.Id;
                        var cmd = SafeGetCommandLine(pid);
                        p.Kill();
                        killed++;
                        sb.AppendLine($"✓ 已终止 PID={pid}");
                        Debug.LogWarning($"[UnityPilot] 已终止服务器进程 PID={pid}\nCMD: {cmd}");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"✗ 终止失败 PID={p.Id}: {ex.Message}");
                    }
                }

                sb.AppendLine($"完成：成功 {killed}/{targets.Count}");
                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"终止进程失败: {ex.Message}");
            }
        }

        private static bool IsUnityPilotMcpLike(string cmdLine)
        {
            return ContainsIgnoreCase(cmdLine, "run_unitypilot_mcp.py") ||
                   ContainsIgnoreCase(cmdLine, "unitypilot_mcp") ||
                   ContainsIgnoreCase(cmdLine, "unitypilot") ||
                   ContainsIgnoreCase(cmdLine, "unity-pilot") ||
                   ContainsIgnoreCase(cmdLine, "mcp");
        }

        private static string SafeGetExePath(System.Diagnostics.Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? "(未知)";
            }
            catch
            {
                return "(无权限或不可读取)";
            }
        }

        private static int SafeGetParentProcessId(int pid)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where ProcessId={pid} get ParentProcessId /value",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return -1;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);

                var marker = "ParentProcessId=";
                var idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return -1;
                var value = output.Substring(idx + marker.Length).Trim();
                return int.TryParse(value, out var ppid) ? ppid : -1;
            }
            catch
            {
                return -1;
            }
#else
            return -1;
#endif
        }

        private static string SafeGetProcessNameById(int pid)
        {
            if (pid <= 0) return "unknown";
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                return proc.ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }

        private static string SafeGetCommandLine(int pid)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where ProcessId={pid} get CommandLine /value",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return "(读取命令行失败)";
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);

                if (string.IsNullOrWhiteSpace(output)) return "(空)";
                var marker = "CommandLine=";
                var idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return output.Trim();

                var cmd = output.Substring(idx + marker.Length).Trim();
                return string.IsNullOrWhiteSpace(cmd) ? "(空)" : cmd;
            }
            catch
            {
                return "(读取命令行失败)";
            }
#else
            return "(当前平台未实现命令行读取)";
#endif
        }

        private static Dictionary<int, List<int>> SafeGetListeningPortsByPid(out bool success)
        {
            var result = new Dictionary<int, List<int>>();
            success = false;

#if UNITY_EDITOR_WIN
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return result;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);

                if (string.IsNullOrWhiteSpace(output))
                    return result;

                var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in lines)
                {
                    var line = (raw ?? string.Empty).Trim();
                    if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5)
                        continue;

                    var localAddress = parts[1];
                    var state = parts[3];
                    if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!int.TryParse(parts[4], out var pid) || pid <= 0)
                        continue;

                    int port = SafeParsePortFromEndpoint(localAddress);
                    if (port <= 0)
                        continue;

                    if (!result.TryGetValue(pid, out var list))
                    {
                        list = new List<int>();
                        result[pid] = list;
                    }

                    if (!list.Contains(port))
                        list.Add(port);
                }

                success = true;
            }
            catch
            {
                success = false;
            }
#endif

            return result;
        }

        private static int SafeParsePortFromEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return -1;

            int idx = endpoint.LastIndexOf(':');
            if (idx < 0 || idx >= endpoint.Length - 1)
                return -1;

            var portText = endpoint.Substring(idx + 1).Trim();
            return int.TryParse(portText, out var port) ? port : -1;
        }

        private static bool ContainsIgnoreCase(string text, string token)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

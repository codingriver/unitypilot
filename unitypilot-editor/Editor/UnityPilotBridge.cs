using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SkillEditor.Editor.UnityPilot
{
    // ── Status snapshot returned to the status window ──────────────────────────
    internal struct BridgeStatus
    {
        public bool IsStarted;
        public bool IsWsOpen;
        public bool IsAuthenticated;
        public string SessionId;
        public long LastHeartbeatSentAt;   // unix ms, 0 = never
        public bool IsCompiling;
        public int  LastErrorCount;
        public string PlayModeState;
    }

    // ── Log entry ───────────────────────────────────────────────────────────────
    internal readonly struct BridgeLogEntry
    {
        public readonly DateTime Time;
        public readonly string   Level; // "info" | "warn" | "error"
        public readonly string   Message;
        public readonly bool     IsWireStructured;
        public readonly string   WireDirection;
        public readonly bool     WireIsRaw;
        public readonly long     WireEnvelopeUnixMs;
        public readonly string   WireSessionId;
        public readonly string   WireName;
        public readonly string   WireType;
        public readonly string   WireId;
        public readonly string   WireDetail;

        public BridgeLogEntry(string level, string message)
        {
            Time               = DateTime.Now;
            Level              = level ?? "info";
            Message            = message ?? string.Empty;
            IsWireStructured   = false;
            WireDirection      = "";
            WireIsRaw          = false;
            WireEnvelopeUnixMs = 0;
            WireSessionId      = "";
            WireName           = "";
            WireType           = "";
            WireId             = "";
            WireDetail         = "";
        }

        public static BridgeLogEntry Wire(
            string level,
            string direction,
            bool isRaw,
            BridgeEnvelope env,
            string detail,
            string messageForFilter)
        {
            var sid = env?.sessionId ?? "";
            var nm  = env?.name ?? "";
            var tp  = env?.type ?? "";
            var id  = env?.id ?? "";
            var ts  = env != null ? env.timestamp : 0L;
            return new BridgeLogEntry(
                level ?? "info",
                messageForFilter ?? "",
                true,
                direction ?? "",
                isRaw,
                ts,
                sid,
                nm,
                tp,
                id,
                detail ?? "");
        }

        private BridgeLogEntry(
            string level,
            string message,
            bool isWireStructured,
            string wireDirection,
            bool wireIsRaw,
            long wireEnvelopeUnixMs,
            string wireSessionId,
            string wireName,
            string wireType,
            string wireId,
            string wireDetail)
        {
            Time               = DateTime.Now;
            Level              = level ?? "info";
            Message            = message ?? string.Empty;
            IsWireStructured   = isWireStructured;
            WireDirection      = wireDirection ?? "";
            WireIsRaw          = wireIsRaw;
            WireEnvelopeUnixMs = wireEnvelopeUnixMs;
            WireSessionId      = wireSessionId ?? "";
            WireName           = wireName ?? "";
            WireType           = wireType ?? "";
            WireId             = wireId ?? "";
            WireDetail         = wireDetail ?? "";
        }
    }

    internal sealed class UnityPilotBridge
    {
        private const string DefaultWsHost   = "127.0.0.1";
        private const int    DefaultWsPort   = 8765;
        /// <summary>升级前无项目后缀的全局键，仅用于一次性迁移。</summary>
        private const string LegacyWsHostPrefsKey = "UnityPilot.WsHost";
        private const string LegacyWsPortPrefsKey = "UnityPilot.WsPort";

        private static string _projectPathHashSuffix;

        /// <summary>当前工程路径规范化后的 SHA256 前 8 字节（16 hex），用于 EditorPrefs 键后缀。</summary>
        private static string ProjectPathHashSuffix
        {
            get
            {
                if (_projectPathHashSuffix != null)
                    return _projectPathHashSuffix;

                var root = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
                var normalized = root.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
                using (var sha = SHA256.Create())
                {
                    var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                    var sb = new StringBuilder(16);
                    for (var i = 0; i < 8; i++)
                        sb.Append(digest[i].ToString("x2"));
                    _projectPathHashSuffix = sb.ToString();
                }

                return _projectPathHashSuffix;
            }
        }

        private static string WsHostPrefsKey => $"UnityPilot.WsHost.{ProjectPathHashSuffix}";
        private static string WsPortPrefsKey => $"UnityPilot.WsPort.{ProjectPathHashSuffix}";
        private const int    ReconnectMs      = 2000;
        private const int    HeartbeatIntervalMs = 2000;
        private const int    MaxLogEntries    = 1000;
        private const string DebugLogPrefsKey = "UnityPilot.DebugWireLogs";

        private static readonly Lazy<UnityPilotBridge> Lazy = new(() => new UnityPilotBridge());
        public static UnityPilotBridge Instance => Lazy.Value;

        private readonly UnityPilotCompileService  _compileService    = new();
        private readonly UnityPilotPlayInputService _playInputService  = new();
        private readonly UnityPilotCommandRouter   _router            = new();
        private readonly ConcurrentQueue<Action>   _mainThreadQueue   = new();
        private readonly SemaphoreSlim             _sendLock          = new(1, 1);
        private readonly object                    _logLock           = new();
        private readonly List<BridgeLogEntry>      _logBuffer         = new(MaxLogEntries);

        // Module services (initialized in constructor after Router is available)
        private UnityPilotConsoleService    _consoleService;
        private UnityPilotGameObjectService _gameObjectService;
        private UnityPilotSceneService      _sceneService;
        private UnityPilotComponentService  _componentService;
        private UnityPilotScreenshotService _screenshotService;
        private UnityPilotAssetService      _assetService;
        private UnityPilotPrefabService     _prefabService;
        private UnityPilotMaterialService   _materialService;
        private UnityPilotMenuService       _menuService;
        private UnityPilotPackageService    _packageService;
        private UnityPilotTestService       _testService;
        // P2 services
        private UnityPilotScriptService     _scriptService;
        private UnityPilotCSharpService     _csharpService;
        private UnityPilotReflectionService _reflectionService;
        private UnityPilotBatchService      _batchService;
        private UnityPilotSelectionService  _selectionService;
        private UnityPilotResourceService   _resourceService;
        private UnityPilotBuildService      _buildService;
        private UnityPilotDragDropService   _dragDropService;
        private UnityPilotKeyboardService   _keyboardService;
        private UnityPilotUIToolkitService  _uiToolkitService;
        private UnityPilotEditorService     _editorService;
        private UnityPilotWindowService     _windowService;

        private ClientWebSocket      _ws;
        private CancellationTokenSource _cts;
        private string               _sessionId;
        private bool                 _started;
        private bool                 _isAuthenticated;
        private long                 _lastHeartbeatSentAt;
        private string               _activeSceneName = string.Empty;
        private string               _wsHost = DefaultWsHost;
        private int                  _wsPort = DefaultWsPort;
        private bool                 _debugWireLogsEnabled;

        private UnityPilotBridge()
        {
            LoadWsEndpointFromEditorPrefs();
            _debugWireLogsEnabled = EditorPrefs.GetBool(DebugLogPrefsKey, false);
            RegisterLegacyCommands();
            RegisterModuleServices();
        }

        private void LoadWsEndpointFromEditorPrefs()
        {
            var hKey = WsHostPrefsKey;
            var pKey = WsPortPrefsKey;

            if (EditorPrefs.HasKey(hKey))
            {
                _wsHost = EditorPrefs.GetString(hKey, DefaultWsHost);
                _wsPort = EditorPrefs.GetInt(pKey, DefaultWsPort);
                return;
            }

            if (EditorPrefs.HasKey(LegacyWsHostPrefsKey))
            {
                _wsHost = EditorPrefs.GetString(LegacyWsHostPrefsKey, DefaultWsHost);
                _wsPort = EditorPrefs.GetInt(LegacyWsPortPrefsKey, DefaultWsPort);
                EditorPrefs.SetString(hKey, _wsHost);
                EditorPrefs.SetInt(pKey, _wsPort);
                return;
            }

            _wsHost = DefaultWsHost;
            _wsPort = DefaultWsPort;
        }

        /// <summary>The command router. Modules register handlers via Router.Register().</summary>
        public UnityPilotCommandRouter Router => _router;

        /// <summary>The main-thread work queue. Enqueue actions that must run on Unity's main thread.</summary>
        public ConcurrentQueue<Action> MainThreadQueue => _mainThreadQueue;

        // ── Public status API (called by status window on main thread) ──────────

        public string WsHost => _wsHost;
        public int WsPort => _wsPort;

        /// <summary>当前工程在 EditorPrefs 中使用的路径哈希后缀（16 位 hex），供界面提示。</summary>
        internal static string WsEndpointEditorPrefsKeySuffix => ProjectPathHashSuffix;
        public bool DebugWireLogsEnabled
        {
            get => _debugWireLogsEnabled;
            set
            {
                if (_debugWireLogsEnabled == value) return;
                _debugWireLogsEnabled = value;
                EditorPrefs.SetBool(DebugLogPrefsKey, value);
                AddLog("info", value ? "调试日志已开启（通信命令收发可见）" : "调试日志已关闭");
            }
        }

        /// <summary>
        /// 更新 WebSocket 地址并写入 <see cref="EditorPrefs"/>。
        /// 键为 <c>UnityPilot.WsHost.{项目路径哈希}</c> / <c>UnityPilot.WsPort.{项目路径哈希}</c>，按工程区分。
        /// </summary>
        public void SetWsEndpoint(string host, int port)
        {
            _wsHost = string.IsNullOrWhiteSpace(host) ? DefaultWsHost : host.Trim();
            _wsPort = port <= 0 ? DefaultWsPort : port;
            EditorPrefs.SetString(WsHostPrefsKey, _wsHost);
            EditorPrefs.SetInt(WsPortPrefsKey, _wsPort);
        }

        public string GetServerUrl() => $"ws://{_wsHost}:{_wsPort}";

        public BridgeStatus GetStatus() => new BridgeStatus
        {
            IsStarted           = _started,
            IsWsOpen            = _ws?.State == WebSocketState.Open,
            IsAuthenticated     = _isAuthenticated,
            SessionId           = _sessionId ?? "",
            LastHeartbeatSentAt = _lastHeartbeatSentAt,
            IsCompiling         = _compileService.IsCompiling,
            LastErrorCount      = _compileService.LastErrorCount,
            PlayModeState       = _playInputService.CurrentPlayModeChangedPayload().state,
        };

        public List<BridgeLogEntry> GetLogsCopy()
        {
            lock (_logLock) { return new List<BridgeLogEntry>(_logBuffer); }
        }

        public void ClearLogs()
        {
            lock (_logLock) { _logBuffer.Clear(); }
        }

        public void Restart()
        {
            Stop();
            EnsureStarted();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        public void EnsureStarted()
        {
            if (_started) return;
            _started = true;
            _lastHeartbeatSentAt = 0;
            _sessionId = Guid.NewGuid().ToString("N");
            _cts = new CancellationTokenSource();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += ProcessMainThreadQueue;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            AddLog("info", "Bridge 已启动，准备连接 " + GetServerUrl());
            _ = ConnectLoopAsync(_cts.Token);
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _ws?.Dispose();
            }
            finally
            {
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.update -= ProcessMainThreadQueue;
                CompilationPipeline.compilationStarted -= OnCompilationStarted;
                CompilationPipeline.compilationFinished -= OnCompilationFinished;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
                _started = false;
                _isAuthenticated = false;
                _lastHeartbeatSentAt = 0;
                AddLog("info", "Bridge 已停止");
            }
        }

        private void ProcessMainThreadQueue()
        {
            // Cache scene name while on main thread
            _activeSceneName = SceneManager.GetActiveScene().name;

            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogError($"[UnityPilotBridge] main thread error: {ex}"); }
            }
        }

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _isAuthenticated = false;
                    _ws = new ClientWebSocket();
                    var serverUrl = GetServerUrl();
                    AddLog("info", $"正在连接 {serverUrl} …");
                    await _ws.ConnectAsync(new Uri(serverUrl), token);
                    AddLog("info", "WS 已连接，发送 session.hello");
                    await SendHelloAsync(token);

                    var recvTask = ReceiveLoopAsync(token);
                    var hbTask = HeartbeatLoopAsync(token);
                    await Task.WhenAny(recvTask, hbTask);
                    AddLog("warn", "WS 连接断开，等待重连");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AddLog("warn", $"连接失败：{ex.Message}");
                    Debug.LogWarning($"[UnityPilotBridge] connect failed: {ex.Message}");
                }

                if (!token.IsCancellationRequested)
                    await Task.Delay(ReconnectMs, token).ConfigureAwait(false);
            }
        }

        private async Task SendHelloAsync(CancellationToken token)
        {
            // Use project root (parent of Assets folder) as projectPath
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
            var msg = new HelloMessage
            {
                id = $"h-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                type = "hello",
                name = "session.hello",
                payload = new HelloPayload
                {
                    unityVersion = Application.unityVersion,
                    projectPath = projectRoot,
                    platform = Application.platform == RuntimePlatform.OSXEditor ? "macos" : "windows",
                },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = _sessionId,
            };
            await SendJsonAsync(JsonUtility.ToJson(msg), token);
        }

        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var hb = new HeartbeatMessage
                {
                    id = $"hb-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    type = "heartbeat",
                    name = "session.heartbeat",
                    payload = new HeartbeatPayload(),
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    sessionId = _sessionId,
                };
                await SendJsonAsync(JsonUtility.ToJson(hb), token);
                _lastHeartbeatSentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await Task.Delay(HeartbeatIntervalMs, token).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[65536];
            var sb = new StringBuilder();

            while (!token.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var json = sb.ToString();
                await HandleMessageJsonAsync(json, token);
            }
        }

        private async Task HandleMessageJsonAsync(string json, CancellationToken token)
        {
            var envelope = JsonUtility.FromJson<BridgeEnvelope>(json);
            if (envelope == null) return;

            LogInboundCommand(envelope, json);

            // Handle hello result → authenticate
            if (envelope.type == "result" && envelope.name == "session.hello")
            {
                _isAuthenticated = true;
                AddLog("info", $"认证成功，sessionId={_sessionId}");
                _ = SendEditorStateEventAsync(token);
                return;
            }

            // Absorb heartbeats from server
            if (envelope.type == "heartbeat" ||
                string.Equals(envelope.name, "session.heartbeat", StringComparison.OrdinalIgnoreCase)) return;

            // Only process commands
            if (envelope.type != "command") return;

            // Guard: require authentication
            if (!_isAuthenticated)
            {
                await SendErrorAsync(envelope.id, "INTERNAL_ERROR", "会话未认证", token, envelope.name);
                return;
            }

            var id = envelope.id;
            AddLog("info", $"收到 {envelope.name} id={id}");

            if (!await _router.TryHandleAsync(envelope.name, id, json, token))
            {
                AddLog("warn", $"未知命令：{envelope.name}");
                await SendErrorAsync(id, "COMMAND_NOT_FOUND", $"未注册命令：{envelope.name}", token, envelope.name);
            }
        }

        // ──────────────────────────────── Legacy command registration ────────────────────────────────

        private void RegisterLegacyCommands()
        {
            _router.Register("compile.request",   HandleCompileRequestAsync);
            _router.Register("compile.errors.get", HandleCompileErrorsGetAsync);
            _router.Register("playmode.set",       HandlePlayModeSetAsync);
            _router.Register("mouse.event",        HandleMouseEventAsync);
            _router.Register("editor.state",       HandleEditorStateAsync);
        }

        private void RegisterModuleServices()
        {
            _consoleService = new UnityPilotConsoleService(this);
            _consoleService.RegisterCommands();

            _gameObjectService = new UnityPilotGameObjectService(this);
            _gameObjectService.RegisterCommands();

            _sceneService = new UnityPilotSceneService(this);
            _sceneService.RegisterCommands();

            _componentService = new UnityPilotComponentService(this);
            _componentService.RegisterCommands();

            _screenshotService = new UnityPilotScreenshotService(this);
            _screenshotService.RegisterCommands();

            _assetService = new UnityPilotAssetService(this);
            _assetService.RegisterCommands();

            _prefabService = new UnityPilotPrefabService(this);
            _prefabService.RegisterCommands();

            _materialService = new UnityPilotMaterialService(this);
            _materialService.RegisterCommands();

            _menuService = new UnityPilotMenuService(this);
            _menuService.RegisterCommands();

            _packageService = new UnityPilotPackageService(this);
            _packageService.RegisterCommands();

            _testService = new UnityPilotTestService(this);
            _testService.RegisterCommands();

            _dragDropService = new UnityPilotDragDropService(this);
            _dragDropService.RegisterCommands();

            _uiToolkitService = new UnityPilotUIToolkitService(this);
            _uiToolkitService.RegisterCommands();

            _keyboardService = new UnityPilotKeyboardService(this);
            _keyboardService.RegisterCommands();

            // P2 services
            _scriptService = new UnityPilotScriptService(this);
            _scriptService.RegisterCommands();
            _csharpService = new UnityPilotCSharpService(this);
            _csharpService.RegisterCommands();
            _reflectionService = new UnityPilotReflectionService(this);
            _reflectionService.RegisterCommands();
            _batchService = new UnityPilotBatchService(this);
            _batchService.RegisterCommands();
            _selectionService = new UnityPilotSelectionService(this);
            _selectionService.RegisterCommands();
            _resourceService = new UnityPilotResourceService(this);
            _resourceService.RegisterCommands();
            _buildService = new UnityPilotBuildService(this);
            _buildService.RegisterCommands();

            _editorService = new UnityPilotEditorService(this);
            _editorService.RegisterCommands();

            _windowService = new UnityPilotWindowService(this);
            _windowService.RegisterCommands();
        }

        // ──────────────────────────────── Command handlers ────────────────────────────────

        private async Task HandleCompileRequestAsync(string id, string json, CancellationToken token)
        {
            var command = JsonUtility.FromJson<CompileRequestMessage>(json);
            var requestId = command?.payload?.requestId ?? string.Empty;

            // Fast-path: already compiling
            if (_compileService.IsCompiling)
            {
                await SendErrorAsync(id, "EDITOR_BUSY", "编译进行中，请稍后重试", token, "compile.request");
                return;
            }

            // Schedule compile start on main thread; TCS resolves when action executes
            var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadQueue.Enqueue(() =>
            {
                if (!_compileService.TryBeginCompile(requestId))
                {
                    startTcs.TrySetResult(false);
                    return;
                }
                startTcs.TrySetResult(true);
            });

            var compileStarted = await startTcs.Task;
            if (!compileStarted)
            {
                await SendErrorAsync(id, "EDITOR_BUSY", "编译进行中，请稍后重试", token, "compile.request");
                return;
            }

            // Report "started"
            var startedPayload = _compileService.BuildStartedStatusPayload(requestId);
            await SendEventAsync(
                $"evt-compile-status-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "compile.status", startedPayload, token);

            // Wait for compile to finish (120 s timeout)
            var compileTask = _compileService.WaitForCompileAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120), token);
            var winner = await Task.WhenAny(compileTask, timeoutTask);
            if (winner == timeoutTask)
            {
                await SendErrorAsync(id, "COMMAND_TIMEOUT", "编译超时", token, "compile.request");
                return;
            }

            // Report "finished" + errors
            var finishedPayload = _compileService.BuildFinishedStatusPayload(requestId);
            await SendEventAsync(
                $"evt-compile-status-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "compile.status", finishedPayload, token);

            var errorsPayload = _compileService.BuildCompileErrorsPayload(requestId);
            await SendEventAsync(
                $"evt-compile-errors-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "compile.errors", errorsPayload, token);

            var accepted = new CompileAcceptedPayload { accepted = true, compileRequestId = requestId };
            await SendResultAsync(id, "compile.request", accepted, token);
        }

        private async Task HandleCompileErrorsGetAsync(string id, string json, CancellationToken token)
        {
            var payload = _compileService.BuildLastCompileErrorsPayload();
            await SendResultAsync(id, "compile.errors.get", payload, token);
        }

        private async Task HandlePlayModeSetAsync(string id, string json, CancellationToken token)
        {
            var command = JsonUtility.FromJson<PlayModeSetMessage>(json);
            var action = command?.payload?.action ?? "stop";

            if (action != "play" && action != "stop")
            {
                await SendErrorAsync(id, "INVALID_PAYLOAD", $"非法 PlayMode 动作：{action}", token, "playmode.set");
                return;
            }

            var resultTcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadQueue.Enqueue(() =>
            {
                var payload = _playInputService.SetPlayMode(action);
                resultTcs.TrySetResult(payload);
            });

            var result = await resultTcs.Task;
            await SendResultAsync(id, "playmode.set", result, token);
            await SendPlayModeChangedEventAsync(token);
        }

        private async Task HandleMouseEventAsync(string id, string json, CancellationToken token)
        {
            var command = JsonUtility.FromJson<MouseEventMessage>(json);
            var mousePayload = command?.payload ?? new MouseEventPayload();

            var validButtons = new[] { "left", "middle", "right" };
            if (Array.IndexOf(validButtons, mousePayload.button) < 0)
            {
                await SendErrorAsync(id, "INVALID_PAYLOAD", $"非法鼠标按键：{mousePayload.button}", token, "mouse.event");
                return;
            }

            var resultTcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadQueue.Enqueue(() =>
            {
                var result = _playInputService.HandleMouseEvent(mousePayload);
                resultTcs.TrySetResult(result);
            });

            var mouseResult = await resultTcs.Task;
            await SendResultAsync(id, "mouse.event", mouseResult, token);
        }

        private async Task HandleEditorStateAsync(string id, string json, CancellationToken token)
        {
            var payload = new EditorStatePayload
            {
                connected = true,
                isCompiling = _compileService.IsCompiling,
                playModeState = _playInputService.CurrentPlayModeChangedPayload().state,
                activeScene = _activeSceneName,
            };
            await SendResultAsync(id, "editor.state", payload, token);
        }

        // ──────────────────────────────── Send helpers ────────────────────────────────

        internal async Task SendResultAsync<TPayload>(string id, string name, TPayload payload, CancellationToken token)
        {
            var result = new ResultMessage<TPayload>
            {
                id = id,
                name = name,
                payload = payload,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = _sessionId,
            };
            await SendJsonAsync(JsonUtility.ToJson(result), token);
        }

        internal async Task SendEventAsync<TPayload>(string id, string name, TPayload payload, CancellationToken token)
        {
            var evt = new EventMessage<TPayload>
            {
                id = id,
                name = name,
                payload = payload,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = _sessionId,
            };
            await SendJsonAsync(JsonUtility.ToJson(evt), token);
        }

        internal async Task SendErrorAsync(string id, string code, string message, CancellationToken token,
            string commandName = "")
        {
            var err = new ErrorMessage
            {
                id = id,
                name = "command.error",
                payload = new ErrorPayload
                {
                    code = code,
                    message = message,
                    detail = new ErrorDetailPayload { commandId = id, commandName = commandName },
                },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = _sessionId,
            };
            await SendJsonAsync(JsonUtility.ToJson(err), token);
        }

        private async Task SendEditorStateEventAsync(CancellationToken token)
        {
            var payload = new EditorStatePayload
            {
                connected = true,
                isCompiling = _compileService.IsCompiling,
                playModeState = _playInputService.CurrentPlayModeChangedPayload().state,
                activeScene = _activeSceneName,
            };
            await SendEventAsync(
                $"evt-editor-state-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "editor.state", payload, token);

            // After reconnect, proactively send current compile errors snapshot.
            // Even if _lastErrors is empty (cleared by domain reload), this tells
            // the Python side there are no errors, overriding any stale cache.
            if (!_compileService.IsCompiling)
            {
                var errorsPayload = _compileService.BuildLastCompileErrorsPayload();
                await SendEventAsync(
                    $"evt-compile-errors-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    "compile.errors", errorsPayload, token);
            }
        }

        private async Task SendPlayModeChangedEventAsync(CancellationToken token)
        {
            await SendEventAsync(
                $"evt-playmode-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "playmode.changed",
                _playInputService.CurrentPlayModeChangedPayload(),
                token);
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            _ = SendPlayModeChangedEventAsync(_cts.Token);
        }

        private void OnBeforeAssemblyReload()
        {
            AddLog("info", "Domain Reload 即将开始，发送 domain_reload.starting 事件");
            if (_ws?.State == WebSocketState.Open && _isAuthenticated)
            {
                try
                {
                    var payload = new DomainReloadPayload
                    {
                        phase = "starting",
                        isCompiling = _compileService.IsCompiling,
                        playModeState = _playInputService.CurrentPlayModeChangedPayload().state,
                    };
                    // Synchronous send — we must complete before the domain unloads
                    var msg = new EventMessage<DomainReloadPayload>
                    {
                        id = $"evt-domain-reload-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        name = "domain_reload.starting",
                        payload = payload,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        sessionId = _sessionId,
                    };
                    var json = JsonUtility.ToJson(msg);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                       .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    AddLog("warn", $"发送 domain_reload.starting 失败：{ex.Message}");
                }
            }
        }

        private void OnAfterAssemblyReload()
        {
            AddLog("info", "Domain Reload 完成");
        }

        private void OnCompilationStarted(object _)
        {
            AddLog("info", "Unity 开始编译");
        }

        private void OnCompilationFinished(object _)
        {
            var wsOpen = _ws?.State == WebSocketState.Open;
            if (!wsOpen)
            {
                AddLog("info", "Unity 编译完成（未发送，WS 未连接）");
                return;
            }

            AddLog("info", "Unity 编译完成，准备发送 compile.errors");
            _ = SendCompileFinishedSnapshotAsync(_cts?.Token ?? CancellationToken.None);
        }

        private async Task SendCompileFinishedSnapshotAsync(CancellationToken token)
        {
            try
            {
                if (_compileService.IsCompiling)
                {
                    AddLog("info", "Unity 编译完成（未发送，仍在编译状态）");
                    return;
                }

                var errorsPayload = _compileService.BuildLastCompileErrorsPayload();
                await SendEventAsync(
                    $"evt-compile-errors-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    "compile.errors", errorsPayload, token);
                AddLog("info", $"已发送 compile.errors，errors={errorsPayload.total}");
            }
            catch (Exception ex)
            {
                AddLog("warn", $"编译完成后发送 compile.errors 失败：{ex.Message}");
            }
        }

        /// <summary>Thread-safe WS send guarded by semaphore.</summary>
        private async Task SendJsonAsync(string json, CancellationToken token)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;

            LogOutboundCommand(json);

            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void LogInboundCommand(BridgeEnvelope envelope, string json)
        {
            if (!_debugWireLogsEnabled || envelope == null) return;

            var commandName = string.IsNullOrEmpty(envelope.name) ? "(unknown)" : envelope.name;
            var type = string.IsNullOrEmpty(envelope.type) ? "(unknown)" : envelope.type;

            // 默认过滤心跳日志，避免实时流刷屏。
            if (type == "heartbeat") return;

            var filter = $"[recv] {type} {commandName}";
            AppendLogEntry(BridgeLogEntry.Wire("info", "RX", true, envelope, json, filter));
        }

        private void LogOutboundCommand(string json)
        {
            if (!_debugWireLogsEnabled || string.IsNullOrEmpty(json)) return;

            var envelope = JsonUtility.FromJson<BridgeEnvelope>(json);
            if (envelope == null) return;

            var commandName = string.IsNullOrEmpty(envelope.name) ? "(unknown)" : envelope.name;
            var type = string.IsNullOrEmpty(envelope.type) ? "(unknown)" : envelope.type;

            // 默认过滤心跳日志，避免实时流刷屏。
            if (type == "heartbeat") return;

            var filter = $"[send] {type} {commandName}";
            AppendLogEntry(BridgeLogEntry.Wire("info", "TX", true, envelope, json, filter));
        }

        internal void AddLog(string level, string message)
        {
            AppendLogEntry(new BridgeLogEntry(level, message));
        }

        private void AppendLogEntry(BridgeLogEntry entry)
        {
            lock (_logLock)
            {
                if (_logBuffer.Count >= MaxLogEntries)
                    _logBuffer.RemoveAt(0);
                _logBuffer.Add(entry);
            }

            UnityPilotEditorLog.AppendLine(UnityPilotEditorLog.FormatLine(entry));
        }
    }
}

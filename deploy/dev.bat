@echo off
chcp 65001 >nul
setlocal

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%\..\.."

echo ============================================================
echo   UnityPilot MCP ^— 开发模式启动
echo ============================================================

cd /d "%PROJECT_ROOT%"

REM ── 配置源码路径（src layout） ────────────────────────────────
set "PYTHONPATH=%PROJECT_ROOT%\unitypilot_mcp\src;%PYTHONPATH%"

REM ── 检查 Python ──────────────────────────────────────────────
python --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] 未找到 python 命令，请确认 Python 3.11+ 已加入 PATH
    pause
    exit /b 1
)

REM ── 检查依赖（首次或 requirements 变更时安装）────────────────
python -c "import mcp, websockets" >nul 2>&1
if errorlevel 1 (
    echo [INFO] 正在安装依赖...
    pip install -e . --user -q
    if errorlevel 1 (
        echo [ERROR] 依赖安装失败，请检查网络或手动运行：pip install -e .
        pause
        exit /b 1
    )
)

REM ── 杀掉占用 8765 端口的旧进程 ───────────────────────────────
for /f "tokens=5" %%p in ('netstat -ano 2^>nul ^| findstr ":8765 " ^| findstr "LISTENING"') do (
    echo [INFO] 杀掉旧进程 PID %%p...
    taskkill /PID %%p /F >nul 2>&1
)

REM ── 启动 WS 服务器（后台）────────────────────────────────────
echo [INFO] 启动 WebSocket 服务器 ws://127.0.0.1:8765 ...
start "UnityPilot-WS" /min cmd /c "set PYTHONPATH=%PYTHONPATH%&& python -m unitypilot_mcp.main & pause"

REM ── 等待端口就绪 ─────────────────────────────────────────────
set /a retry=0
:wait_loop
timeout /t 1 /nobreak >nul
netstat -ano 2>nul | findstr ":8765 " | findstr "LISTENING" >nul
if errorlevel 1 (
    set /a retry+=1
    if %retry% lss 10 goto wait_loop
    echo [WARN] 等待端口超时，服务器可能启动失败，请检查 UnityPilot-WS 窗口
) else (
    echo [OK] WebSocket 服务器已就绪
)

REM ── 冒烟测试 ─────────────────────────────────────────────────
echo.
echo [INFO] 运行 MCP 冒烟测试...
python unitypilot_mcp\src\unitypilot_mcp\mcp_smoke_test.py
if errorlevel 1 (
    echo [WARN] 冒烟测试失败，请检查上方输出
) else (
    echo [OK] MCP 服务器协议正常
)

echo.
echo ============================================================
echo   开发环境就绪
echo   WebSocket: ws://127.0.0.1:8765
echo   现在打开 Unity Editor，等待 UnityPilot 自动连接
echo ============================================================
echo.
echo 提示：修改 Python 文件后运行 unitypilot_mcp\deploy\restart_mcp.bat 重启服务器
echo.
pause

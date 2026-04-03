@echo off
chcp 65001 >nul
setlocal

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%\..\.."

echo ============================================================
echo   UnityPilot MCP ^— 重启 WS 服务器
echo ============================================================

cd /d "%PROJECT_ROOT%"
set "PYTHONPATH=%PROJECT_ROOT%\mcp\src;%PYTHONPATH%"

REM ── 杀掉占用 8765 端口的旧进程 ───────────────────────────────
set FOUND=0
for /f "tokens=5" %%p in ('netstat -ano 2^>nul ^| findstr ":8765 " ^| findstr "LISTENING"') do (
    echo [INFO] 停止旧进程 PID %%p...
    taskkill /PID %%p /F >nul 2>&1
    set FOUND=1
)

if "%FOUND%"=="0" (
    echo [INFO] 未发现监听 8765 端口的进程
)

REM ── 稍等端口释放 ─────────────────────────────────────────────
timeout /t 1 /nobreak >nul

REM ── 重启 WS 服务器 ───────────────────────────────────────────
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
    echo [WARN] 端口未就绪，请检查 UnityPilot-WS 窗口
    goto done
)
echo [OK] WebSocket 服务器已重启

:done
echo.
echo 提示：Unity Editor 会自动重新连接（最长约 6 秒）
echo 提示：Claude Code / Cursor 在下次调用工具时会自动重连 MCP 服务器
echo.
pause

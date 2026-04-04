from __future__ import annotations

import asyncio
import json
import logging
import os
import sys
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

from mcp.server.fastmcp import FastMCP

from .server import WsOrchestratorServer
from .tool_facade import McpToolFacade

logger = logging.getLogger("unitypilot.mcp")

# ── Shared server state ──────────────────────────────────────────────────────

_orchestrator: WsOrchestratorServer | None = None
_facade: McpToolFacade | None = None


def _resolve_config() -> tuple[str, int]:
    """Resolve host/port from CLI args (--host/--port) or env vars (UNITYPILOT_HOST/UNITYPILOT_PORT)."""
    host = os.environ.get("UNITYPILOT_HOST", "127.0.0.1")
    port_str = os.environ.get("UNITYPILOT_PORT", "8765")

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--host" and i + 1 < len(args):
            host = args[i + 1]
            i += 2
        elif args[i] == "--port" and i + 1 < len(args):
            port_str = args[i + 1]
            i += 2
        else:
            i += 1

    try:
        port = int(port_str)
    except ValueError:
        logger.error("Invalid port value: %s, falling back to 8765", port_str)
        port = 8765

    return host, port


def _workspace_folder_label() -> str:
    """Folder name of the current working directory (typically the Cursor workspace root)."""
    try:
        name = Path.cwd().resolve().name
    except OSError:
        return ""
    return (name or "").strip()[:256]


def _resolve_mcp_label() -> str:
    """Display name for Unity / diagnostics.

    If ``--label`` is present on the command line, its value is used; otherwise the
    current working directory's folder name (normally the Cursor workspace root).
    """
    args = sys.argv[1:]
    cli_label: str | None = None
    i = 0
    while i < len(args):
        if args[i] == "--label" and i + 1 < len(args):
            cli_label = args[i + 1].strip()
            i += 2
        else:
            i += 1

    if cli_label is not None:
        return cli_label[:256]

    return _workspace_folder_label()


@asynccontextmanager
async def _lifespan(app: FastMCP):
    global _orchestrator, _facade
    host, port = _resolve_config()
    mcp_label = _resolve_mcp_label()
    _orchestrator = WsOrchestratorServer(host=host, port=port, mcp_label=mcp_label)
    _facade = McpToolFacade(_orchestrator)
    task = asyncio.create_task(_orchestrator.start())
    logger.info("unitypilot MCP server started  ws=%s:%s", host, port)
    try:
        yield
    finally:
        _orchestrator.stop()
        try:
            await task
        except (asyncio.CancelledError, Exception):
            pass


mcp = FastMCP("unitypilot", lifespan=_lifespan)


def _get_facade() -> McpToolFacade:
    if _facade is None:
        raise RuntimeError("Server not initialized")
    return _facade


def _payload(r) -> str:
    return json.dumps(
        {
            "ok": r.ok,
            "data": r.data,
            "error": (
                {"code": r.error.code, "message": r.error.message, "detail": r.error.detail}
                if r.error
                else None
            ),
            "requestId": r.request_id,
            "timestamp": r.timestamp,
        },
        ensure_ascii=False,
    )


# ── Tool definitions ─────────────────────────────────────────────────────────


@mcp.tool(description="检查 Unity 连接并返回会话信息。")
async def unity_open_editor(command: str = "", waitForConnectMs: int = 60000) -> str:
    r = await _get_facade().open_editor(command=command, wait_for_connect_ms=waitForConnectMs)
    return _payload(r)


@mcp.tool(description="触发 Unity 编译。")
async def unity_compile() -> str:
    r = await _get_facade().compile()
    return _payload(r)


@mcp.tool(description="获取最近一次编译状态。")
async def unity_compile_status(compileRequestId: str = "") -> str:
    r = await _get_facade().compile_status(compile_request_id=compileRequestId)
    return _payload(r)


@mcp.tool(description="获取最近一次结构化编译错误（仅 live，不回退缓存）。")
async def unity_compile_errors(compileRequestId: str = "") -> str:
    r = await _get_facade().compile_errors(compile_request_id=compileRequestId)
    return _payload(r)


@mcp.tool(
    description=(
        "诊断 MCP 连接/会话/超时/编译状态。"
        "返回 paths.unityProjectAbsolute（当前 Unity 工程绝对路径）与 paths.mcpProcessWorkingDirectory（MCP Python 进程当前工作目录，多为 Cursor 工作区根目录）。"
    ),
)
async def unity_mcp_status() -> str:
    r = await _get_facade().mcp_status()
    return _payload(r)


@mcp.tool(description="启动自动修复循环。")
async def unity_auto_fix_start(maxIterations: int = 20, stopWhenNoError: bool = True) -> str:
    r = await _get_facade().auto_fix_start(max_iterations=maxIterations, stop_when_no_error=stopWhenNoError)
    return _payload(r)


@mcp.tool(description="停止自动修复循环。")
async def unity_auto_fix_stop(loopId: str) -> str:
    r = await _get_facade().auto_fix_stop(loop_id=loopId)
    return _payload(r)


@mcp.tool(description="读取自动修复循环状态。")
async def unity_auto_fix_status() -> str:
    r = await _get_facade().auto_fix_status()
    return _payload(r)


@mcp.tool(description="进入 PlayMode。")
async def unity_playmode_start() -> str:
    r = await _get_facade().playmode_start()
    return _payload(r)


@mcp.tool(description="退出 PlayMode。")
async def unity_playmode_stop() -> str:
    r = await _get_facade().playmode_stop()
    return _payload(r)


@mcp.tool(description="执行 Unity 编辑器鼠标动作。支持 elementName 按名称自动定位 UIToolkit 元素中心坐标（无需手动算坐标）。")
async def unity_mouse_event(
    action: str,
    button: str,
    x: float = 0,
    y: float = 0,
    targetWindow: str = "",
    modifiers: list[str] | None = None,
    scrollDeltaX: float = 0.0,
    scrollDeltaY: float = 0.0,
    elementName: str = "",
    elementIndex: int = -1,
) -> str:
    r = await _get_facade().mouse_event(
        action=action,
        button=button,
        x=x,
        y=y,
        target_window=targetWindow,
        modifiers=modifiers,
        scroll_delta_x=scrollDeltaX,
        scroll_delta_y=scrollDeltaY,
        element_name=elementName,
        element_index=elementIndex,
    )
    return _payload(r)


@mcp.tool(description="导出 Unity 编辑器窗口的 UIToolkit VisualElement 树结构。")
async def unity_uitoolkit_dump(targetWindow: str, maxDepth: int = 10) -> str:
    r = await _get_facade().uitoolkit_dump(target_window=targetWindow, max_depth=maxDepth)
    return _payload(r)


@mcp.tool(description="在 Unity 编辑器窗口中查询 UIToolkit 元素（按名称、类名、类型或文本过滤）。")
async def unity_uitoolkit_query(
    targetWindow: str,
    nameFilter: str = "",
    classFilter: str = "",
    typeFilter: str = "",
    textFilter: str = "",
) -> str:
    r = await _get_facade().uitoolkit_query(
        target_window=targetWindow, name_filter=nameFilter,
        class_filter=classFilter, type_filter=typeFilter,
        text_filter=textFilter,
    )
    return _payload(r)


@mcp.tool(description="向 Unity 编辑器窗口中的 UIToolkit 元素派发合成事件。")
async def unity_uitoolkit_event(
    targetWindow: str,
    eventType: str,
    elementName: str = "",
    elementIndex: int = -1,
    keyCode: str = "",
    character: str = "",
    mouseButton: int = 0,
    mouseX: float = 0,
    mouseY: float = 0,
    modifiers: list[str] | None = None,
) -> str:
    r = await _get_facade().uitoolkit_event(
        target_window=targetWindow, event_type=eventType,
        element_name=elementName, element_index=elementIndex,
        key_code=keyCode, character=character,
        mouse_button=mouseButton, mouse_x=mouseX, mouse_y=mouseY,
        modifiers=modifiers,
    )
    return _payload(r)


@mcp.tool(description="滚动 UIToolkit ScrollView 到指定位置或增量偏移。mode='absolute' 使用 scrollToX/scrollToY，mode='delta' 使用 deltaX/deltaY。")
async def unity_uitoolkit_scroll(
    targetWindow: str,
    elementName: str = "",
    elementIndex: int = -1,
    scrollToX: float = -1,
    scrollToY: float = -1,
    deltaX: float = 0,
    deltaY: float = 0,
    mode: str = "absolute",
) -> str:
    r = await _get_facade().uitoolkit_scroll(
        target_window=targetWindow, element_name=elementName,
        element_index=elementIndex, scroll_to_x=scrollToX,
        scroll_to_y=scrollToY, delta_x=deltaX, delta_y=deltaY,
        mode=mode,
    )
    return _payload(r)


@mcp.tool(description="直接设置 UIToolkit 元素的值（TextField/Toggle/Slider/Dropdown/IntegerField/FloatField/Foldout）。通过 elementName 或 elementIndex 定位元素。")
async def unity_uitoolkit_set_value(
    targetWindow: str,
    value: str,
    elementName: str = "",
    elementIndex: int = -1,
) -> str:
    r = await _get_facade().uitoolkit_set_value(
        target_window=targetWindow, value=value,
        element_name=elementName, element_index=elementIndex,
    )
    return _payload(r)


@mcp.tool(description="对 UIToolkit 元素执行交互（click/focus/blur），自动计算元素中心坐标，无需手动传坐标。")
async def unity_uitoolkit_interact(
    targetWindow: str,
    action: str = "click",
    elementName: str = "",
    elementIndex: int = -1,
) -> str:
    r = await _get_facade().uitoolkit_interact(
        target_window=targetWindow, action=action,
        element_name=elementName, element_index=elementIndex,
    )
    return _payload(r)


@mcp.tool(description="轮询等待 UIToolkit UI 条件满足。conditionType: element_exists(元素存在) | element_not_exists(元素消失) | element_value(值匹配) | text_contains(文本包含)。")
async def unity_wait_condition(
    targetWindow: str,
    conditionType: str = "element_exists",
    elementName: str = "",
    textContains: str = "",
    valueEquals: str = "",
    typeFilter: str = "",
    timeoutS: float = 30,
    pollIntervalS: float = 0.5,
) -> str:
    r = await _get_facade().wait_condition(
        target_window=targetWindow, condition_type=conditionType,
        element_name=elementName, text_contains=textContains,
        value_equals=valueEquals, type_filter=typeFilter,
        timeout_s=timeoutS, poll_interval_s=pollIntervalS,
    )
    return _payload(r)


@mcp.tool(description="预检测试环境就绪：检查 Unity 连接 + 编译完成 + 编辑模式。返回 ready=true/false 及各项状态。")
async def unity_ensure_ready(timeoutS: float = 120) -> str:
    r = await _get_facade().ensure_ready(timeout_s=timeoutS)
    return _payload(r)


@mcp.tool(description="带超时看门狗执行 MCP 工具。超时→尝试重连 Unity→重试→总时间超限则跳过。用于自动化测试流水线防卡死。")
async def unity_task_execute(
    taskName: str,
    toolName: str,
    toolArgs: dict | None = None,
    timeoutS: float = 600,
    maxTotalS: float = 1200,
    retryCount: int = 1,
    restartUnityOnTimeout: bool = True,
) -> str:
    r = await _get_facade().task_execute(
        task_name=taskName, tool_name=toolName,
        tool_args=toolArgs, timeout_s=timeoutS,
        max_total_s=maxTotalS, retry_count=retryCount,
        restart_unity_on_timeout=restartUnityOnTimeout,
    )
    return _payload(r)


@mcp.tool(description="执行 Unity 编辑器拖放操作。")
async def unity_drag_drop(
    sourceWindow: str,
    targetWindow: str,
    dragType: str,
    fromX: float,
    fromY: float,
    toX: float,
    toY: float,
    assetPaths: list[str] | None = None,
    gameObjectIds: list[int] | None = None,
    customData: str = "",
    modifiers: list[str] | None = None,
) -> str:
    r = await _get_facade().drag_drop(
        source_window=sourceWindow,
        target_window=targetWindow,
        drag_type=dragType,
        from_x=fromX,
        from_y=fromY,
        to_x=toX,
        to_y=toY,
        asset_paths=assetPaths,
        game_object_ids=gameObjectIds,
        custom_data=customData,
        modifiers=modifiers,
    )
    return _payload(r)


@mcp.tool(description="执行 Unity 编辑器键盘动作。")
async def unity_keyboard_event(
    action: str,
    targetWindow: str,
    keyCode: str = "",
    character: str = "",
    text: str = "",
    modifiers: list[str] | None = None,
) -> str:
    r = await _get_facade().keyboard_event(
        action=action,
        target_window=targetWindow,
        key_code=keyCode,
        character=character,
        text=text,
        modifiers=modifiers,
    )
    return _payload(r)


@mcp.tool(description="列出 Unity 编辑器中所有打开的窗口（支持按类型和标题过滤），返回 instanceId/类型/标题/坐标等信息，用于窗口定位。")
async def unity_editor_windows_list(typeFilter: str = "", titleFilter: str = "") -> str:
    r = await _get_facade().editor_windows_list(type_filter=typeFilter, title_filter=titleFilter)
    return _payload(r)


@mcp.tool(description="获取 Unity 编辑器状态快照。")
async def unity_editor_state() -> str:
    r = await _get_facade().editor_state()
    return _payload(r)


# ── M07 Console 日志读取 ─────────────────────────────────────────────────────


@mcp.tool(description="获取 Unity 控制台日志列表，支持按类型过滤和数量限制。")
async def unity_console_get_logs(logType: str = "", count: int = 100) -> str:
    r = await _get_facade().console_get_logs(log_type=logType, count=count)
    return _payload(r)


@mcp.tool(description="清空 Unity 控制台日志。")
async def unity_console_clear() -> str:
    r = await _get_facade().console_clear()
    return _payload(r)


# ── M08 GameObject 操作 ──────────────────────────────────────────────────────


@mcp.tool(description="在 Unity 场景中创建新的 GameObject。")
async def unity_gameobject_create(
    name: str = "New GameObject",
    parentId: int = 0,
    primitiveType: str = "",
) -> str:
    r = await _get_facade().gameobject_create(name=name, parent_id=parentId, primitive_type=primitiveType)
    return _payload(r)


@mcp.tool(description="在 Unity 场景中查找 GameObject，支持按名称、标签或 InstanceID 查找。")
async def unity_gameobject_find(name: str = "", tag: str = "", instanceId: int = 0) -> str:
    r = await _get_facade().gameobject_find(name=name, tag=tag, instance_id=instanceId)
    return _payload(r)


@mcp.tool(description="修改 Unity 场景中 GameObject 的属性（名称、标签、层级、激活状态等）。")
async def unity_gameobject_modify(
    instanceId: int,
    name: str | None = None,
    tag: str | None = None,
    layer: int | None = None,
    activeSelf: bool | None = None,
    isStatic: bool | None = None,
    parentId: int | None = None,
) -> str:
    r = await _get_facade().gameobject_modify(
        instance_id=instanceId, name=name, tag=tag, layer=layer,
        active_self=activeSelf, is_static=isStatic, parent_id=parentId,
    )
    return _payload(r)


@mcp.tool(description="销毁 Unity 场景中的 GameObject。")
async def unity_gameobject_delete(instanceId: int) -> str:
    r = await _get_facade().gameobject_delete(instance_id=instanceId)
    return _payload(r)


@mcp.tool(description="修改 Unity 场景中 GameObject 的变换（位置、旋转、缩放）。")
async def unity_gameobject_move(
    instanceId: int,
    position: dict | None = None,
    rotation: dict | None = None,
    scale: dict | None = None,
) -> str:
    r = await _get_facade().gameobject_move(
        instance_id=instanceId, position=position, rotation=rotation, scale=scale,
    )
    return _payload(r)


@mcp.tool(description="复制 Unity 场景中的 GameObject（包含所有子对象和组件）。")
async def unity_gameobject_duplicate(instanceId: int) -> str:
    r = await _get_facade().gameobject_duplicate(instance_id=instanceId)
    return _payload(r)


# ── M09 Scene 管理 ──────────────────────────────────────────────────────────


@mcp.tool(description="在 Unity 中新建空场景。")
async def unity_scene_create(sceneName: str = "") -> str:
    r = await _get_facade().scene_create(scene_name=sceneName)
    return _payload(r)


@mcp.tool(description="在 Unity 中打开指定路径的场景。")
async def unity_scene_open(scenePath: str, mode: str = "single") -> str:
    r = await _get_facade().scene_open(scene_path=scenePath, mode=mode)
    return _payload(r)


@mcp.tool(description="保存当前 Unity 场景或指定路径的场景。")
async def unity_scene_save(scenePath: str = "") -> str:
    r = await _get_facade().scene_save(scene_path=scenePath)
    return _payload(r)


@mcp.tool(description="加载 Unity 场景（支持叠加模式或单场景模式）。")
async def unity_scene_load(scenePath: str, mode: str = "additive") -> str:
    r = await _get_facade().scene_load(scene_path=scenePath, mode=mode)
    return _payload(r)


@mcp.tool(description="设置指定场景为 Unity 当前激活场景。")
async def unity_scene_set_active(scenePath: str) -> str:
    r = await _get_facade().scene_set_active(scene_path=scenePath)
    return _payload(r)


@mcp.tool(description="获取 Unity 当前所有已打开场景列表。")
async def unity_scene_list() -> str:
    r = await _get_facade().scene_list()
    return _payload(r)


@mcp.tool(description="卸载 Unity 场景（可选择从层级视图中移除）。")
async def unity_scene_unload(scenePath: str, removeScene: bool = False) -> str:
    r = await _get_facade().scene_unload(scene_path=scenePath, remove_scene=removeScene)
    return _payload(r)


@mcp.tool(
    description=(
        "确保并打开用于自动化/验收的空场景：若磁盘上已有资源则单场景打开；否则新建 EmptyScene 并保存。"
        "默认 Assets/unitypilot-test.unity。返回 ensureAction: opened|created 与 scene 信息。"
    ),
)
async def unity_scene_ensure_test(
    sceneName: str = "unitypilot-test",
    scenePath: str = "",
) -> str:
    r = await _get_facade().scene_ensure_test(scene_name=sceneName, scene_path=scenePath)
    return _payload(r)


# ── M10 Component 操作 ──────────────────────────────────────────────────────


@mcp.tool(description="在指定 GameObject 上添加组件。")
async def unity_component_add(gameObjectId: int, componentType: str) -> str:
    r = await _get_facade().component_add(game_object_id=gameObjectId, component_type=componentType)
    return _payload(r)


@mcp.tool(description="从指定 GameObject 上移除组件。")
async def unity_component_remove(gameObjectId: int, componentType: str, componentIndex: int = 0) -> str:
    r = await _get_facade().component_remove(
        game_object_id=gameObjectId, component_type=componentType, component_index=componentIndex,
    )
    return _payload(r)


@mcp.tool(description="获取指定 GameObject 上组件的序列化属性。")
async def unity_component_get(gameObjectId: int, componentType: str, componentIndex: int = 0) -> str:
    r = await _get_facade().component_get(
        game_object_id=gameObjectId, component_type=componentType, component_index=componentIndex,
    )
    return _payload(r)


@mcp.tool(description="修改指定 GameObject 上组件的属性。")
async def unity_component_modify(
    gameObjectId: int,
    componentType: str,
    properties: dict,
    componentIndex: int = 0,
) -> str:
    r = await _get_facade().component_modify(
        game_object_id=gameObjectId, component_type=componentType,
        properties=properties, component_index=componentIndex,
    )
    return _payload(r)


@mcp.tool(description="列出指定 GameObject 上的所有组件。")
async def unity_component_list(gameObjectId: int) -> str:
    r = await _get_facade().component_list(game_object_id=gameObjectId)
    return _payload(r)


# ── M11 截图能力 ────────────────────────────────────────────────────────────


@mcp.tool(description="截取 Unity Game 视图画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_game_view(
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
) -> str:
    r = await _get_facade().screenshot_game_view(width=width, height=height, format=format, quality=quality)
    return _payload(r)


@mcp.tool(description="截取 Unity Scene 视图画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_scene_view(
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
) -> str:
    r = await _get_facade().screenshot_scene_view(width=width, height=height, format=format, quality=quality)
    return _payload(r)


@mcp.tool(description="截取指定 Camera 的画面，返回 Base64 编码的图像数据。")
async def unity_screenshot_camera(
    cameraName: str,
    width: int = 1280,
    height: int = 720,
    format: str = "png",
    quality: int = 75,
) -> str:
    r = await _get_facade().screenshot_camera(
        camera_name=cameraName, width=width, height=height, format=format, quality=quality,
    )
    return _payload(r)


# ── M12 Asset 管理 ──────────────────────────────────────────────────────────


@mcp.tool(description="在 Unity 资源数据库中搜索资源，支持按名称和类型过滤。")
async def unity_asset_find(query: str, assetType: str = "") -> str:
    r = await _get_facade().asset_find(query=query, asset_type=assetType)
    return _payload(r)


@mcp.tool(description="在 Unity Assets 目录下创建新文件夹。")
async def unity_asset_create_folder(parentFolder: str, newFolderName: str) -> str:
    r = await _get_facade().asset_create_folder(parent_folder=parentFolder, new_folder_name=newFolderName)
    return _payload(r)


@mcp.tool(description="复制 Unity 资源到指定路径。")
async def unity_asset_copy(sourcePath: str, destinationPath: str) -> str:
    r = await _get_facade().asset_copy(source_path=sourcePath, destination_path=destinationPath)
    return _payload(r)


@mcp.tool(description="移动 Unity 资源到指定路径。")
async def unity_asset_move(sourcePath: str, destinationPath: str) -> str:
    r = await _get_facade().asset_move(source_path=sourcePath, destination_path=destinationPath)
    return _payload(r)


@mcp.tool(description="删除指定路径的 Unity 资源。")
async def unity_asset_delete(assetPath: str) -> str:
    r = await _get_facade().asset_delete(asset_path=assetPath)
    return _payload(r)


@mcp.tool(description="触发 Unity 资源数据库刷新（AssetDatabase.Refresh）。")
async def unity_asset_refresh() -> str:
    r = await _get_facade().asset_refresh()
    return _payload(r)


@mcp.tool(
    description=(
        "在 Cursor/IDE 中改完或新建完本轮所有脚本并全部保存后，再调用一次（不要每文件一调）："
        "先等待 delayS 秒（默认 2，缓解落盘延迟），再 AssetDatabase.Refresh；"
        "triggerCompile=true 时再触发 unity_compile（含 Refresh+脚本编译）。"
        "随后可 unity_compile_wait 确认编译结束。避免 Unity 无焦点时迟迟不导入。"
    ),
)
async def unity_sync_after_disk_write(delayS: float = 2.0, triggerCompile: bool = False) -> str:
    r = await _get_facade().sync_after_disk_write(delay_s=delayS, trigger_compile=triggerCompile)
    return _payload(r)


@mcp.tool(description="获取 Unity 资源的元数据信息（GUID、类型、大小等）。")
async def unity_asset_get_info(assetPath: str) -> str:
    r = await _get_facade().asset_get_info(asset_path=assetPath)
    return _payload(r)


@mcp.tool(description="搜索 Unity 内置资源（如默认材质、Shader、字体等）。")
async def unity_asset_find_built_in(query: str = "", assetType: str = "") -> str:
    r = await _get_facade().asset_find_built_in(query=query, asset_type=assetType)
    return _payload(r)


@mcp.tool(description="获取 Unity 资源的序列化属性数据（SerializedObject 深度读取）。")
async def unity_asset_get_data(
    assetPath: str = "",
    gameObjectId: int = 0,
    componentType: str = "",
    componentIndex: int = 0,
    maxDepth: int = 10,
) -> str:
    r = await _get_facade().asset_get_data(
        asset_path=assetPath, game_object_id=gameObjectId,
        component_type=componentType, component_index=componentIndex,
        max_depth=maxDepth,
    )
    return _payload(r)


@mcp.tool(description="修改 Unity 资源的序列化属性数据（SerializedObject 深度写入）。")
async def unity_asset_modify_data(
    properties: list[dict],
    assetPath: str = "",
    gameObjectId: int = 0,
    componentType: str = "",
    componentIndex: int = 0,
) -> str:
    r = await _get_facade().asset_modify_data(
        properties=properties, asset_path=assetPath,
        game_object_id=gameObjectId, component_type=componentType,
        component_index=componentIndex,
    )
    return _payload(r)


# ── M13 Prefab 操作 ─────────────────────────────────────────────────────────


@mcp.tool(description="将场景中的 GameObject 创建为 Prefab 资源。")
async def unity_prefab_create(sourceGameObjectId: int, prefabPath: str) -> str:
    r = await _get_facade().prefab_create(source_game_object_id=sourceGameObjectId, prefab_path=prefabPath)
    return _payload(r)


@mcp.tool(description="在场景中实例化指定路径的 Prefab。")
async def unity_prefab_instantiate(prefabPath: str, parentId: int = 0) -> str:
    r = await _get_facade().prefab_instantiate(prefab_path=prefabPath, parent_id=parentId)
    return _payload(r)


@mcp.tool(description="进入 Prefab 编辑模式。")
async def unity_prefab_open(prefabPath: str) -> str:
    r = await _get_facade().prefab_open(prefab_path=prefabPath)
    return _payload(r)


@mcp.tool(description="退出 Prefab 编辑模式。")
async def unity_prefab_close() -> str:
    r = await _get_facade().prefab_close()
    return _payload(r)


@mcp.tool(description="保存当前 Prefab 编辑模式下的修改。")
async def unity_prefab_save() -> str:
    r = await _get_facade().prefab_save()
    return _payload(r)


# ── M14 Material 与 Shader ──────────────────────────────────────────────────


@mcp.tool(description="创建新的 Unity 材质资源并指定 Shader。")
async def unity_material_create(materialPath: str, shaderName: str = "Standard") -> str:
    r = await _get_facade().material_create(material_path=materialPath, shader_name=shaderName)
    return _payload(r)


@mcp.tool(description="修改 Unity 材质的属性（颜色、纹理、数值等）。")
async def unity_material_modify(materialPath: str, properties: dict) -> str:
    r = await _get_facade().material_modify(material_path=materialPath, properties=properties)
    return _payload(r)


@mcp.tool(description="将材质分配给场景中 GameObject 的渲染器。")
async def unity_material_assign(targetGameObjectId: int, materialPath: str, materialIndex: int = 0) -> str:
    r = await _get_facade().material_assign(
        target_game_object_id=targetGameObjectId, material_path=materialPath, material_index=materialIndex,
    )
    return _payload(r)


@mcp.tool(description="获取 Unity 材质的详细属性信息。")
async def unity_material_get(materialPath: str) -> str:
    r = await _get_facade().material_get(material_path=materialPath)
    return _payload(r)


@mcp.tool(description="列出 Unity 中所有可用的 Shader。")
async def unity_shader_list() -> str:
    r = await _get_facade().shader_list()
    return _payload(r)


# ── M15 菜单项执行 ──────────────────────────────────────────────────────────


@mcp.tool(description="执行 Unity 编辑器中指定路径的菜单项。")
async def unity_menu_execute(menuPath: str) -> str:
    r = await _get_facade().menu_execute(menu_path=menuPath)
    return _payload(r)


@mcp.tool(description="列出 Unity 编辑器中所有可用的菜单项。")
async def unity_menu_list() -> str:
    r = await _get_facade().menu_list()
    return _payload(r)


# ── M16 Package 管理 ────────────────────────────────────────────────────────


@mcp.tool(description="通过 Unity Package Manager 添加包（支持名称、版本或 Git URL）。")
async def unity_package_add(packageName: str, version: str = "") -> str:
    r = await _get_facade().package_add(package_name=packageName, version=version)
    return _payload(r)


@mcp.tool(description="通过 Unity Package Manager 移除已安装的包。")
async def unity_package_remove(packageName: str) -> str:
    r = await _get_facade().package_remove(package_name=packageName)
    return _payload(r)


@mcp.tool(description="列出 Unity 项目中所有已安装的包。")
async def unity_package_list() -> str:
    r = await _get_facade().package_list()
    return _payload(r)


@mcp.tool(description="在 Unity Package Manager 注册表中搜索包。")
async def unity_package_search(query: str) -> str:
    r = await _get_facade().package_search(query=query)
    return _payload(r)


# ── M17 测试运行 ────────────────────────────────────────────────────────────


@mcp.tool(description="运行 Unity 测试（支持 EditMode 和 PlayMode）。")
async def unity_test_run(testMode: str = "EditMode", testFilter: str = "") -> str:
    r = await _get_facade().test_run(test_mode=testMode, test_filter=testFilter)
    return _payload(r)


@mcp.tool(description="获取最近一次 Unity 测试运行的结果。")
async def unity_test_results() -> str:
    r = await _get_facade().test_results()
    return _payload(r)


@mcp.tool(description="列出 Unity 项目中所有可用的测试用例。")
async def unity_test_list(testMode: str = "EditMode") -> str:
    r = await _get_facade().test_list(test_mode=testMode)
    return _payload(r)


# ── M18 脚本读写 ────────────────────────────────────────────────────────────


@mcp.tool(description="读取 Unity 项目中指定路径的 C# 脚本内容。")
async def unity_script_read(scriptPath: str) -> str:
    r = await _get_facade().script_read(script_path=scriptPath)
    return _payload(r)


@mcp.tool(description="在 Unity 项目中创建新的 C# 脚本文件。")
async def unity_script_create(scriptPath: str, content: str = "") -> str:
    r = await _get_facade().script_create(script_path=scriptPath, content=content)
    return _payload(r)


@mcp.tool(description="更新 Unity 项目中已有 C# 脚本文件的内容。")
async def unity_script_update(scriptPath: str, content: str) -> str:
    r = await _get_facade().script_update(script_path=scriptPath, content=content)
    return _payload(r)


@mcp.tool(description="删除 Unity 项目中指定路径的 C# 脚本文件。")
async def unity_script_delete(scriptPath: str) -> str:
    r = await _get_facade().script_delete(script_path=scriptPath)
    return _payload(r)


# ── M19 C# 代码执行 ─────────────────────────────────────────────────────────


@mcp.tool(description="在 Unity 编辑器中实时执行 C# 代码片段（通过 Roslyn），返回执行结果。")
async def unity_csharp_execute(code: str, timeoutSeconds: int = 10) -> str:
    r = await _get_facade().csharp_execute(code=code, timeout_seconds=timeoutSeconds)
    return _payload(r)


@mcp.tool(description="查询 C# 代码执行任务的当前状态。")
async def unity_csharp_status(executionId: str) -> str:
    r = await _get_facade().csharp_status(execution_id=executionId)
    return _payload(r)


@mcp.tool(description="终止正在运行的 C# 代码执行任务。")
async def unity_csharp_abort(executionId: str) -> str:
    r = await _get_facade().csharp_abort(execution_id=executionId)
    return _payload(r)


# ── M20 反射调用 ────────────────────────────────────────────────────────────


@mcp.tool(description="通过反射搜索 Unity 程序集中的类和方法。")
async def unity_reflection_find(typeName: str, methodName: str = "") -> str:
    r = await _get_facade().reflection_find(type_name=typeName, method_name=methodName)
    return _payload(r)


@mcp.tool(description="通过反射动态调用 Unity 程序集中的方法。")
async def unity_reflection_call(
    typeName: str,
    methodName: str,
    parameters: list | None = None,
    isStatic: bool = True,
    targetInstancePath: str = "",
) -> str:
    r = await _get_facade().reflection_call(
        type_name=typeName, method_name=methodName, parameters=parameters,
        is_static=isStatic, target_instance_path=targetInstancePath,
    )
    return _payload(r)


# ── M21 批量操作 ────────────────────────────────────────────────────────────


@mcp.tool(description="批量执行多个 Unity 操作指令（支持顺序或并行模式）。")
async def unity_batch_execute(
    operations: list,
    mode: str = "sequential",
    stopOnError: bool = True,
) -> str:
    r = await _get_facade().batch_execute(operations=operations, mode=mode, stop_on_error=stopOnError)
    return _payload(r)


@mcp.tool(description="取消正在执行的批量操作。")
async def unity_batch_cancel(batchId: str) -> str:
    r = await _get_facade().batch_cancel(batch_id=batchId)
    return _payload(r)


@mcp.tool(description="查询批量操作的执行结果。")
async def unity_batch_results(batchId: str) -> str:
    r = await _get_facade().batch_results(batch_id=batchId)
    return _payload(r)


# ── M22 Selection 管理 ──────────────────────────────────────────────────────


@mcp.tool(description="获取 Unity 编辑器当前选中的 GameObject 和资源列表。")
async def unity_selection_get() -> str:
    r = await _get_facade().selection_get()
    return _payload(r)


@mcp.tool(description="设置 Unity 编辑器的选中项（支持 InstanceID 列表或资源路径列表）。")
async def unity_selection_set(
    gameObjectIds: list[int] | None = None,
    assetPaths: list[str] | None = None,
) -> str:
    r = await _get_facade().selection_set(game_object_ids=gameObjectIds, asset_paths=assetPaths)
    return _payload(r)


@mcp.tool(description="清空 Unity 编辑器的当前选中项。")
async def unity_selection_clear() -> str:
    r = await _get_facade().selection_clear()
    return _payload(r)


# ── M23 MCP Resources ──────────────────────────────────────────────────────


@mcp.resource("unity://scenes/hierarchy", description="Unity 当前场景的 GameObject 层级树。")
async def resource_scene_hierarchy() -> str:
    r = await _get_facade().resource_scene_hierarchy()
    return _payload(r)


@mcp.resource("unity://console/logs", description="Unity 控制台最近日志。")
async def resource_console_logs() -> str:
    r = await _get_facade().resource_console_logs()
    return _payload(r)


@mcp.resource("unity://editor/state", description="Unity 编辑器当前状态快照。")
async def resource_editor_state() -> str:
    r = await _get_facade().resource_editor_state()
    return _payload(r)


@mcp.resource("unity://packages/list", description="Unity 项目已安装的包列表。")
async def resource_packages() -> str:
    r = await _get_facade().resource_packages()
    return _payload(r)


@mcp.resource("unity://build/status", description="Unity 当前构建状态。")
async def resource_build_status() -> str:
    r = await _get_facade().resource_build_status()
    return _payload(r)


@mcp.resource(
    "unity://diagnostics/unitypilot-logs-tab",
    description="UnityPilot 诊断日志标签页布局快照（横向滚动风险、滚动位置等，需打开窗口并切到该标签）。",
)
async def resource_unitypilot_logs_tab() -> str:
    r = await _get_facade().resource_unitypilot_logs_tab()
    return _payload(r)


@mcp.resource(
    "unity://diagnostics/window",
    description="UnityPilot 全窗口级布局诊断快照（健康分、各区域宽度溢出检测、编译状态、代码版本、Domain Reload 纪元）。",
)
async def resource_window_diagnostics() -> str:
    r = await _get_facade().resource_window_diagnostics()
    return _payload(r)


@mcp.resource(
    "unity://console/summary",
    description="Unity 控制台日志按类型统计（logCount/warningCount/errorCount/assertCount）。",
)
async def resource_console_summary() -> str:
    r = await _get_facade().resource_console_summary()
    return _payload(r)


# ── M26 验收自动化工具 ─────────────────────────────────────────────────────


@mcp.tool(
    description=(
        "等待 Unity 脚本编译结束。优先使用 Bridge 推送的 compile.started/finished 与 compile.pipeline.* 信号（wait_for_compile_idle），"
        "再以指数退避轮询 resource.editorState。preferEvents=false 可仅用轮询。返回 waitMode：immediate|event|poll|timeout。"
    ),
)
async def unity_compile_wait(
    timeoutS: float = 120,
    pollIntervalS: float = 1.0,
    preferEvents: bool = True,
) -> str:
    r = await _get_facade().compile_wait(
        timeout_s=timeoutS,
        poll_interval_s=pollIntervalS,
        prefer_events=preferEvents,
    )
    return _payload(r)


@mcp.tool(
    description=(
        "在 Unity 编辑器侧单命令阻塞等待：直到 EditorApplication.isCompiling 为 false（任意来源的编译）。"
        "timeoutMs 默认 120000。适合需要与编辑器内部状态严格对齐的场景。"
    ),
)
async def unity_compile_wait_editor(timeoutMs: int = 120000) -> str:
    r = await _get_facade().compile_wait_editor(timeout_ms=timeoutMs)
    return _payload(r)


@mcp.tool(description="截取 Unity 编辑器窗口（EditorWindow）画面，返回 Base64 编码的 PNG。通过窗口标题匹配。screenshotDegrade: none|auto|scene|minimal — auto 在无法截取窗口时降级为 Scene 视图或占位图。")
async def unity_screenshot_editor_window(
    windowTitle: str = "UnityPilot",
    screenshotDegrade: str = "auto",
) -> str:
    r = await _get_facade().screenshot_editor_window(window_title=windowTitle, degrade=screenshotDegrade)
    return _payload(r)


@mcp.tool(description="一次性获取全部诊断信息：窗口布局诊断 + 控制台摘要 + 编辑器状态。免去多次调用。")
async def unity_batch_diagnostics() -> str:
    r = await _get_facade().batch_diagnostics()
    return _payload(r)


@mcp.tool(description="全自动窗口验收：等编译完成 → 截图（可选） + 窗口布局诊断 + 控制台摘要，一次调用完成所有验收步骤。screenshotDegrade 同 unity_screenshot_editor_window。")
async def unity_verify_window(
    windowTitle: str = "UnityPilot",
    includeScreenshot: bool = True,
    screenshotDegrade: str = "auto",
) -> str:
    r = await _get_facade().verify_window(
        window_title=windowTitle,
        include_screenshot=includeScreenshot,
        screenshot_degrade=screenshotDegrade,
    )
    return _payload(r)


# ── M24 Build Pipeline ─────────────────────────────────────────────────────


@mcp.tool(description="启动 Unity Player 构建（支持指定平台、输出路径和场景列表）。")
async def unity_build_start(
    buildTarget: str = "StandaloneWindows64",
    outputPath: str = "Builds/",
    scenes: list[str] | None = None,
) -> str:
    r = await _get_facade().build_start(build_target=buildTarget, output_path=outputPath, scenes=scenes)
    return _payload(r)


@mcp.tool(description="获取当前 Unity 构建任务的状态和进度。")
async def unity_build_status() -> str:
    r = await _get_facade().build_status()
    return _payload(r)


@mcp.tool(description="取消正在进行的 Unity 构建任务。")
async def unity_build_cancel() -> str:
    r = await _get_facade().build_cancel()
    return _payload(r)


@mcp.tool(description="获取当前 Unity 安装中支持的构建目标平台列表。")
async def unity_build_targets() -> str:
    r = await _get_facade().build_targets()
    return _payload(r)


# ── M25 Editor Commands ─────────────────────────────────────────────────────


@mcp.tool(description="执行 Unity 撤销操作（Undo）。")
async def unity_editor_undo(steps: int = 1) -> str:
    r = await _get_facade().editor_undo(steps=steps)
    return _payload(r)


@mcp.tool(description="执行 Unity 重做操作（Redo）。")
async def unity_editor_redo(steps: int = 1) -> str:
    r = await _get_facade().editor_redo(steps=steps)
    return _payload(r)


@mcp.tool(description="执行 Unity 编辑器命令（通过菜单路径，如 'Edit/Play'）。")
async def unity_editor_execute_command(commandName: str) -> str:
    r = await _get_facade().editor_execute_command(command_name=commandName)
    return _payload(r)


@mcp.tool(description="导航 Unity SceneView 视图（聚焦对象、设置视角、正交/透视切换等）。")
async def unity_sceneview_navigate(
    lookAtInstanceId: int = 0,
    pivot: dict | None = None,
    size: float = -1,
    rotation: dict | None = None,
    orthographic: bool | None = None,
    in2DMode: bool | None = None,
) -> str:
    r = await _get_facade().sceneview_navigate(
        look_at_instance_id=lookAtInstanceId, pivot=pivot, size=size,
        rotation=rotation, orthographic=orthographic, in_2d_mode=in2DMode,
    )
    return _payload(r)


# ── Entry point ───────────────────────────────────────────────────────────────


async def main() -> None:
    await mcp.run_stdio_async()


if __name__ == "__main__":
    asyncio.run(main())

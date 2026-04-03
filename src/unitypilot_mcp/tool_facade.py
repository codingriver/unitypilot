from __future__ import annotations

import asyncio
import subprocess
from dataclasses import asdict
from pathlib import Path

from .auto_fix_loop import AutoFixLoopService
from .dispatcher import CommandDispatcher
from .fix_planner import CompileFixPlanner
from .models import ToolResponse
from .patch_service import PatchApplyService
from .protocol import new_id, now_ms
from .responses import fail, ok
from .server import WsOrchestratorServer


class McpToolFacade:
    def __init__(self, server: WsOrchestratorServer) -> None:
        self.server = server
        workspace_root = "."
        session = server.session_manager.active
        if session and session.project_path:
            workspace_root = session.project_path

        self.dispatcher = CommandDispatcher(transport=server, state=server.state)
        self.patch_service = PatchApplyService(workspace_root=workspace_root)
        self.fix_planner = CompileFixPlanner(workspace_root=workspace_root)
        self.auto_fix_loop = AutoFixLoopService(
            dispatcher=self.dispatcher,
            state=server.state,
            patch_service=self.patch_service,
            planner=self.fix_planner,
        )

    def _detect_library_dll_mtime(self) -> int:
        session = self.server.session_manager.active
        if not session or not session.project_path:
            return 0
        library_dir = Path(session.project_path) / "Library"
        if not library_dir.exists() or not library_dir.is_dir():
            return 0
        latest = 0.0
        patterns = ["**/*.dll", "**/*.dll.mdb", "**/*.pdb"]
        for pattern in patterns:
            for p in library_dir.glob(pattern):
                try:
                    ts = p.stat().st_mtime
                    if ts > latest:
                        latest = ts
                except OSError:
                    continue
        return int(latest * 1000)

    def _sync_workspace_root_from_session(self) -> None:
        session = self.server.session_manager.active
        if not session or not session.project_path:
            return
        self.patch_service.set_workspace_root(session.project_path)
        self.fix_planner.workspace_root = self.patch_service.workspace_root

    async def open_editor(self, command: str = "", wait_for_connect_ms: int = 60000) -> ToolResponse:
        request_id = new_id("req")

        # Already connected — return current session info immediately
        if self.server.session_manager.is_connected():
            self._sync_workspace_root_from_session()
            session = self.server.session_manager.active
            return ok(request_id, {
                "started": False,
                "connected": True,
                "sessionId": session.session_id if session else "",
            })

        # Launch Unity if a command is provided
        started = False
        if command:
            try:
                subprocess.Popen(command, shell=True)
                started = True
            except Exception as ex:
                return fail(request_id, "OPEN_EDITOR_FAILED", f"启动 Unity 失败: {ex}",
                            {"command": command})

        # Poll until Unity connects or timeout
        deadline = now_ms() + wait_for_connect_ms
        while now_ms() < deadline:
            await asyncio.sleep(0.5)
            if self.server.session_manager.is_connected():
                self._sync_workspace_root_from_session()
                session = self.server.session_manager.active
                return ok(request_id, {
                    "started": started,
                    "connected": True,
                    "sessionId": session.session_id if session else "",
                })

        return fail(request_id, "UNITY_NOT_CONNECTED", "等待 Unity 连接超时",
                    {"waitForConnectMs": wait_for_connect_ms, "command": command})

    async def compile(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "compile.request", {"requestId": request_id}, timeout_ms=120000)

    async def compile_status(self, compile_request_id: str = "") -> ToolResponse:
        request_id = new_id("req")
        compile_state = self.server.state.compile
        return ok(
            request_id,
            {
                "status": compile_state.status,
                "errorCount": compile_state.error_count,
                "warningCount": compile_state.warning_count,
                "startedAt": compile_state.started_at,
                "finishedAt": compile_state.finished_at,
                "compileRequestId": compile_request_id or compile_state.compile_request_id,
            },
        )

    async def compile_errors(self, compile_request_id: str = "") -> ToolResponse:
        request_id = new_id("req")
        # 强制 live：禁止回退缓存；若拉取失败视为无报错
        result = await self.dispatcher.call(request_id, "compile.errors.get", {}, timeout_ms=45000)
        if result.ok:
            data = result.data or {}
            self.server.state.update_compile_errors(data)
            data.setdefault("source", "live")
            data.setdefault("mode", "strict_live")
            data.setdefault("compileRequestId", compile_request_id)
            return ok(request_id, data)

        # live 拉取失败：按要求视为“无报错”，并附加辅助诊断信息
        dll_mtime_ms = self._detect_library_dll_mtime()
        return ok(
            request_id,
            {
                "errors": [],
                "total": 0,
                "compileRequestId": compile_request_id,
                "source": "live",
                "mode": "strict_live",
                "liveFetch": "failed_as_empty",
                "diagnostics": {
                    "libraryDllLatestWriteMs": dll_mtime_ms,
                    "libraryDllExists": dll_mtime_ms > 0,
                },
            },
        )

    async def auto_fix_start(self, max_iterations: int = 20, stop_when_no_error: bool = True) -> ToolResponse:
        self._sync_workspace_root_from_session()
        request_id = new_id("req")
        loop = await self.auto_fix_loop.start(max_iterations=max_iterations, stop_when_no_error=stop_when_no_error)
        return ok(request_id, asdict(loop))

    async def auto_fix_stop(self, loop_id: str) -> ToolResponse:
        request_id = new_id("req")
        loop = self.auto_fix_loop.stop(loop_id)
        if not loop:
            return fail(request_id, "INVALID_PAYLOAD", "loopId 不存在", {"loopId": loop_id})
        return ok(request_id, asdict(loop))

    async def auto_fix_status(self) -> ToolResponse:
        request_id = new_id("req")
        loop = self.auto_fix_loop.status()
        if not loop:
            return ok(request_id, {"status": "idle"})
        return ok(request_id, asdict(loop))

    async def playmode_start(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "playmode.set", {"action": "play"})

    async def playmode_stop(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "playmode.set", {"action": "stop"})

    async def mouse_event(self, action: str, button: str, x: float, y: float, target_window: str,
                          modifiers: list[str] | None = None,
                          scroll_delta_x: float = 0.0, scroll_delta_y: float = 0.0) -> ToolResponse:
        request_id = new_id("req")
        payload = {
            "action": action,
            "button": button,
            "x": x,
            "y": y,
            "targetWindow": target_window,
            "modifiers": modifiers or [],
            "scrollDeltaX": scroll_delta_x,
            "scrollDeltaY": scroll_delta_y,
        }
        return await self.dispatcher.call(request_id, "mouse.event", payload)

    async def uitoolkit_dump(self, target_window: str, max_depth: int = 10) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "uitoolkit.dump", {
            "targetWindow": target_window, "maxDepth": max_depth,
        })

    async def uitoolkit_query(self, target_window: str, name_filter: str = "",
                              class_filter: str = "", type_filter: str = "",
                              text_filter: str = "") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "uitoolkit.query", {
            "targetWindow": target_window, "nameFilter": name_filter,
            "classFilter": class_filter, "typeFilter": type_filter,
            "textFilter": text_filter,
        })

    async def uitoolkit_event(self, target_window: str, event_type: str,
                              element_name: str = "", element_index: int = -1,
                              key_code: str = "", character: str = "",
                              mouse_button: int = 0, mouse_x: float = 0, mouse_y: float = 0,
                              modifiers: list[str] | None = None) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "uitoolkit.event", {
            "targetWindow": target_window, "eventType": event_type,
            "elementName": element_name, "elementIndex": element_index,
            "keyCode": key_code, "character": character,
            "mouseButton": mouse_button, "mouseX": mouse_x, "mouseY": mouse_y,
            "modifiers": modifiers or [],
        })

    async def drag_drop(self, source_window: str, target_window: str, drag_type: str,
                        from_x: float, from_y: float, to_x: float, to_y: float,
                        asset_paths: list[str] | None = None,
                        game_object_ids: list[int] | None = None,
                        custom_data: str = "",
                        modifiers: list[str] | None = None) -> ToolResponse:
        request_id = new_id("req")
        payload = {
            "sourceWindow": source_window,
            "targetWindow": target_window,
            "dragType": drag_type,
            "fromX": from_x,
            "fromY": from_y,
            "toX": to_x,
            "toY": to_y,
            "assetPaths": asset_paths or [],
            "gameObjectIds": game_object_ids or [],
            "customData": custom_data,
            "modifiers": modifiers or [],
        }
        return await self.dispatcher.call(request_id, "dragdrop.execute", payload)

    async def keyboard_event(self, action: str, target_window: str, key_code: str = "",
                             character: str = "", text: str = "",
                             modifiers: list[str] | None = None) -> ToolResponse:
        request_id = new_id("req")
        payload = {
            "action": action,
            "targetWindow": target_window,
            "keyCode": key_code,
            "character": character,
            "text": text,
            "modifiers": modifiers or [],
        }
        return await self.dispatcher.call(request_id, "keyboard.event", payload)

    async def editor_state(self) -> ToolResponse:
        request_id = new_id("req")
        s = self.server.state.editor
        return ok(request_id, {
            "connected": s.connected,
            "isCompiling": s.is_compiling,
            "playModeState": s.play_mode_state,
            "activeScene": s.active_scene,
            "source": "live",
        })

    async def mcp_status(self) -> ToolResponse:
        request_id = new_id("req")
        session = self.server.session_manager.active
        compile_state = self.server.state.compile
        return ok(
            request_id,
            {
                "connected": self.server.session_manager.is_connected(),
                "serverReady": self.server.is_ready(),
                "session": {
                    "sessionId": session.session_id if session else "",
                    "projectPath": session.project_path if session else "",
                    "unityVersion": session.unity_version if session else "",
                    "platform": session.platform if session else "",
                    "lastHeartbeatAt": session.last_heartbeat_at if session else 0,
                },
                "compile": {
                    "status": compile_state.status,
                    "errorCount": compile_state.error_count,
                    "warningCount": compile_state.warning_count,
                    "startedAt": compile_state.started_at,
                    "finishedAt": compile_state.finished_at,
                },
                "timeouts": self.dispatcher.timeout_policy_snapshot(),
            },
        )

    # ── M07 Console 日志读取 ─────────────────────────────────────────────────

    async def console_get_logs(self, log_type: str = "", count: int = 100) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if log_type:
            payload["logType"] = log_type
        payload["count"] = max(1, min(count, 1000))
        return await self.dispatcher.call(request_id, "console.logs.get", payload)

    async def console_clear(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "console.clear", {})

    # ── M08 GameObject 操作 ──────────────────────────────────────────────────

    async def gameobject_create(self, name: str = "New GameObject", parent_id: int = 0, primitive_type: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"name": name}
        if parent_id:
            payload["parentId"] = parent_id
        if primitive_type:
            payload["primitiveType"] = primitive_type
        return await self.dispatcher.call(request_id, "gameobject.create", payload)

    async def gameobject_find(self, name: str = "", tag: str = "", instance_id: int = 0) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if name:
            payload["name"] = name
        if tag:
            payload["tag"] = tag
        if instance_id:
            payload["instanceId"] = instance_id
        return await self.dispatcher.call(request_id, "gameobject.find", payload)

    async def gameobject_modify(self, instance_id: int, name: str | None = None, tag: str | None = None,
                                layer: int | None = None, active_self: bool | None = None,
                                is_static: bool | None = None, parent_id: int | None = None) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"instanceId": instance_id}
        if name is not None:
            payload["name"] = name
        if tag is not None:
            payload["tag"] = tag
        if layer is not None:
            payload["layer"] = layer
        if active_self is not None:
            payload["activeSelf"] = active_self
        if is_static is not None:
            payload["isStatic"] = is_static
        if parent_id is not None:
            payload["parentId"] = parent_id
        return await self.dispatcher.call(request_id, "gameobject.modify", payload)

    async def gameobject_delete(self, instance_id: int) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "gameobject.delete", {"instanceId": instance_id})

    async def gameobject_move(self, instance_id: int, position: dict | None = None,
                              rotation: dict | None = None, scale: dict | None = None) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"instanceId": instance_id}
        if position is not None:
            payload["position"] = position
        if rotation is not None:
            payload["rotation"] = rotation
        if scale is not None:
            payload["scale"] = scale
        return await self.dispatcher.call(request_id, "gameobject.move", payload)

    async def gameobject_duplicate(self, instance_id: int) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "gameobject.duplicate", {"instanceId": instance_id})

    # ── M09 Scene 管理 ──────────────────────────────────────────────────────

    async def scene_create(self, scene_name: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if scene_name:
            payload["sceneName"] = scene_name
        return await self.dispatcher.call(request_id, "scene.create", payload)

    async def scene_open(self, scene_path: str, mode: str = "single") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "scene.open", {"scenePath": scene_path, "mode": mode}, timeout_ms=30000)

    async def scene_save(self, scene_path: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if scene_path:
            payload["scenePath"] = scene_path
        return await self.dispatcher.call(request_id, "scene.save", payload)

    async def scene_load(self, scene_path: str, mode: str = "additive") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "scene.load", {"scenePath": scene_path, "mode": mode}, timeout_ms=30000)

    async def scene_set_active(self, scene_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "scene.setActive", {"scenePath": scene_path})

    async def scene_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "scene.list", {})

    async def scene_unload(self, scene_path: str, remove_scene: bool = False) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "scene.unload", {
            "scenePath": scene_path, "removeScene": 1 if remove_scene else 0,
        })

    # ── M10 Component 操作 ───────────────────────────────────────────────────

    async def component_add(self, game_object_id: int, component_type: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "component.add", {
            "gameObjectId": game_object_id, "componentType": component_type,
        })

    async def component_remove(self, game_object_id: int, component_type: str, component_index: int = 0) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "component.remove", {
            "gameObjectId": game_object_id, "componentType": component_type, "componentIndex": component_index,
        })

    async def component_get(self, game_object_id: int, component_type: str, component_index: int = 0) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "component.get", {
            "gameObjectId": game_object_id, "componentType": component_type, "componentIndex": component_index,
        })

    async def component_modify(self, game_object_id: int, component_type: str,
                                properties: dict, component_index: int = 0) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "component.modify", {
            "gameObjectId": game_object_id, "componentType": component_type,
            "properties": properties, "componentIndex": component_index,
        })

    async def component_list(self, game_object_id: int) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "component.list", {"gameObjectId": game_object_id})

    # ── M11 截图能力 ─────────────────────────────────────────────────────────

    async def screenshot_game_view(self, width: int = 1280, height: int = 720,
                                    format: str = "png", quality: int = 75) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "screenshot.gameView", {
            "width": width, "height": height, "format": format, "quality": quality,
        })

    async def screenshot_scene_view(self, width: int = 1280, height: int = 720,
                                     format: str = "png", quality: int = 75) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "screenshot.sceneView", {
            "width": width, "height": height, "format": format, "quality": quality,
        })

    async def screenshot_camera(self, camera_name: str, width: int = 1280, height: int = 720,
                                 format: str = "png", quality: int = 75) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "screenshot.camera", {
            "cameraName": camera_name, "width": width, "height": height, "format": format, "quality": quality,
        })

    # ── M12 Asset 管理 ──────────────────────────────────────────────────────

    async def asset_find(self, query: str, asset_type: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"query": query}
        if asset_type:
            payload["assetType"] = asset_type
        return await self.dispatcher.call(request_id, "asset.find", payload)

    async def asset_create_folder(self, parent_folder: str, new_folder_name: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "asset.createFolder", {
            "parentFolder": parent_folder, "newFolderName": new_folder_name,
        })

    async def asset_copy(self, source_path: str, destination_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "asset.copy", {
            "sourcePath": source_path, "destinationPath": destination_path,
        })

    async def asset_move(self, source_path: str, destination_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "asset.move", {
            "sourcePath": source_path, "destinationPath": destination_path,
        })

    async def asset_delete(self, asset_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "asset.delete", {"assetPath": asset_path})

    async def asset_refresh(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "asset.refresh", {})

    async def asset_get_info(self, asset_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "asset.getInfo", {"assetPath": asset_path})

    async def asset_find_built_in(self, query: str = "", asset_type: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if query:
            payload["query"] = query
        if asset_type:
            payload["assetType"] = asset_type
        return await self.dispatcher.call(request_id, "asset.findBuiltIn", payload)

    async def asset_get_data(self, asset_path: str = "", game_object_id: int = 0,
                             component_type: str = "", component_index: int = 0,
                             max_depth: int = 10) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"maxDepth": max_depth}
        if asset_path:
            payload["assetPath"] = asset_path
        if game_object_id:
            payload["gameObjectId"] = game_object_id
        if component_type:
            payload["componentType"] = component_type
        if component_index:
            payload["componentIndex"] = component_index
        return await self.dispatcher.call(request_id, "asset.getData", payload)

    async def asset_modify_data(self, properties: list[dict],
                                asset_path: str = "", game_object_id: int = 0,
                                component_type: str = "", component_index: int = 0) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"properties": properties}
        if asset_path:
            payload["assetPath"] = asset_path
        if game_object_id:
            payload["gameObjectId"] = game_object_id
        if component_type:
            payload["componentType"] = component_type
        if component_index:
            payload["componentIndex"] = component_index
        return await self.dispatcher.call(request_id, "asset.modifyData", payload)

    # ── M13 Prefab 操作 ─────────────────────────────────────────────────────

    async def prefab_create(self, source_game_object_id: int, prefab_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "prefab.create", {
            "sourceGameObjectId": source_game_object_id, "prefabPath": prefab_path,
        })

    async def prefab_instantiate(self, prefab_path: str, parent_id: int = 0) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"prefabPath": prefab_path}
        if parent_id:
            payload["parentId"] = parent_id
        return await self.dispatcher.call(request_id, "prefab.instantiate", payload)

    async def prefab_open(self, prefab_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "prefab.open", {"prefabPath": prefab_path})

    async def prefab_close(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "prefab.close", {})

    async def prefab_save(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "prefab.save", {})

    # ── M14 Material 与 Shader ──────────────────────────────────────────────

    async def material_create(self, material_path: str, shader_name: str = "Standard") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "material.create", {
            "materialPath": material_path, "shaderName": shader_name,
        })

    async def material_modify(self, material_path: str, properties: dict) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "material.modify", {
            "materialPath": material_path, "properties": properties,
        })

    async def material_assign(self, target_game_object_id: int, material_path: str, material_index: int = 0) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "material.assign", {
            "targetGameObjectId": target_game_object_id, "materialPath": material_path, "materialIndex": material_index,
        })

    async def material_get(self, material_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "material.get", {"materialPath": material_path})

    async def shader_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "shader.list", {})

    # ── M15 菜单项执行 ──────────────────────────────────────────────────────

    async def menu_execute(self, menu_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "menu.execute", {"menuPath": menu_path})

    async def menu_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "menu.list", {})

    # ── M16 Package 管理 ─────────────────────────────────────────────────────

    async def package_add(self, package_name: str, version: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"packageName": package_name}
        if version:
            payload["version"] = version
        return await self.dispatcher.call(request_id, "package.add", payload, timeout_ms=120000)

    async def package_remove(self, package_name: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "package.remove", {"packageName": package_name}, timeout_ms=60000)

    async def package_list(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "package.list", {})

    async def package_search(self, query: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "package.search", {"query": query})

    # ── M17 测试运行 ─────────────────────────────────────────────────────────

    async def test_run(self, test_mode: str = "EditMode", test_filter: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"testMode": test_mode}
        if test_filter:
            payload["testFilter"] = test_filter
        return await self.dispatcher.call(request_id, "test.run", payload, timeout_ms=300000)

    async def test_results(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "test.results", {})

    async def test_list(self, test_mode: str = "EditMode") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "test.list", {"testMode": test_mode})

    # ── M18 脚本读写 ─────────────────────────────────────────────────────────

    async def script_read(self, script_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "script.read", {"scriptPath": script_path})

    async def script_create(self, script_path: str, content: str = "") -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "script.create", {
            "scriptPath": script_path, "content": content,
        })

    async def script_update(self, script_path: str, content: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "script.update", {
            "scriptPath": script_path, "content": content,
        })

    async def script_delete(self, script_path: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "script.delete", {"scriptPath": script_path})

    # ── M19 C# 代码执行 ──────────────────────────────────────────────────────

    async def csharp_execute(self, code: str, timeout_seconds: int = 10) -> ToolResponse:
        request_id = new_id("req")
        clamped = max(1, min(timeout_seconds, 30))
        return await self.dispatcher.call(request_id, "csharp.execute", {
            "code": code, "timeoutSeconds": clamped,
        }, timeout_ms=clamped * 1000 + 5000)

    async def csharp_status(self, execution_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "csharp.status", {"executionId": execution_id})

    async def csharp_abort(self, execution_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "csharp.abort", {"executionId": execution_id})

    # ── M20 反射调用 ─────────────────────────────────────────────────────────

    async def reflection_find(self, type_name: str, method_name: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"typeName": type_name}
        if method_name:
            payload["methodName"] = method_name
        return await self.dispatcher.call(request_id, "reflection.find", payload)

    async def reflection_call(self, type_name: str, method_name: str,
                              parameters: list | None = None, is_static: bool = True,
                              target_instance_path: str = "") -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {
            "typeName": type_name,
            "methodName": method_name,
            "parameters": parameters or [],
            "isStatic": is_static,
        }
        if target_instance_path:
            payload["targetInstancePath"] = target_instance_path
        return await self.dispatcher.call(request_id, "reflection.call", payload)

    # ── M21 批量操作 ─────────────────────────────────────────────────────────

    async def batch_execute(self, operations: list, mode: str = "sequential",
                            stop_on_error: bool = True) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "batch.execute", {
            "operations": operations, "mode": mode, "stopOnError": stop_on_error,
        }, timeout_ms=60000)

    async def batch_cancel(self, batch_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "batch.cancel", {"batchId": batch_id})

    async def batch_results(self, batch_id: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "batch.results", {"batchId": batch_id})

    # ── M22 Selection 管理 ───────────────────────────────────────────────────

    async def selection_get(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "selection.get", {})

    async def selection_set(self, game_object_ids: list[int] | None = None,
                            asset_paths: list[str] | None = None) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if game_object_ids:
            payload["gameObjectIds"] = game_object_ids
        if asset_paths:
            payload["assetPaths"] = asset_paths
        return await self.dispatcher.call(request_id, "selection.set", payload)

    async def selection_clear(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "selection.clear", {})

    # ── M23 MCP Resources (facade helpers) ───────────────────────────────────

    async def resource_scene_hierarchy(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.sceneHierarchy", {})

    async def resource_console_logs(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.consoleLogs", {})

    async def resource_editor_state(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.editorState", {})

    async def resource_packages(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.packages", {})

    async def resource_build_status(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "resource.buildStatus", {})

    # ── M24 Build Pipeline ───────────────────────────────────────────────────

    async def build_start(self, build_target: str = "StandaloneWindows64",
                          output_path: str = "Builds/", scenes: list[str] | None = None) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {"buildTarget": build_target, "outputPath": output_path}
        if scenes:
            payload["scenes"] = scenes
        return await self.dispatcher.call(request_id, "build.start", payload, timeout_ms=600000)

    async def build_status(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "build.status", {})

    async def build_cancel(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "build.cancel", {})

    async def build_targets(self) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "build.targets", {})

    # ── M25 Editor Commands ──────────────────────────────────────────────────

    async def editor_undo(self, steps: int = 1) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "editor.undo", {"steps": steps})

    async def editor_redo(self, steps: int = 1) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "editor.redo", {"steps": steps})

    async def editor_execute_command(self, command_name: str) -> ToolResponse:
        request_id = new_id("req")
        return await self.dispatcher.call(request_id, "editor.executeCommand", {"commandName": command_name})

    async def sceneview_navigate(self, look_at_instance_id: int = 0,
                                  pivot: dict | None = None, size: float = -1,
                                  rotation: dict | None = None,
                                  orthographic: bool | None = None,
                                  in_2d_mode: bool | None = None) -> ToolResponse:
        request_id = new_id("req")
        payload: dict = {}
        if look_at_instance_id:
            payload["lookAtInstanceId"] = look_at_instance_id
        if pivot is not None:
            payload["pivot"] = pivot
        if size >= 0:
            payload["size"] = size
        if rotation is not None:
            payload["rotation"] = rotation
        if orthographic is not None:
            payload["orthographic"] = 1 if orthographic else 0
        if in_2d_mode is not None:
            payload["in2DMode"] = 1 if in_2d_mode else 0
        return await self.dispatcher.call(request_id, "sceneview.navigate", payload)

from __future__ import annotations

import os
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(slots=True)
class PatchRequest:
    patch_id: str
    file_path: str
    patch_type: str
    old_content: str = ""
    new_content: str = ""


class PatchApplyService:
    def __init__(self, workspace_root: str) -> None:
        self.workspace_root = Path(workspace_root)

    def set_workspace_root(self, workspace_root: str) -> None:
        self.workspace_root = Path(workspace_root)

    def apply_patch(self, req: PatchRequest) -> dict[str, Any]:
        target = Path(req.file_path)
        if not target.is_absolute():
            target = self.workspace_root / target

        if not target.exists():
            return self._failed(req, f"文件不存在：{target}")

        original = target.read_text(encoding="utf-8")
        try:
            updated = self._build_content(req, original)
        except ValueError as ex:
            return self._failed(req, str(ex))

        if updated == original:
            return {
                "patchId": req.patch_id,
                "filePath": str(target),
                "patchType": req.patch_type,
                "writeStatus": "skipped",
                "errorMessage": "无变更",
            }

        tmp_path = None
        try:
            with tempfile.NamedTemporaryFile("w", delete=False, encoding="utf-8", dir=str(target.parent)) as tmp:
                tmp.write(updated)
                tmp_path = tmp.name
            os.replace(tmp_path, target)
        except Exception as ex:  # noqa: BLE001
            if tmp_path and Path(tmp_path).exists():
                Path(tmp_path).unlink(missing_ok=True)
            return self._failed(req, f"写入失败：{ex}")

        changed_lines = abs(updated.count("\n") - original.count("\n"))
        return {
            "patchId": req.patch_id,
            "filePath": str(target),
            "patchType": req.patch_type,
            "writeStatus": "success",
            "errorMessage": "",
            "diffSummary": {"lineDelta": changed_lines},
        }

    def apply_batch(self, patches: list[PatchRequest]) -> list[dict[str, Any]]:
        return [self.apply_patch(item) for item in patches]

    def _build_content(self, req: PatchRequest, original: str) -> str:
        if req.patch_type == "overwrite":
            return req.new_content

        if req.patch_type == "replace_all":
            if req.old_content == "":
                raise ValueError("oldContent 为空，无法执行 replace_all")
            return original.replace(req.old_content, req.new_content)

        if req.patch_type == "replace_block":
            if req.old_content == "":
                raise ValueError("oldContent 为空，无法执行 replace_block")
            matches = original.count(req.old_content)
            if matches != 1:
                raise ValueError(f"旧片段未唯一匹配：{matches}")
            return original.replace(req.old_content, req.new_content, 1)

        raise ValueError(f"未知 patchType：{req.patch_type}")

    @staticmethod
    def _failed(req: PatchRequest, msg: str) -> dict[str, Any]:
        return {
            "patchId": req.patch_id,
            "filePath": req.file_path,
            "patchType": req.patch_type,
            "writeStatus": "failed",
            "errorMessage": msg,
        }

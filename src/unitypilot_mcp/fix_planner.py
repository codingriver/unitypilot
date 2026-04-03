from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .patch_service import PatchRequest
from .protocol import new_id


@dataclass(slots=True)
class PlannedPatch:
    request: PatchRequest
    reason: str
    rule: str
    file_path: str
    line: int
    message: str

    def to_report(self) -> dict[str, Any]:
        return {
            "patchId": self.request.patch_id,
            "rule": self.rule,
            "reason": self.reason,
            "filePath": self.file_path,
            "line": self.line,
            "message": self.message,
            "patchType": self.request.patch_type,
        }


class CompileFixPlanner:
    """M04/M06 最小规划器：按编译错误生成补丁请求。"""

    def __init__(self, workspace_root: str) -> None:
        self.workspace_root = Path(workspace_root)

    def plan(self, compile_errors: list[dict[str, Any]], max_files: int = 20) -> list[PlannedPatch]:
        planned: list[PlannedPatch] = []
        touched: set[tuple[str, int]] = set()   # (file, line) already patched
        overwrite_files: set[str] = set()        # files already getting a full overwrite patch

        for err in compile_errors:
            if len(planned) >= max_files:
                break

            file_path = str(err.get("file", "")).strip()
            line = int(err.get("line", 0) or 0)
            message = str(err.get("message", "")).strip()

            if not file_path or line <= 0:
                continue

            target = Path(file_path)
            if not target.is_absolute():
                target = self.workspace_root / target
            if not target.exists():
                continue

            source = target.read_text(encoding="utf-8")
            lines = source.splitlines()
            idx = line - 1
            if idx < 0 or idx >= len(lines):
                continue

            key = (str(target), line)
            if key in touched:
                continue

            semicolon_patch = self._plan_missing_semicolon(target, lines, idx, message, line)
            if semicolon_patch:
                # replace_block patches are safe alongside overwrites (they match by text, not offset)
                planned.append(semicolon_patch)
                touched.add(key)
                continue

            # overwrite patches (e.g. adding a using directive) must not stack on the same file
            if str(target) not in overwrite_files:
                using_patch = self._plan_missing_using(target, source, lines[idx], message, line)
                if using_patch:
                    planned.append(using_patch)
                    touched.add(key)
                    overwrite_files.add(str(target))
                    continue

            fallback = self._plan_fallback_comment(target, source, lines[idx], message, line)
            if fallback:
                planned.append(fallback)
                touched.add(key)

        return planned

    def _plan_missing_semicolon(self, target: Path, lines: list[str], idx: int, message: str, line: int) -> PlannedPatch | None:
        if "; expected" not in message and "expected ;" not in message:
            return None

        original_line = lines[idx]
        stripped = original_line.rstrip()
        bare = stripped.strip()
        if bare == "" or bare.startswith("//"):
            return None
        if bare.endswith(";") or bare.endswith("{") or bare.endswith("}"):
            return None

        control_prefixes = ("if ", "if(", "for ", "for(", "while ", "while(", "switch ", "switch(")
        if bare.startswith(control_prefixes):
            return None

        replacement = stripped + ";" + original_line[len(stripped):]
        req = PatchRequest(
            patch_id=new_id("patch"),
            file_path=str(target),
            patch_type="replace_block",
            old_content=original_line,
            new_content=replacement,
        )
        return PlannedPatch(
            request=req,
            reason="补全缺失分号",
            rule="semicolon",
            file_path=str(target),
            line=line,
            message=message,
        )

    def _plan_missing_using(self, target: Path, source: str, error_line: str, message: str, line: int) -> PlannedPatch | None:
        if "using directive" not in message and "namespace name" not in message:
            return None

        missing = self._guess_using_namespace(error_line, message)
        if not missing:
            return None

        if f"using {missing};" in source:
            return None

        updated = self._insert_using(source, missing)
        if updated == source:
            return None

        req = PatchRequest(
            patch_id=new_id("patch"),
            file_path=str(target),
            patch_type="overwrite",
            new_content=updated,
        )
        return PlannedPatch(
            request=req,
            reason=f"补全 using {missing}",
            rule="using",
            file_path=str(target),
            line=line,
            message=message,
        )

    @staticmethod
    def _guess_using_namespace(error_line: str, message: str) -> str | None:
        if "List<" in error_line or "Dictionary<" in error_line or "HashSet<" in error_line:
            return "System.Collections.Generic"
        if "Task" in error_line:
            return "System.Threading.Tasks"
        if "Regex" in error_line:
            return "System.Text.RegularExpressions"
        if "Enumerable" in error_line or ".Select(" in error_line or ".Where(" in error_line:
            return "System.Linq"

        m = re.search(r"'([A-Za-z0-9_\.]+)'", message)
        if m and "." in m.group(1):
            candidate = m.group(1).rsplit(".", 1)[0]
            if candidate and candidate[0].isupper():
                return candidate
        return None

    @staticmethod
    def _insert_using(source: str, namespace: str) -> str:
        lines = source.splitlines()
        insert_idx = 0
        for i, line in enumerate(lines):
            if line.strip().startswith("using "):
                insert_idx = i + 1
            elif insert_idx > 0:
                break

        using_line = f"using {namespace};"
        if insert_idx == 0:
            lines.insert(0, using_line)
        else:
            lines.insert(insert_idx, using_line)
        return "\n".join(lines) + ("\n" if source.endswith("\n") else "")

    def _plan_fallback_comment(self, target: Path, source: str, original_line: str, message: str, line: int) -> PlannedPatch | None:
        if original_line.strip().startswith("// AUTO_FIX"):
            return None

        # Skip if the line is not uniquely present — replace_block requires exactly 1 match
        if source.count(original_line) != 1:
            return None

        replacement = f"// AUTO_FIX: {message}\n// {original_line}"
        req = PatchRequest(
            patch_id=new_id("patch"),
            file_path=str(target),
            patch_type="replace_block",
            old_content=original_line,
            new_content=replacement,
        )
        return PlannedPatch(
            request=req,
            reason="兜底注释问题行",
            rule="fallback-comment",
            file_path=str(target),
            line=line,
            message=message,
        )

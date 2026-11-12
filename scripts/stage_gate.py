#!/usr/bin/env python3
"""阶段状态：把某一格 Sxx 的测试结果翻译成一行无歧义状态。

判据全部来自 dotnet test；本脚本不产生判据、不读文档、不手写状态。
用法：python scripts/stage_gate.py S01
退出码 0 = PASS，1 = BLOCKED，2 = 参数或运行错误。
"""

from __future__ import annotations

import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# 判据住在哪个测试项目，由「这一格归哪一侧」决定，不按项目大小分。
# 内核格（Runtime.*）→ CoreTests；宿主格（Hub）→ ApplicationTests。
# 加新格时**必须**在这里登记，否则会落到默认项目、报 0 条 BLOCKED——那种「找不到」必须显形。
CORE = ROOT / "tests" / "AgentWorkbench.CoreTests" / "AgentWorkbench.CoreTests.csproj"
HOST = ROOT / "tests" / "AgentWorkbench.ApplicationTests" / "AgentWorkbench.ApplicationTests.csproj"
PROJECTS = {
    "S01": HOST,   # 入口契约：AgentHub
    "S02": HOST,   # 身份与会话：AgentHub
    "S03": HOST,   # 组上下文：Harness
    "S04": HOST,   # 模型决策：流程验收，与其余 Sxx 同处；模块级单测在 CoreTests
}
DEFAULT_PROJECT = HOST


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def read_trx(path: Path) -> tuple[dict[str, str], list[str], list[str]]:
    """返回 (计数, 失败用例, 未执行用例)。

    未执行必须单独收集：<c>Skip</c> 的用例在 trx 里 outcome 是 NotExecuted，
    它既不是绿也不是红。把它算进 total 却不算失败，正是「没有保障伪装成有保障」。
    """
    root = ET.parse(path).getroot()
    counters: dict[str, str] = {}
    failed: list[str] = []
    skipped: list[str] = []
    for elem in root.iter():
        name = local(elem.tag)
        if name == "Counters" and not counters:
            counters = {local(k): v for k, v in elem.attrib.items()}
        elif name == "UnitTestResult":
            outcome = elem.attrib.get("outcome", "")
            test_name = elem.attrib.get("testName", "?")
            if outcome == "Failed":
                failed.append(test_name)
            elif outcome in ("NotExecuted", "Inconclusive", "NotRunnable"):
                skipped.append(test_name)
    return counters, failed, skipped


def main() -> int:
    if len(sys.argv) != 2 or not re.fullmatch(r"S(0[1-9]|10)", sys.argv[1]):
        print("BLOCKED 用法：python scripts/stage_gate.py S01 … S10")
        return 2
    key = sys.argv[1]
    project = PROJECTS.get(key, DEFAULT_PROJECT)

    with tempfile.TemporaryDirectory(prefix="aw-stage-") as tmp:
        cmd = [
            "dotnet", "test", str(project), "--nologo",
            "--filter", f"FullyQualifiedName~{key}&FullyQualifiedName!~Sentinel",
            "--logger", "trx;LogFileName=stage.trx",
            "--results-directory", tmp,
        ]
        run = subprocess.run(
            cmd, cwd=ROOT, capture_output=True, text=True,
            encoding="utf-8", errors="replace",
        )
        trx = Path(tmp) / "stage.trx"
        if not trx.exists():
            print(f"{key} BLOCKED 测试未运行（dotnet test 退出码 {run.returncode}）")
            for line in (run.stdout + run.stderr).strip().splitlines()[-8:]:
                print("   " + line)
            return 1
        counters, failed, skipped = read_trx(trx)
        total = int(counters.get("total", "0"))
        failed_n = int(counters.get("failed", "0"))

    # 零条测试不能算通过：否则「这一格还没装置」会被误读成 PASS。
    if total == 0:
        print(f"{key} BLOCKED 这一格还没有测试组（0 条判据，不能算通过）")
        return 1
    if failed_n > 0:
        print(f"{key} BLOCKED {total - failed_n - len(skipped)}/{total} 条判据绿，不能进入下一格")
        for name in failed or ["（未取到用例名）"]:
            print("   - 红：" + name)
        for name in skipped:
            print("   - 未执行：" + name)
        return 1
    # 未执行 ≠ 通过：这一格有步骤没有装置守着，状态必须是 BLOCKED，并且点名是哪一步。
    if skipped:
        print(f"{key} BLOCKED {total - len(skipped)}/{total} 条判据绿，{len(skipped)} 条未执行（无装置），不能进入下一格")
        for name in skipped:
            print("   - 未执行：" + name)
        return 1

    print(f"{key} PASS {total}/{total} 条判据绿，可以进入下一格")
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())

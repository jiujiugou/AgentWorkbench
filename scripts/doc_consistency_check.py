#!/usr/bin/env python3
"""跨文档「同一事实口径」一致性扫描（报告模式，非零仅用于自检失败）。

和 check_doc_refs.py 的分工：
- check_doc_refs.py 管「文档点名的符号还在不在」——引用漂移。
- 本脚本管「同一事实在多份文档里说法是否一致」——口径漂移。
两者不是等价物，故不合并。

只读、零依赖、不评分、不修改任何文件。默认退出码恒为 0：
盘点的多数冲突属于「待人类裁决」，机器无权判红灯。脚本只把它们列成
WARN 供 doc-gardening 阅读，禁止拿 WARN 数量当文档质量分数。

自证：--self-test 用伪造行证明每条检测器都能认出违规——
否则把检测写成永远返回空，扫描全绿也是假绿。

刻意不检查：
- 修改日期判断过期（规范明令禁止；事实/决策/时间三类过期须人工区分）。
- 终态词表语义（failed vs iteration_limit 是否矛盾属语义判断，易误报）。
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# 规则：名称 -> (正则, 判定为冲突的提示)。命中即 WARN，不当场判对错。
RULES = {
    "正本旧路径": (
        re.compile(r"docs/需求/产品目标与范围\.md"),
        "正本已迁至 docs/产品目标与范围.md；若指历史态请写「删除态 / git show」",
    ),
    "六句旧章节号": (
        re.compile(r"§\s*4\.1"),
        "六句现位于正本 §3；§4.1 为旧章节号",
    ),
    "ADR 范围口径": (
        re.compile(r"ADR\s*0013\s*[-–—]\s*0018|ADR\s*0013\s*[-–—]\s*0019"),
        "ADR 范围在本仓 AGENTS.md 与正本之间不一致，属待人类裁决",
    ),
}

# 与 DocsHygieneTests.PathRefViolations 一致的正当用法白名单。
EXEMPT = ("git show", "不在工作区", "删除态", "已移除", "已删除")


def scan_line(line: str) -> list[str]:
    """返回该行命中的规则名；正当用法行跳过。"""
    if any(token in line for token in EXEMPT):
        return []
    return [name for name, (pattern, _) in RULES.items() if pattern.search(line)]


def collect() -> list[tuple[str, int, str, str]]:
    files = sorted((ROOT / "docs").rglob("*.md")) + [ROOT / "AGENTS.md"]
    found: list[tuple[str, int, str, str]] = []
    for path in files:
        if not path.exists():
            continue
        rel = path.relative_to(ROOT).as_posix()
        for no, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            for name in scan_line(line):
                found.append((rel, no, name, line.strip()[:120]))
    return found


def self_test() -> int:
    probes = [
        "正本在 docs/需求/产品目标与范围.md",
        "见正本 §4.1 第 3 句",
        "ADR 0013–0018 为准",
        "ADR 0013–0019 为准",
    ]
    ok = True
    for line in probes:
        hits = scan_line(line)
        print(f"  probe {line!r} -> {hits}")
        if not hits:
            ok = False
    # 白名单行必须不命中，否则会误报一片。
    exempt = "docs/需求/产品目标与范围.md 当前删除态，用 git show 取"
    if scan_line(exempt):
        print(f"  FAIL 白名单行被误报：{exempt!r}")
        ok = False
    print("自检通过：四条违规均能识别，白名单行不误报" if ok else "自检失败：检测器认不出违规")
    return 0 if ok else 1


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()

    found = collect()
    print(f"跨文档口径扫描：{len(found)} 处 WARN（仅提示，不判对错）\n")
    for rel, no, name, line in found:
        hint = RULES[name][1]
        print(f"WARN {rel}:{no} [{name}] {line}")
        print(f"     -> {hint}")
    if not found:
        print("无口径漂移提示。注意：这不等于文档与代码一致。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

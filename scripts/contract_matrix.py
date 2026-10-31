#!/usr/bin/env python3
"""契约验收对照表生成器。

它做四件事，机器做，不靠人记：

  1. 从接口契约里读出验收编号（A1…G2）—— 这是「承诺」
  2. 从 Controller 源码里读出 (HTTP 方法, 路由) → 类.方法:行号 —— 这是「实现」
  3. 跑测试，从 TRX 里读出 类.方法 + 结果 —— 这是「证明」
  4. 三边对照，输出一张 md 表；全绿退出 0，否则退出 1

用法：
    python scripts/contract_matrix.py              # 跑测试 + 重新生成
    python scripts/contract_matrix.py --no-test    # 复用 tests/TestResults 里已有的 TRX
    python scripts/contract_matrix.py --dry-run    # 只打印总览，不写文件

测试怎么和编号对上（二选一，Trait 优先）：
    [Trait("Contract", "A4")]                      ← 改方法名也不会断
    public async Task A4_扩展名不支持_返回业务码()   ← 简单，方法名前缀

退出码：0 = 全部有测试且全绿；1 = 有红 / 跳过 / 未写 / 编号异常
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONTRACT = ROOT / "docs/设计/接口/知识资料管理接口.md"
OUT = ROOT / "docs/验证/契约对照表.md"
RESULTS_DIR = ROOT / "tests/TestResults"

TEST_PROJECTS = [
    "tests/AgentWorkbench.ApiContractTests",
    "tests/AgentWorkbench.ApplicationTests",
    "tests/AgentWorkbench.DataIntegrationTests",
    "tests/AgentWorkbench.EndToEndTests",
    "tests/AgentWorkbench.ArchitectureTests",
]

# 契约章节 → 端点。改端点时必须同步这里，脚本会把它打印在输出里给自己看。
SECTION_ENDPOINTS = {
    1: [("POST", "/api/documents")],
    2: [
        ("GET", "/api/documents"),
        ("GET", "/api/documents/{id}"),
        ("GET", "/api/documents/{id}/pages"),
        ("GET", "/api/documents/{id}/chunks"),
        ("GET", "/api/documents/{id}/file"),
    ],
    3: [("POST", "/api/documents/{id}/reparse")],
    4: [("DELETE", "/api/documents/{id}")],
    8: [],  # 跨操作，不绑定单个端点
}

# 同一章有多个端点时，用 Given/When 里的关键词把编号精确到某个端点。
# 命中不了就退回「该章全部端点」——宁可显示多个，也不猜。
ENDPOINT_HINTS = [
    ("调列表", ("GET", "/api/documents")),
    ("调详情", ("GET", "/api/documents/{id}")),
    ("pages", ("GET", "/api/documents/{id}/pages")),
    ("chunks", ("GET", "/api/documents/{id}/chunks")),
    ("file", ("GET", "/api/documents/{id}/file")),
]

ID_RE = re.compile(r"^[A-G]\d+$")
CONTRACT_ROW_RE = re.compile(r"^\|\s*([A-G]\d+)\s*\|([^|]*)\|([^|]*)\|([^|]*)\|")
SECTION_RE = re.compile(r"^##\s+(\d+)\.\s+(.+)$")
ROUTE_RE = re.compile(r'\[Http(Get|Post|Put|Delete|Patch)(?:\("([^"]*)"\))?\]')
CLASS_ROUTE_RE = re.compile(r'\[Route\("([^"]*)"\)\]')
ACTION_RE = re.compile(
    r"public\s+(?:async\s+)?(?:Task<|ValueTask<)?[\w<>\[\]\.\?]+\s+(\w+)\s*\("
)


# ---------------------------------------------------------------- 契约


def parse_contract() -> tuple[list[dict], list[str]]:
    """返回 (验收条目, 问题列表)。问题 = 重号 / 格式坏行。"""
    text = CONTRACT.read_text(encoding="utf-8")
    items, problems = [], []
    seen: dict[str, int] = {}
    section, section_title = 0, ""

    for lineno, line in enumerate(text.splitlines(), 1):
        m = SECTION_RE.match(line)
        if m:
            section, section_title = int(m.group(1)), m.group(2).strip()
            continue
        m = CONTRACT_ROW_RE.match(line)
        if not m:
            continue
        cid = m.group(1)
        if cid in seen:
            problems.append(f"{cid} 重号：第 {seen[cid]} 行与第 {lineno} 行")
            continue
        seen[cid] = lineno
        items.append(
            {
                "id": cid,
                "given": m.group(2).strip(),
                "when": m.group(3).strip(),
                "then": m.group(4).strip(),
                "section": section,
                "section_title": section_title,
                "line": lineno,
            }
        )
    return items, problems


def check_id_sequence(items: list[dict]) -> list[str]:
    """按组检查缺号：A 组应连续、B 组应连续……"""
    problems = []
    groups: dict[str, list[int]] = {}
    for it in items:
        groups.setdefault(it["id"][0], []).append(int(it["id"][1:]))
    for g, nums in sorted(groups.items()):
        nums.sort()
        missing = [n for n in range(1, nums[-1] + 1) if n not in nums]
        if missing:
            problems.append(
                f"{g} 组缺号：" + "、".join(f"{g}{n}" for n in missing)
            )
    return problems


# ---------------------------------------------------------------- 实现


def normalize_route(path: str) -> str:
    p = "/" + path.strip().strip("/")
    p = re.sub(r"\{[^}]*\}", "{id}", p)
    return re.sub(r"/+", "/", p).lower()


def scan_controllers() -> dict[tuple[str, str], dict]:
    """(HTTP 方法, 路由) → {where: '类.方法:L行'}"""
    found: dict[tuple[str, str], dict] = {}
    for cs in sorted((ROOT / "src").rglob("Controllers/*.cs")):
        text = cs.read_text(encoding="utf-8")
        cls = re.search(r"class\s+(\w+)", text)
        if not cls:
            continue
        cls_route = CLASS_ROUTE_RE.search(text)
        prefix = cls_route.group(1) if cls_route else ""

        pending: list[tuple[str, str, int]] = []
        for lineno, line in enumerate(text.splitlines(), 1):
            rm = ROUTE_RE.search(line)
            if rm:
                pending.append((rm.group(1).upper(), rm.group(2) or "", lineno))
                continue
            am = ACTION_RE.search(line)
            if am and pending:
                verb, sub, decl_line = pending.pop(0)
                route = normalize_route(f"{prefix}/{sub}" if sub else prefix)
                key = (verb, route)
                if key not in found:
                    found[key] = {
                        "where": f"{cls.group(1)}.{am.group(1)}:{lineno}",
                        "decl": decl_line,
                        "file": cs.relative_to(ROOT).as_posix(),
                    }
                pending.clear()
    return found


# ---------------------------------------------------------------- 测试


def run_tests() -> list[Path]:
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    for old in RESULTS_DIR.glob("*.trx"):
        old.unlink()
    produced: list[Path] = []
    for proj in TEST_PROJECTS:
        name = Path(proj).name.replace("AgentWorkbench.", "") + ".trx"
        cmd = [
            "dotnet", "test", proj,
            "--nologo", "-v", "q",
            "--logger", f"trx;LogFileName={name}",
            "--results-directory", str(RESULTS_DIR),
        ]
        print(f"  → {Path(proj).name}", flush=True)
        proc = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True,
                              encoding="utf-8", errors="replace")
        trx = RESULTS_DIR / name
        if trx.exists():
            produced.append(trx)
        else:
            tail = (proc.stdout or "")[-400:]
            print(f"    没有产出 TRX（退出码 {proc.returncode}）{tail}")
    return produced


def parse_trx(paths: list[Path]) -> tuple[dict[tuple[str, str], dict], list[str]]:
    """(className, methodName) → {outcome, traits, project}"""
    tests: dict[tuple[str, str], dict] = {}
    errors: list[str] = []
    for path in paths:
        try:
            tree = ET.parse(path)
        except ET.ParseError as exc:
            errors.append(f"{path.name}: {exc}")
            continue
        root = tree.getroot()
        project = path.stem

        # testId → (className, methodName, traits)
        idmap: dict[str, tuple[str, str, list[str]]] = {}
        for ut in root.iter():
            if not ut.tag.endswith("UnitTest"):
                continue
            uid = ut.get("id")
            tm = next((c for c in ut if c.tag.endswith("TestMethod")), None)
            if not uid or tm is None:
                continue
            traits: list[str] = []
            for prop in ut.iter():
                if not prop.tag.endswith("Property"):
                    continue
                kv = {c.tag.split("}")[-1]: (c.text or "").strip() for c in prop}
                if kv.get("Key") == "TestCategory" and ID_RE.match(kv.get("Value", "")):
                    traits.append(kv["Value"])
            idmap[uid] = (tm.get("className", ""), tm.get("name", ""), traits)

        for r in root.iter():
            if not r.tag.endswith("UnitTestResult"):
                continue
            info = idmap.get(r.get("testId", ""))
            if not info:
                # 退路：testName 里带完整名
                tn = r.get("testName", "").split("(")[0]
                if "." not in tn:
                    continue
                cls, name = tn.rsplit(".", 1)
                info = (cls, name, [])
            cls, name, traits = info
            key = (cls, name)
            outcome = r.get("outcome", "")
            prev = tests.get(key)
            if prev is None:
                tests[key] = {"outcome": outcome, "traits": traits, "project": project}
            else:
                prev["outcome"] = worst(prev["outcome"], outcome)
                prev["traits"] = sorted(set(prev["traits"]) | set(traits))
    return tests, errors


RANK = {"Failed": 3, "Error": 3, "Timeout": 3, "Passed": 1, "NotExecuted": 0, "Skipped": 0, "": 0}


def worst(a: str, b: str) -> str:
    return a if RANK.get(a, 0) >= RANK.get(b, 0) else b


def id_of_test(traits: list[str], name: str) -> str | None:
    for t in traits:
        if ID_RE.match(t):
            return t
    m = re.match(r"^([A-G]\d+)(?:_|\b)", name)
    return m.group(1) if m else None


def resolve_impl(item: dict, controllers: dict[tuple[str, str], dict]) -> str:
    eps = SECTION_ENDPOINTS.get(item["section"], [])
    if not eps:
        return "—（跨端点）" if item["section"] == 8 else "—"

    if len(eps) > 1:
        blob = item["when"] + item["then"]
        picked = [e for kw, e in ENDPOINT_HINTS if kw in blob and e in eps]
        if picked:
            eps = picked

    hits = [controllers[e]["where"] for e in eps if e in controllers]
    if not hits:
        return "⚠️ 找不到对应 action"
    seen: list[str] = []
    for h in hits:
        if h not in seen:
            seen.append(h)
    if len(seen) == 1:
        return f"`{seen[0]}`"
    return f"`{seen[0]}` 等 {len(seen)} 个"


# ---------------------------------------------------------------- 渲染


def cell(text: str, limit: int = 70) -> str:
    t = " ".join(text.split()).replace("|", "\\|")
    return t if len(t) <= limit else t[: limit - 1] + "…"


STATUS = {"Passed": "✅ 绿", "Failed": "❌ 红", "Error": "❌ 错误",
          "Timeout": "❌ 超时", "NotExecuted": "⬜ 跳过", "Skipped": "⬜ 跳过", "": "⬜ 跳过"}


def render(items, tests, controllers, problems, test_errors, sources) -> tuple[str, int]:
    rows = []
    counts = {"green": 0, "red": 0, "skip": 0, "none": 0, "ghost": [], "other": 0}
    used: set[tuple[str, str]] = set()

    for it in items:
        matched = []
        for (cls, name), info in tests.items():
            if id_of_test(info["traits"], name) == it["id"]:
                matched.append((cls, name, info))
                used.add((cls, name))

        if not matched:
            counts["none"] += 1
            status, test_cell = "⬜ 未写", "—"
        else:
            outcomes = [m[2]["outcome"] for m in matched]
            agg = "Failed" if any(RANK.get(o, 0) >= 3 for o in outcomes) else (
                "Passed" if all(o == "Passed" for o in outcomes) else "Skipped")
            counts[{"Passed": "green", "Failed": "red", "Skipped": "skip"}[agg]] += 1
            status = STATUS.get(agg, agg)
            test_cell = "<br>".join(f"`{c}.{n}`" for c, n, _ in matched[:3])
            if len(matched) > 3:
                test_cell += f"<br>…共 {len(matched)} 个"

        rows.append(
            f"| **{it['id']}** | {cell(it['given'], 26)} | {cell(it['when'], 26)} | "
            f"{cell(it['then'], 60)} | {resolve_impl(it, controllers)} | {test_cell} | {status} |"
        )

    # 「无主测试」只报警真正像契约测试的（方法名或 trait 带编号但编号不存在）。
    # 架构测试 / 普通单元测试本来就不对应 A1–G2，只计数，不算异常。
    for (cls, name), info in sorted(tests.items()):
        if (cls, name) in used:
            continue
        if info["traits"] or re.match(r"^[A-G]\d+(_|$)", name):
            counts["ghost"].append(f"{cls}.{name}")
        else:
            counts["other"] += 1

    total = len(items)
    ok = counts["green"] == total and not problems and not counts["ghost"]
    head = f"""# 契约验收对照表（脚本生成）

> 本文档由 `python scripts/contract_matrix.py` 生成，**不要手改**——下次生成会覆盖。
> 手写的「现在红还是绿、缺什么」在 [知识资料管理.md](知识资料管理.md)。
> 契约原文：[接口/知识资料管理接口.md](../设计/接口/知识资料管理接口.md)

| 项 | 值 |
|---|---|
| 生成时间 | {datetime.now().strftime('%Y-%m-%d %H:%M')} |
| 契约条目 | {total} |
| 测试来源 | {'、'.join(sources) if sources else '（无 TRX）'} |
| 结论 | {'**全绿**' if ok else '**未全绿**'} |

## 总览

| 绿 | 红 | 跳过 | 未写 | 契约编号问题 | 无主测试 | 非契约测试 |
|---|---|---|---|---|---|---|
| {counts['green']} | {counts['red']} | {counts['skip']} | {counts['none']} | {len(problems)} | {len(counts['ghost'])} | {counts['other']} |

「非契约测试」= 架构测试与普通单元测试，不对应 A1–G2，只证明别的约定（R1–R6、预算棘轮），不参与本表对账。
"""

    if problems or counts["ghost"] or test_errors:
        head += "\n## 对账异常\n\n"
        for p in problems:
            head += f"- 🔴 {p}\n"
        for g in counts["ghost"]:
            head += f"- 🔴 测试 `{g}` 没有对应任何契约编号（编号写错？还是契约漏了这条？）\n"
        for e in test_errors:
            head += f"- 🔴 TRX 解析失败：{e}\n"

    head += f"""
## 明细

实现列 = `类.方法:行号`（从 Controller 源码扫出来的真实位置）。
测试列 = TRX 里的 `类.方法`（跑了什么就是什么，不是自称）。

| 编号 | Given | When | Then | 实现 | 测试 | 状态 |
|---|---|---|---|---|---|---|
{chr(10).join(rows)}

## 这张表怎么维护

| 你做了什么 | 会怎样 |
|---|---|
| 写了一条测试，方法名以 `A4_` 开头（或打了 `[Trait("Contract","A4")]`） | 该行「测试」列自动出现，状态变绿或红 |
| 改了 Controller 路由 | 「实现」列跟着变；若找不到会显示 ⚠️ |
| 契约里加了 `H1` 却没人写测试 | 明细多一行 ⬜ 未写，退出码 1 |
| 测试方法名打错成 `A44_` | 它变成「无主测试」，同时 A4 仍显示未写 |

**端点映射是脚本常量 `SECTION_ENDPOINTS`**（§{list(SECTION_ENDPOINTS)}），改端点时要一起改，否则实现列会指错。
"""
    return head, (0 if ok else 1)


# ---------------------------------------------------------------- main


def print_summary(md: str, trx_count: int, test_count: int, code: int) -> None:
    body = md.split("## 明细")[0]
    lines = body.splitlines()
    for line in lines:
        if line.startswith("| 契约条目") or line.startswith("| 结论"):
            print(line)
    try:
        i = next(i for i, l in enumerate(lines) if l.startswith("## 总览"))
        print("\n" + lines[i + 2])
        print(lines[i + 3])
        print(lines[i + 4])
    except (StopIteration, IndexError):
        pass
    alarms = [l for l in lines if l.startswith("- 🔴")]
    if alarms:
        print(f"\n对账异常 {len(alarms)} 条：")
        for a in alarms[:5]:
            print("  " + a)
    print(f"\nTRX {trx_count} 份 ｜ 测试 {test_count} 个 ｜ 退出码 {code}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-test", action="store_true", help="不跑测试，复用已有 TRX")
    ap.add_argument("--dry-run", action="store_true", help="只打印总览，不写文件")
    ap.add_argument("--trx-dir", default=None, help="TRX 目录（默认 tests/TestResults）")
    ap.add_argument("--out", default=str(OUT))
    args = ap.parse_args()

    items, problems = parse_contract()
    problems += check_id_sequence(items)
    controllers = scan_controllers()

    trx_dir = Path(args.trx_dir) if args.trx_dir else RESULTS_DIR
    if args.no_test:
        trx = sorted(trx_dir.glob("*.trx"))
        if not trx:
            print(f"{trx_dir} 里没有 TRX，请去掉 --no-test 先跑一次。")
            return 1
    else:
        print(f"跑 {len(TEST_PROJECTS)} 个测试项目…", flush=True)
        trx = run_tests()

    tests, test_errors = parse_trx(trx)
    md, code = render(items, tests, controllers, problems, test_errors,
                      [p.stem for p in trx])

    if args.dry_run:
        print_summary(md, len(trx), len(tests), code)
        return code

    Path(args.out).write_text(md, encoding="utf-8", newline="\n")
    print_summary(md, len(trx), len(tests), code)
    print(f"已写入 {args.out}")
    return code


if __name__ == "__main__":
    sys.exit(main())

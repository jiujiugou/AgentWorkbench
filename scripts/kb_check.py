#!/usr/bin/env python3
"""知识库元数据的读取与校验：documents/*.toml + invariants.toml。

它只做三件事：
1. 校验元数据本身没坏：文档 / 证据 / 符号 / 事实都还对得上真实仓库。
2. --impact：给一个改动路径，按 doc-map 的 watch 列出该核对哪份文档、关联哪条不变量、有哪些事实要复核。
3. --self-test：证明上面的判断不是空壳。

事实对账（fact）支持三种 check：
- json      ：读 appsettings 一类 JSON，按冒号路径取值，比对 expect / expect_count / expect_contains
- csproj    ：读 PackageReference 的 Version
- test-count：数测试文件里的 [Fact]/[Theory]，防止测试被删了文档还留着

刻意不做：
- 不写 status。测试今天通过不代表下次提交仍通过；状态每次现算。
- 不跑测试。本脚本只做静态对账，跑测试交给 scripts/ci.sh。
- 不落 JSON/报告文件。没有 CI 时落文件=没人读，输出到终端即可。
- 不判断语义漂移。"文档描述的控制流是否仍符合代码"机器判不了，只在报告末尾标出需人工审查。
- 不做自动修复。AI 可以提议，不能改权威规则。

用法：
    python scripts/kb_check.py                 # 全量校验元数据
    python scripts/kb_check.py --impact PATH   # 改了某文件，输出影响报告
    python scripts/kb_check.py --list          # 列出 doc-map / invariants / 事实
    python scripts/kb_check.py --self-test
"""

from __future__ import annotations

import json
import re
import sys
import tomllib
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
KB = ROOT / "docs" / "kb"
DOCS_DIR = KB / "documents"


def load(name: str) -> dict:
    path = KB / name
    if not path.exists():
        raise SystemExit(f"元数据缺失：{path.as_posix()}")
    with path.open("rb") as f:
        return tomllib.load(f)


def load_documents() -> list[dict]:
    """一份文档一个 TOML：docs/kb/documents/*.toml，拼成一张表并校验 id 唯一。"""
    files = sorted(DOCS_DIR.glob("*.toml"))
    if not files:
        raise SystemExit(f"没有文档元数据：{DOCS_DIR.as_posix()}/*.toml")
    out: list[dict] = []
    seen: dict[str, str] = {}
    for f in files:
        with f.open("rb") as fh:
            data = tomllib.load(fh)
        for d in data.get("documents", []):
            did = d.get("id", "?")
            if did in seen:
                raise SystemExit(f"文档 id 重复：{did}（{seen[did]} 与 {f.name}）——一份文档只能有一个文件")
            if not d.get("path"):
                raise SystemExit(f"{f.name} 里的 {did} 缺 path")
            seen[did] = f.name
            out.append(d)
    return out


def rel(p: Path) -> str:
    return p.relative_to(ROOT).as_posix()


def match_watch(path: str, pattern: str) -> bool:
    """watch 匹配。支持尾部 /** 的递归，其余按单层 glob。"""
    if pattern.endswith("/**"):
        return path.startswith(pattern[:-3] + "/")
    regex = "^" + "".join(
        ".*" if c == "*" else "[^/]" if c == "?" else re.escape(c) for c in pattern
    ) + "$"
    return re.match(regex, path) is not None


def test_filter_hits(project: str, filt: str) -> tuple[bool, str]:
    """校验测试过滤器能匹配到真实文件/内容。"""
    proj = ROOT / project
    if not proj.is_dir():
        return False, f"测试项目目录不存在：{project}"

    if "~" in filt:
        needle = filt.split("~", 1)[1].strip()
        for p in proj.rglob("*.cs"):
            if "obj" in p.parts or "bin" in p.parts:
                continue
            if needle in p.stem or needle in p.read_text(encoding="utf-8", errors="ignore"):
                return True, rel(p)
        return False, f"没有任何测试文件含 {needle!r}"

    for p in proj.rglob("*.cs"):
        if "obj" in p.parts or "bin" in p.parts:
            continue
        if p.stem == filt:
            return True, rel(p)
    return False, f"没有文件名为 {filt}.cs"


def resolve_json(node, path: str):
    """按冒号路径取值。列表遇到非数字段时按 Name 字段挑一项。"""
    for seg in path.split(":"):
        if isinstance(node, list):
            if seg.isdigit():
                node = node[int(seg)]
                continue
            hit = next(
                (
                    x
                    for x in node
                    if isinstance(x, dict) and (x.get("Name") == seg or x.get("name") == seg)
                ),
                None,
            )
            if hit is None:
                raise KeyError(f"列表里没有 Name={seg} 的项")
            node = hit
        elif isinstance(node, dict):
            if seg not in node:
                raise KeyError(f"缺键 {seg}")
            node = node[seg]
        else:
            raise KeyError(f"路径在 {seg} 处落到标量")
    return node


def check_fact(fact: dict) -> str | None:
    """返回 None = 对账通过；返回字符串 = 失败原因。"""
    kind = fact.get("check")
    path = fact.get("path", "")
    p = ROOT / path
    if not p.is_file():
        return f"被检查文件不存在：{path}"

    if kind == "json":
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            return f"JSON 解析失败：{e}"
        try:
            v = resolve_json(data, fact["json_path"])
        except KeyError as e:
            return f"json_path 取不到值：{e}"
        if "expect_count" in fact:
            if not isinstance(v, (list, dict)):
                return "expect_count 用在非集合值上"
            if len(v) != fact["expect_count"]:
                return f"数量不符：实际 {len(v)}，期望 {fact['expect_count']}"
        if "expect_contains" in fact:
            if not isinstance(v, list):
                return "expect_contains 用在非列表值上"
            miss = [x for x in fact["expect_contains"] if x not in v]
            if miss:
                return f"缺少元素：{miss}"
        if "expect" in fact and v != fact["expect"]:
            return f"值不符：实际 {v!r}，期望 {fact['expect']!r}"
        return None

    if kind == "csproj":
        try:
            root = ET.parse(p).getroot()
        except ET.ParseError as e:
            return f"csproj 解析失败：{e}"
        for ref in root.iter():
            if not ref.tag.endswith("PackageReference"):
                continue
            name = ref.get("Include") or ref.get("Update")
            if name != fact.get("package"):
                continue
            ver = ref.get("Version")
            if ver is None:
                for child in ref:
                    if child.tag.endswith("Version"):
                        ver = (child.text or "").strip()
            if ver != fact.get("expect"):
                return f"版本不符：{ver} != {fact.get('expect')}"
            return None
        return f"没找到 PackageReference {fact.get('package')}"

    if kind == "test-count":
        text = p.read_text(encoding="utf-8", errors="ignore")
        n = len(re.findall(r"\[(?:Fact|Theory)\]", text))
        if n != fact.get("expect"):
            return f"测试方法数不符：{n} != {fact.get('expect')}"
        return None

    return f"未知 check 类型：{kind}"


def check_symbols(ev: dict) -> str | None:
    syms = ev.get("symbols") or []
    if not syms:
        return None
    text = (ROOT / ev["path"]).read_text(encoding="utf-8", errors="ignore")
    miss = [s for s in syms if s not in text]
    return f"符号未出现在该文件：{miss}" if miss else None


def validate() -> int:
    doc_map = load_documents()
    invs = load("invariants.toml").get("invariant", [])
    bad = 0
    facts = sum(len(d.get("fact", [])) for d in doc_map)

    print(f"documents/：{len(doc_map)} 份文档；invariants：{len(invs)} 条不变量；facts：{facts} 条\n")

    for d in doc_map:
        did, dpath = d.get("id", "?"), d.get("path", "")
        if not (ROOT / dpath).is_file():
            print(f"FAIL {did} 文档不存在：{dpath}")
            bad += 1
            continue
        for ev in d.get("evidence", []):
            epath = ev.get("path", "")
            if not (ROOT / epath).is_file():
                print(f"FAIL {did} 证据不存在：{epath}")
                bad += 1
                continue
            why = check_symbols(ev)
            if why:
                print(f"FAIL {did} {why}（{epath}）")
                bad += 1
        for fact in d.get("fact", []):
            why = check_fact(fact)
            if why:
                print(f"FAIL {fact.get('id', '?')} {fact.get('claim', '')} → {why}")
                print(f"     FIX：核对 {fact.get('path')}；别为了变绿改 expect，先判断是事实变了还是文档过时")
                bad += 1

    for inv in invs:
        iid = inv.get("id", "?")
        dpath = inv.get("document", "")
        if dpath and not (ROOT / dpath).is_file():
            print(f"FAIL {iid} 声称所在文档不存在：{dpath}")
            bad += 1
        for ev in inv.get("evidence", []):
            if ev.get("type") == "source":
                if not (ROOT / ev.get("path", "")).is_file():
                    print(f"FAIL {iid} 源码证据不存在：{ev.get('path')}")
                    bad += 1
            elif ev.get("type") == "test":
                ok, detail = test_filter_hits(ev.get("project", ""), ev.get("filter", ""))
                if not ok:
                    print(f"FAIL {iid} 测试证据失效：{detail}")
                    bad += 1

    if bad:
        print(f"\n元数据坏了 {bad} 处。FIX：改正 path/symbol/expect，或删除已失效条目。")
        print("REF：这些是数据，权威在仓库真实文件；不要为了变绿去改指向。")
        return 1
    print("元数据自洽：文档、证据、符号、事实全部对得上真实仓库。")
    print("注意：这只证明可枚举部分没漂，不证明文档语义正确。")
    return 0


def impact(path: str) -> int:
    doc_map = load_documents()
    invs = load("invariants.toml").get("invariant", [])
    norm = path.replace("\\", "/").lstrip("./")

    hits = [d for d in doc_map if any(match_watch(norm, w) for w in d.get("watch", []))]

    print("Documentation Impact Report")
    print("-" * 40)
    print(f"触发：{norm}\n")

    if not hits:
        print("未命中任何文档映射。")
        print("可能确实不涉及文档；若属公共接口/职责/依赖/状态/流程/安全/配置/部署八类之一，")
        print("FIX  在 docs/kb/documents/ 对应文件里补一条 watch。")
        print("REF  AGENTS.md §4（改完代码先核对文档那条）")
        return 0

    for d in hits:
        print(f"需要核对：{d['path']}  ({d.get('id', '?')})")

        # 关联判定两条，任一成立：
        # ① 不变量直接声称在这份文档里；
        # ② 不变量的 source 证据落在本文档 watch 的目录范围内。
        # 不能用「evidence.path 是否出现在文档路径里」——test 型证据没有 path 键，
        # 空字符串 in 任意串恒为真，会把所有不变量都误关联（已实测踩过）。
        related = []
        for i in invs:
            if i.get("document", "") == d["path"]:
                related.append(i)
                continue
            if any(
                ev.get("type") == "source"
                and any(match_watch(ev.get("path", ""), w) for w in d.get("watch", []))
                for ev in i.get("evidence", [])
            ):
                related.append(i)
        if related:
            print("  关联不变量：")
            for i in related:
                print(f"    {i['id']}  {i['claim']}")

        print("  自动检查：")
        ok = (ROOT / d["path"]).is_file()
        print(f"    [{'PASS' if ok else 'FAIL'}] 文档存在")
        for ev in d.get("evidence", []):
            p = ev.get("path", "")
            eok = (ROOT / p).is_file()
            syms = ev.get("symbols") or []
            syok = eok and check_symbols(ev) is None
            mark = "PASS" if (eok and syok) else "FAIL"
            extra = f"  symbols {len(syms)}" if syms else ""
            print(f"    [{mark}] {ev.get('type')}: {p}{extra}")
        for fact in d.get("fact", []):
            why = check_fact(fact)
            print(f"    [{'PASS' if why is None else 'FAIL'}] {fact.get('id')}  {fact.get('claim')}")
            if why:
                print(f"           {why}")

        print("  仍需语义审查（机器判不了）：")
        print("    当前实现是否仍符合文档描述？是否出现文档未覆盖的新路径？")
        print()

    print("提示：本报告只覆盖可枚举事实；语义漂移需要人或 agent 对照代码，别把 PASS 当结论。")
    return 0


def list_all() -> int:
    for d in load_documents():
        print(f"{d.get('id')}  {d['path']}")
        for w in d.get("watch", []):
            print(f"    watch  {w}")
        for ev in d.get("evidence", []):
            syms = ev.get("symbols") or []
            tail = f"  symbols={','.join(syms)}" if syms else ""
            print(f"    {ev.get('type'):6} {ev.get('path')}{tail}")
        for fact in d.get("fact", []):
            print(f"    fact   {fact.get('id')}  [{fact.get('check')}]  {fact.get('claim')}")
    print()
    for i in load("invariants.toml").get("invariant", []):
        print(f"{i['id']}  {i['claim']}")
    return 0


def self_test() -> int:
    ok = True

    cases = [
        ("src/Core/Tools/AgentWorkbench.Tools.Mcp/McpToolRegistry.cs", "src/Core/Tools/**", True),
        ("src/Core/Tools.Mcp/X.cs", "src/Core/Tools/**", False),
        ("src/Host/AgentWorkbench.Webapi/appsettings.json", "src/Host/**", True),
        ("src/Core/Runtime/AgentWorkbench.Runtime.Tools/X.cs",
         "src/Core/Runtime/AgentWorkbench.Runtime.Tools/**", True),
    ]
    for path, pat, expect in cases:
        got = match_watch(path, pat)
        print(f"  glob {pat} vs {path} -> {got} (期望 {expect})")
        if got != expect:
            ok = False

    hits, _ = test_filter_hits("tests/AgentWorkbench.ApplicationTests", "S06ContractTests")
    print(f"  filter S06ContractTests 命中: {hits}")
    if not hits:
        ok = False
    miss, _ = test_filter_hits("tests/AgentWorkbench.ApplicationTests", "NoSuchTestXyz")
    print(f"  filter NoSuchTestXyz 命中: {miss} (期望 False)")
    if miss:
        ok = False

    # 事实对账不是恒真：同一个文件，正确 expect 过、错误 expect 必须失败
    good = {
        "check": "test-count",
        "path": "tests/AgentWorkbench.ApplicationTests/Flow/S04ContractTests.cs",
        "expect": 15,
    }
    bad = dict(good, expect=999)
    print(f"  fact test-count 正确值 -> {check_fact(good) or 'PASS'} (期望 PASS)")
    if check_fact(good) is not None:
        ok = False
    print(f"  fact test-count 错误值 -> {'FAIL 被识别' if check_fact(bad) else 'PASS'} (期望 FAIL)")
    if check_fact(bad) is None:
        ok = False

    sample = {"Mcp": {"Servers": [{"Name": "rag", "AllowTools": ["a"]}, {"Name": "erp", "AllowTools": []}]}}
    got = resolve_json(sample, "Mcp:Servers:erp:AllowTools")
    print(f"  json 路径按 Name 选列表项 -> {got} (期望 [])")
    if got != []:
        ok = False

    try:
        docs = load_documents()
        load("invariants.toml")
        ids = [d.get("id") for d in docs]
        print(f"  documents/*.toml 可解析：{len(docs)} 份，id 唯一={len(ids) == len(set(ids))}")
        if len(ids) != len(set(ids)):
            ok = False
    except SystemExit as e:
        print(f"  FAIL {e}")
        ok = False

    print("自检通过。" if ok else "自检失败。")
    return 0 if ok else 1


def main() -> int:
    args = sys.argv[1:]
    if "--self-test" in args:
        return self_test()
    if "--list" in args:
        return list_all()
    if "--impact" in args:
        i = args.index("--impact")
        if i + 1 >= len(args):
            print("用法：python scripts/kb_check.py --impact <改动文件路径>")
            return 2
        return impact(args[i + 1])
    return validate()


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""知识库元数据的读取与校验：documents/*.toml + contracts/*.toml。

它只做三件事：
1. 校验元数据本身没坏：文档 / 证据 / 符号 / 事实 / 契约用例都还对得上真实仓库。
2. --impact：给一个改动路径，按 doc-map 的 watch 列出该核对哪份文档、关联哪些契约、有哪些事实要复核。
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
CONTRACTS_DIR = KB / "contracts"


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
            dpath = d.get("path", "")
            if not dpath:
                raise SystemExit(f"{f.name} 缺 path —— 文档用路径做身份，不再发 DOC-* 编号")
            if dpath in seen:
                raise SystemExit(f"同一份文档出现在两个文件：{dpath}（{seen[dpath]} 与 {f.name}）")
            seen[dpath] = f.name
            out.append(d)
    return out


def load_contracts() -> list[dict]:
    """一份文档一个契约文件：docs/kb/contracts/*.toml，拼成一张表并校验 id 跨文件唯一。"""
    files = sorted(CONTRACTS_DIR.glob("*.toml"))
    if not files:
        raise SystemExit(f"没有契约元数据：{CONTRACTS_DIR.as_posix()}/*.toml")
    out: list[dict] = []
    seen: dict[str, str] = {}
    for f in files:
        with f.open("rb") as fh:
            data = tomllib.load(fh)
        for c in data.get("contract", []):
            cid = c.get("id", "")
            if not cid:
                raise SystemExit(f"{f.name} 里有契约缺 id —— 契约用 ID 做身份")
            if cid in seen:
                raise SystemExit(f"契约 id 重复：{cid}（{seen[cid]} 与 {f.name}）")
            seen[cid] = f.name
            out.append(c)
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


def section_exists(doc: Path, sec: str) -> bool:
    """文档里是否存在 `## 2.4` / `### 3.4 …` 这样的小节标题。"""
    if not sec:
        return False
    text = doc.read_text(encoding="utf-8", errors="ignore")
    return re.search(rf"^#{{2,4}}\s*{re.escape(sec)}(?:\s|$)", text, re.MULTILINE) is not None


def check_contract(c: dict) -> str | None:
    """契约条目自洽性。返回 None = 通过；否则返回失败原因。

    它只验「指向的东西还在」：文档在、小节能定位、实现符号在、有用例则用例名真的在，
    无判据必须写 gap。不验主张真假——那是测试和人的事。
    """
    cid = c.get("id", "?")
    if c.get("status"):
        return f"{cid} 写了 status —— TOML 不存验证状态，缺证据用 gap 登记"
    doc = c.get("document", "")
    if not doc:
        return f"{cid} 缺 document"
    dp = ROOT / doc
    if not dp.is_file():
        return f"{cid} 声称所在文档不存在：{doc}"
    if not section_exists(dp, c.get("section", "")):
        return f"{cid} 文档里定位不到 §{c.get('section', '')}"
    ip = c.get("impl_path", "")
    if ip:
        fp = ROOT / ip
        if not fp.is_file():
            return f"{cid} 实现位置不存在：{ip}"
        text = fp.read_text(encoding="utf-8", errors="ignore")
        miss = [s for s in c.get("impl_symbols", []) if s not in text]
        if miss:
            return f"{cid} 符号未出现在实现文件：{miss}（{ip}）"

    evs = c.get("evidence", [])
    if not evs:
        return f"{cid} 没有任何 evidence —— 无判据必须写 gap，留空会被当成已验"
    has_test = False
    for ev in evs:
        kind = ev.get("kind", "")
        if kind == "gap":
            if not ev.get("reason"):
                return f"{cid} 的 gap 没写 reason —— 空着等于伪装成已验"
            continue
        if kind != "test":
            return f"{cid} 有未知 evidence.kind：{kind!r}（只认 test / gap）"
        has_test = True
        tp = ROOT / ev.get("path", "")
        if not ev.get("path") or not tp.is_file():
            return f"{cid} 测试文件不存在：{ev.get('path')}"
        src = tp.read_text(encoding="utf-8", errors="ignore")
        miss = [x for x in ev.get("cases", []) if x not in src]
        if miss:
            return f"{cid} 用例未出现在 {ev['path']}：{miss}（测试改名或删了）"
    if not has_test and not any(e.get("kind") == "gap" for e in evs):
        return f"{cid} 既没有 test 也没写 gap —— 无判据必须写明原因"
    return None


def contract_doc_registered(c: dict, doc_paths: set[str]) -> str | None:
    """契约只属于一份已登记文档。返回 None = 已登记。"""
    if c.get("document") in doc_paths:
        return None
    return f"{c.get('id')} 声称所在文档未登记在 documents/：{c.get('document')}"


def validate() -> int:
    doc_map = load_documents()
    contracts = load_contracts()
    bad = 0
    facts = sum(len(d.get("fact", [])) for d in doc_map)

    print(f"documents/：{len(doc_map)} 份文档；contracts：{len(contracts)} 条；facts：{facts} 条\n")

    for d in doc_map:
        dpath = d.get("path", "")
        if not (ROOT / dpath).is_file():
            print(f"FAIL 文档不存在：{dpath}")
            bad += 1
            continue
        for ev in d.get("evidence", []):
            epath = ev.get("path", "")
            if not (ROOT / epath).is_file():
                print(f"FAIL {dpath} 证据不存在：{epath}")
                bad += 1
                continue
            why = check_symbols(ev)
            if why:
                print(f"FAIL {dpath} {why}")
                bad += 1
        for fact in d.get("fact", []):
            why = check_fact(fact)
            if why:
                print(f"FAIL {fact.get('claim', '')} → {why}")
                print(f"     FIX：核对 {fact.get('path')}；别为了变绿改 expect，先判断是事实变了还是文档过时")
                bad += 1

    for c in contracts:
        why = check_contract(c)
        if why:
            print(f"FAIL {why}")
            bad += 1
            continue
        # 契约只属于一份已登记文档：文档在 documents/ 里没元数据，说明它根本没进映射，
        # 契约就挂在一个没人维护 watch 的文档上。
        why = contract_doc_registered(c, {d.get("path") for d in doc_map})
        if why:
            print(f"FAIL {why}")
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
    contracts = load_contracts()
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
        print(f"需要核对：{d['path']}")

        own = [c for c in contracts if c.get("document") == d["path"]]
        if own:
            print("  关联契约：")
            for c in own:
                print(f"    {c['id']}  §{c.get('section')}  {c.get('statement')}")
                for ev in c.get("evidence", []):
                    if ev.get("kind") == "test":
                        n = len(ev.get("cases", []))
                        print(f"        [test] {Path(ev['path']).name} × {n} 用例 — {ev.get('strength', '')}")
                    else:
                        print(f"        [缺口] {ev.get('reason', '')}")

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
            print(f"    [{'PASS' if why is None else 'FAIL'}] {fact.get('claim')}")
            if why:
                print(f"           {why}")

        print("  仍需语义审查（机器判不了）：")
        print("    当前实现是否仍符合文档描述？是否出现文档未覆盖的新路径？")
        print()

    print("提示：本报告只覆盖可枚举事实；语义漂移需要人或 agent 对照代码，别把 PASS 当结论。")
    return 0


def list_all() -> int:
    for d in load_documents():
        print(f"{d['path']}")
        for w in d.get("watch", []):
            print(f"    watch  {w}")
        for ev in d.get("evidence", []):
            syms = ev.get("symbols") or []
            tail = f"  symbols={','.join(syms)}" if syms else ""
            print(f"    {ev.get('type'):6} {ev.get('path')}{tail}")
        for fact in d.get("fact", []):
            print(f"    fact   [{fact.get('check')}]  {fact.get('claim')}")
    print()
    for c in load_contracts():
        print(f"{c['id']}  §{c.get('section')}  {c.get('statement')}")
        for ev in c.get("evidence", []):
            if ev.get("kind") == "test":
                print(f"    test  {Path(ev['path']).name}  {', '.join(ev.get('cases', []))}")
                print(f"          {ev.get('strength', '')}")
                print(f"          变异点：{ev.get('mutation', '未写')}")
            else:
                print(f"    gap   {ev.get('reason', '')}")
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
    # 期望值不写死：S04 加过测试（15→19），写死会让自检自己漂成恒红。
    # 这里读实际方法数，只验证 check_fact 确实在比较而不是恒过/恒红。
    fixture = ROOT / "tests/AgentWorkbench.ApplicationTests/Flow/S04ContractTests.cs"
    actual = len(re.findall(r"\[Fact\]|\[Theory\]", fixture.read_text(encoding="utf-8")))
    good = {
        "check": "test-count",
        "path": "tests/AgentWorkbench.ApplicationTests/Flow/S04ContractTests.cs",
        "expect": actual,
    }
    bad = dict(good, expect=actual + 999)
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
        cs = load_contracts()
        paths = [d.get("path") for d in docs]
        print(f"  documents/*.toml 可解析：{len(docs)} 份，path 唯一={len(paths) == len(set(paths))}")
        print(f"  contracts/*.toml 可解析：{len(cs)} 条，id 唯一（重复会在加载时报错）")
        if len(paths) != len(set(paths)):
            ok = False
    except SystemExit as e:
        print(f"  FAIL {e}")
        ok = False

    # 契约：小节定位不是恒真；无判据必须写 gap；用例名必须真的在测试文件里
    doc = ROOT / "docs/core/工具执行.md"
    hit, miss_sec = section_exists(doc, "3.4"), section_exists(doc, "99.9")
    print(f"  契约小节定位 3.4 -> {hit}（期望 True） / 99.9 -> {miss_sec}（期望 False）")
    if not hit or miss_sec:
        ok = False

    base = {
        "id": "X-TEST-001",
        "document": "docs/core/工具执行.md",
        "section": "3.4",
        "impl_path": "src/Core/Runtime/AgentWorkbench.Runtime.Tools/ToolExecutionPipeline.cs",
        "impl_symbols": ["InspectApprovalAsync"],
    }
    no_ev = dict(base, evidence=[])
    why = check_contract(no_ev)
    print(f"  契约没有任何 evidence -> {why or 'PASS'}（期望 FAIL）")
    if why is None:
        ok = False

    good_gap = dict(base, evidence=[{"kind": "gap", "reason": "占位缺口说明"}])
    print(f"  契约只写 gap -> {check_contract(good_gap) or 'PASS'}（期望 PASS）")
    if check_contract(good_gap) is not None:
        ok = False

    empty_gap = dict(base, evidence=[{"kind": "gap", "reason": ""}])
    print(f"  契约 gap 不写原因 -> {'FAIL 被识别' if check_contract(empty_gap) else 'PASS'}（期望 FAIL）")
    if check_contract(empty_gap) is None:
        ok = False

    with_status = dict(good_gap, status="verified")
    print(f"  契约偷写 status -> {'FAIL 被识别' if check_contract(with_status) else 'PASS'}（期望 FAIL）")
    if check_contract(with_status) is None:
        ok = False

    s04 = "tests/AgentWorkbench.ApplicationTests/Flow/S04ContractTests.cs"
    good_case = dict(base, evidence=[{"kind": "test", "path": s04, "cases": ["S04_08"]}])
    print(f"  契约用例真的存在 -> {check_contract(good_case) or 'PASS'}（期望 PASS）")
    if check_contract(good_case) is not None:
        ok = False

    bad_case = dict(base, evidence=[{"kind": "test", "path": s04, "cases": ["S04_99_不存在"]}])
    print(f"  契约用例写错 -> {'FAIL 被识别' if check_contract(bad_case) else 'PASS'}（期望 FAIL）")
    if check_contract(bad_case) is None:
        ok = False

    registered = {"docs/core/工具执行.md"}
    hit_reg = contract_doc_registered(good_gap, registered)
    miss_reg = contract_doc_registered(dict(good_gap, document="docs/没登记.md"), registered)
    print(f"  契约挂已登记文档 -> {hit_reg or 'PASS'}（期望 PASS） / 挂未登记 -> {'FAIL 被识别' if miss_reg else 'PASS'}（期望 FAIL）")
    if hit_reg is not None or miss_reg is None:
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

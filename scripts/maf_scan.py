#!/usr/bin/env python3
"""MAF 家族机制层全扫描：从本机 NuGet 缓存的编译器 XML 文档抽取全部公共类型与成员。

产出（机械生成，可随时重跑覆盖）：
  docs/机制层/MAF机制层扫描.json  机器正本：包 → 命名空间 → 类型 → 成员，全文摘要
  docs/机制层/MAF机制层扫描.md    人读视图：同结构，成员摘要截断

版本不写死：从仓库各 obj/project.assets.json 里解析实际锁定的版本（脚本的
「版本与解决方案一致」由此保证）。升级框架后重跑本脚本，git diff 即升级影响面。

用法：python scripts/maf_scan.py
"""
from __future__ import annotations

import glob
import json
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from datetime import date
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
NUGET = Path.home() / ".nuget" / "packages"
OUT_DIR = REPO / "docs" / "机制层"
JSON_OUT = OUT_DIR / "MAF机制层扫描.json"
MD_OUT = OUT_DIR / "MAF机制层扫描.md"

# 只扫 AI 家族；名字包含这些片段的包都算
FAMILY = ("extensions.ai", "agents.ai", "modelcontextprotocol")
SUMMARY_LIMIT_JSON = 600
SUMMARY_LIMIT_MD = 200


def resolved_versions() -> dict[str, str]:
    """从所有 project.assets.json 提取家族包的实际解析版本（取最高）。"""
    best: dict[str, str] = {}
    for assets in glob.glob(str(REPO / "src/**/obj/project.assets.json"), recursive=True) + glob.glob(
        str(REPO / "tests/**/obj/project.assets.json"), recursive=True
    ):
        data = json.loads(Path(assets).read_text(encoding="utf-8"))
        for key in data.get("libraries", {}):
            name, _, ver = key.lower().partition("/")
            if any(f in name for f in FAMILY):
                if name not in best or ver > best[name]:
                    best[name] = ver
    return best


def clean(text: str | None, limit: int) -> str:
    """去掉内嵌 XML 标签、折叠空白、截断。"""
    if text is None:
        return ""
    text = re.sub(r"<[^>]+>", " ", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text[:limit]


def find_xml(pkg: str, ver: str) -> Path | None:
    base = NUGET / pkg / ver
    if not base.is_dir():
        return None
    hits = sorted(base.glob("lib/*/​*.xml")) or sorted(base.glob("lib/*/*.xml"))
    return hits[0] if hits else None


def parse_package(pkg: str, ver: str) -> dict:
    xml_path = find_xml(pkg, ver)
    if xml_path is None:
        return {"id": pkg, "version": ver, "error": "找不到 lib 下的 XML 文档", "namespaces": []}
    root = ET.parse(xml_path).getroot()

    # 第一遍：收集全部类型全名（XML 里嵌套类型与命名空间都用点分隔，无法从单个名字分辨；
    # 用「全名的前缀本身也是类型」来识别嵌套，剥到真命名空间为止）。
    type_fullnames = {
        m.get("name", "").partition(":")[2].split("(", 1)[0]
        for m in root.findall(".//member")
        if m.get("name", "").startswith("T:")
    }

    def peel(full: str) -> tuple[str, str]:
        """(类型全名) → (命名空间, 含嵌套链的类型名)。"""
        while True:
            ns, _, name = full.rpartition(".")
            if ns and ns in type_fullnames:  # 父段本身是类型 → 当前是嵌套类型
                full = ns
            else:
                return ns, name

    # 第二遍：归位。
    tree: dict[str, dict[str, dict]] = defaultdict(dict)
    for m in root.findall(".//member"):
        name = m.get("name", "")
        prefix, _, rest = name.partition(":")
        if prefix not in ("T", "M", "P", "F", "E") or not rest:
            continue
        rest = rest.split("(", 1)[0]  # 剥掉方法参数列表，否则参数类型里的点会污染归属
        summary = clean("".join(m.find("summary").itertext()) if m.find("summary") is not None else "", SUMMARY_LIMIT_JSON)
        kind = {"T": "type", "M": "method", "P": "property", "F": "field", "E": "event"}[prefix]
        if prefix == "T":
            ns, tname = peel(rest)
            tree[ns].setdefault(tname, {"kind": "type", "summary": summary, "members": []})
        else:
            ns, tname = peel(rest)
            slot = tree[ns].setdefault(tname, {"kind": "type", "summary": "", "members": []})
            slot["members"].append({"name": rest.rpartition(".")[2], "kind": kind, "summary": summary})

    namespaces = []
    for ns in sorted(tree):
        types = []
        for tname in sorted(tree[ns]):
            slot = tree[ns][tname]
            types.append(
                {
                    "name": tname,
                    "kind": slot["kind"],
                    "summary": slot["summary"] or "",
                    "members": sorted(slot["members"], key=lambda x: x["name"]),
                }
            )
        namespaces.append({"name": ns, "types": types})
    return {"id": pkg, "version": ver, "xml": str(xml_path.relative_to(NUGET)), "namespaces": namespaces}


def stats(doc: dict) -> str:
    lines = []
    for pkg in doc["packages"]:
        t = sum(len(ns["types"]) for ns in pkg["namespaces"])
        m = sum(len(ty["members"]) for ns in pkg["namespaces"] for ty in ns["types"])
        lines.append(f"  {pkg['id']} {pkg['version']}: {len(pkg['namespaces'])} 个命名空间 / {t} 个类型 / {m} 个成员")
    return "\n".join(lines)


def write_md(doc: dict) -> None:
    out = [
        "# MAF 家族机制层全扫描（人读视图）",
        "",
        f"- 生成：{doc['meta']['generated']}（脚本 `scripts/maf_scan.py` 机械生成，勿手改；升级框架后重跑，git diff 即升级影响面）",
        "- 结构：包 → 命名空间 → 类型 → 成员；摘要取自编译器 XML 文档，机器正本是同目录 `.json`（全文）",
        "- **边界**：XML 文档格式本身无法区分「嵌套类型」与「命名空间」，少数嵌套类型会被归成伪命名空间——类型与成员的清单完整准确，命名空间分组是近似；成员摘要可能截断",
        "",
    ]
    for pkg in doc["packages"]:
        out.append(f"## {pkg['id']} {pkg['version']}")
        if "error" in pkg:
            out.append(f"> {pkg['error']}")
            continue
        for ns in pkg["namespaces"]:
            out.append(f"### {ns['name']}")
            for ty in ns["types"]:
                out.append(f"#### {ty['name']}（{ty['kind']}）")
                if ty["summary"]:
                    out.append(ty["summary"])
                if ty["members"]:
                    out.append("| 成员 | 类别 | 摘要 |")
                    out.append("| --- | --- | --- |")
                    for mem in ty["members"]:
                        s = mem["summary"].replace("|", "\\|")
                        out.append(f"| `{mem['name']}` | {mem['kind']} | {s} |")
                out.append("")
        out.append("")
    MD_OUT.write_text("\n".join(out), encoding="utf-8", newline="\n")


def write_graph_html(doc: dict) -> None:
    """把 包→命名空间→类型 三层结构转成 Cytoscape.js 连线图（成员不进图，6500 个节点没法看）。"""
    nodes: list[dict] = []
    edges: list[dict] = []
    pkg_colors = ["#185FA5", "#7B4EA8", "#1E7A46", "#A8552E", "#8A2E5C", "#2E6E8A", "#6E6A2E", "#8A3A3A", "#3E5C2E"]
    for pi, pkg in enumerate(doc["packages"]):
        color = pkg_colors[pi % len(pkg_colors)]
        pkg_id = f"p{pi}"
        nodes.append({"data": {"id": pkg_id, "label": pkg["id"] + " " + pkg.get("version", ""), "kind": "pkg", "info": f"{pkg.get('version', '')} · {len(pkg.get('namespaces', []))} 个命名空间"}, "classes": "pkg"})
        for ni, ns in enumerate(pkg.get("namespaces", [])):
            ns_id = f"p{pi}n{ni}"
            nodes.append({"data": {"id": ns_id, "label": ns["name"], "kind": "ns", "info": f"{len(ns['types'])} 个类型", "pkg": pkg["id"]}, "classes": "ns"})
            edges.append({"data": {"id": f"e{pkg_id}-{ns_id}", "source": pkg_id, "target": ns_id}})
            for ti, ty in enumerate(ns["types"]):
                ty_id = f"p{pi}n{ni}t{ti}"
                nodes.append({"data": {"id": ty_id, "label": ty["name"], "kind": "type", "info": (ty["summary"] or "（无摘要）")[:300], "pkg": pkg["id"]}, "classes": "type"})
                edges.append({"data": {"id": f"e{pkg_id}-{ns_id}-{ty_id}", "source": ns_id, "target": ty_id}})
    graph = json.dumps({"nodes": nodes, "edges": edges}, ensure_ascii=False, separators=(",", ":"))
    html = """<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>MAF 家族机制层图谱</title>
<script src="https://unpkg.com/cytoscape@3.30.4/dist/cytoscape.min.js"></script>
<style>
  body{margin:0;font-family:system-ui,"Microsoft YaHei",sans-serif;background:#F5F4F0;color:#2C2C2A}
  #bar{padding:8px 14px;background:#fff;border-bottom:1px solid #DDD;display:flex;gap:16px;align-items:center;flex-wrap:wrap}
  #bar b{font-size:14px}
  #bar label{font-size:13px}
  #search{width:220px;padding:4px 8px;border:1px solid #BBB;border-radius:4px;font-size:13px}
  #count{font-size:12px;color:#666}
  #cy{position:absolute;top:44px;bottom:0;left:0;right:0}
  #tip{position:absolute;right:12px;bottom:12px;max-width:420px;background:#fff;border:1px solid #CCC;border-radius:6px;padding:10px 12px;font-size:12px;display:none;box-shadow:0 2px 8px rgba(0,0,0,.15)}
  #tip h4{margin:0 0 6px;font-size:13px}
</style></head><body>
<div id="bar"><b>MAF 家族机制层图谱</b>
  <label><input type="checkbox" id="lvPkg" checked> 包</label>
  <label><input type="checkbox" id="lvNs" checked> 命名空间</label>
  <label><input type="checkbox" id="lvType"> 类型（1700+ 节点，勾选会卡几秒）</label>
  <input id="search" placeholder="搜类型名，回车定位">
  <span id="count"></span>
</div>
<div id="cy"></div>
<div id="tip"><h4></h4><div></div></div>
<script>
const GRAPH = __GRAPH__;
const cy = cytoscape({
  container: document.getElementById('cy'),
  elements: GRAPH.nodes.map(n => ({data: n.data, classes: n.classes})) 
                   .concat(GRAPH.edges.map(e => ({data: e.data}))),
  style: [
    {selector:'node.pkg', style:{'background-color':'#33322E','label':'data(label)','color':'#fff','font-size':13,'text-valign':'center','text-halign':'center','width':34,'height':34,'shape':'round-rectangle'}},
    {selector:'node.ns', style:{'background-color':'#185FA5','label':'data(label)','color':'#fff','font-size':10,'text-valign':'center','text-halign':'center','width':22,'height':22,'shape':'round-rectangle'}},
    {selector:'node.type', style:{'background-color':'#9BB7CF','label':'data(label)','color':'#222','font-size':8,'width':10,'height':10,'shape':'ellipse'}},
    {selector:'edge', style:{'width':1,'line-color':'#C9C7C0','curve-style':'haystack','haystack-radius':0.4}},
    {selector:'.dim', style:{'opacity':0.12}},
    {selector:'.hit', style:{'border-width':3,'border-color':'#C4451C'}}
  ],
  layout:{name:'cose', idealEdgeLength:()=>70, nodeRepulsion:()=>9000, animate:false},
  wheelSensitivity:0.2
});
const show = () => {
  const p = document.getElementById('lvPkg').checked,
        n = document.getElementById('lvNs').checked,
        t = document.getElementById('lvType').checked;
  const visible = el => {
    const k = el.hasClass('pkg') ? 'pkg' : el.hasClass('ns') ? 'ns' : 'type';
    return (k==='pkg' && p) || (k==='ns' && n) || (k==='type' && t);
  };
  cy.batch(() => {
    cy.nodes().forEach(el => el.style('display', visible(el) ? 'element' : 'none'));
    cy.edges().forEach(e => e.style('display',
      (visible(e.source()) && visible(e.target())) ? 'element' : 'none'));
  });
  document.getElementById('count').textContent =
    cy.nodes(':visible').length + ' 节点 / ' + cy.edges(':visible').length + ' 边';
};
document.getElementById('lvPkg').onchange = show;
document.getElementById('lvNs').onchange = show;
document.getElementById('lvType').onchange = () => { show(); };
document.getElementById('lvType').onchange();
cy.on('tap', 'node', e => {
  const tip = document.getElementById('tip');
  tip.querySelector('h4').textContent = e.target.data('label');
  tip.querySelector('div').textContent = e.target.data('info') || '';
  tip.style.display = 'block';
});
document.getElementById('search').addEventListener('keydown', e => {
  if (e.key !== 'Enter') return;
  const q = e.target.value.trim().toLowerCase();
  cy.nodes().removeClass('hit').removeClass('dim');
  if (!q) { show(); return; }
  const hits = cy.nodes('[label @*= "' + q + '"]');
  cy.batch(() => {
    cy.nodes().addClass('dim'); hits.removeClass('dim').addClass('hit');
    hits.connectedEdges().removeClass('dim');
  });
  document.getElementById('count').textContent = hits.length + ' 个命中';
});
show();
</script></body></html>"""
    html = html.replace("__GRAPH__", graph)
    (OUT_DIR / "MAF机制层图谱.html").write_text(html, encoding="utf-8", newline="\n")


def main() -> int:
    versions = resolved_versions()
    if not versions:
        print("没找到任何家族包的 project.assets.json——先构建一次解决方案。", file=sys.stderr)
        return 1
    packages = [parse_package(pkg, ver) for pkg, ver in sorted(versions.items())]
    doc = {
        "meta": {
            "generated": date.today().isoformat(),
            "source": "本机 NuGet 缓存编译器 XML 文档；版本取自 project.assets.json 实际解析值",
            "script": "scripts/maf_scan.py",
        },
        "packages": packages,
    }
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    JSON_OUT.write_text(json.dumps(doc, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")
    write_md(doc)
    write_graph_html(doc)
    print(f"已写 {JSON_OUT.name} / {MD_OUT.name} / MAF机制层图谱.html\n{stats(doc)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

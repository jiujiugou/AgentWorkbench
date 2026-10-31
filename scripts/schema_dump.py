#!/usr/bin/env python3
"""从真实 SQLite 库导出表结构快照，覆盖 docs/数据库/表结构.md。

为什么要有这个脚本：手写的表结构一定会过期，而且过期时不报错。
库里的 schema 是唯一事实，脚本只是把它读出来。

用法：
    python scripts/schema_dump.py                 # 用默认库，覆盖 docs/数据库/表结构.md
    python scripts/schema_dump.py --db <路径> --out <路径>
    python scripts/schema_dump.py --check         # 只比对，不写文件；有差异时退出码 1
"""

from __future__ import annotations

import argparse
import io
import sqlite3
import sys
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DBS = [
    ROOT / "src/AgentWorkbench.Webapi/data/agentworkbench.db",
    ROOT / "data/agentworkbench.db",
]
DEFAULT_OUT = ROOT / "docs/数据库/表结构.md"

# FTS5 与 sqlite-vec 的内部表：由扩展创建，不手建、不手改。
SHADOW_SUFFIXES = ("_data", "_idx", "_content", "_docsize", "_config", "_info", "_chunks")


def pick_db(explicit: str | None) -> Path:
    if explicit:
        p = Path(explicit)
        if not p.exists():
            sys.exit(f"库文件不存在：{p}")
        return p
    for candidate in DEFAULT_DBS:
        if candidate.exists():
            return candidate
    sys.exit("找不到库文件，请用 --db 指定")


def is_shadow(name: str) -> bool:
    return name.startswith("sqlite_") or name.endswith(SHADOW_SUFFIXES)


def q(conn: sqlite3.Connection, sql: str, args: tuple = ()) -> list[tuple]:
    return conn.execute(sql, args).fetchall()


def columns_of(conn, table: str) -> list[dict]:
    # PRAGMA table_info: cid, name, type, notnull, dflt_value, pk
    return [
        {"name": r[1], "type": r[2] or "", "notnull": bool(r[3]), "default": r[4], "pk": r[5]}
        for r in q(conn, f'PRAGMA table_info("{table}")')
    ]


def indexes_of(conn, table: str) -> list[dict]:
    out = []
    # PRAGMA index_list: seq, name, unique, origin, partial
    for _seq, name, unique, origin, _partial in q(conn, f'PRAGMA index_list("{table}")'):
        cols = [r[2] for r in q(conn, f'PRAGMA index_info("{name}")') if r[2]]
        out.append({"name": name, "unique": bool(unique), "origin": origin, "columns": cols})
    # 索引名排序，保证每次输出稳定
    return sorted(out, key=lambda i: i["name"])


def fks_of(conn, table: str) -> list[dict]:
    # PRAGMA foreign_key_list: id, seq, table, from, to, on_update, on_delete, match
    return [
        {"column": r[3], "ref_table": r[2], "ref_column": r[4], "on_delete": r[6]}
        for r in q(conn, f'PRAGMA foreign_key_list("{table}")')
    ]


def count_rows(conn, table: str) -> int | None:
    try:
        return q(conn, f'SELECT COUNT(*) FROM "{table}"')[0][0]
    except sqlite3.Error:
        return None


def build(conn: sqlite3.Connection, db_path: Path) -> str:
    version = None
    try:
        rows = q(conn, "SELECT MAX(Version) FROM VersionInfo")
        version = rows[0][0] if rows else None
    except sqlite3.Error:
        pass

    masters = q(conn, "SELECT name, sql FROM sqlite_master WHERE type='table' ORDER BY name")
    all_tables = [r[0] for r in masters]
    create_sql = {r[0]: (r[1] or "") for r in masters}
    main_tables = [t for t in all_tables if not is_shadow(t)]
    shadow_tables = [t for t in all_tables if is_shadow(t)]

    # 没装 vec0 扩展的 Python 读不了向量表的列，这不是错误，记下来照实写。
    unreadable: dict[str, str] = {}
    for t in main_tables:
        try:
            columns_of(conn, t)
        except sqlite3.OperationalError as e:
            unreadable[t] = str(e)

    lines: list[str] = []
    add = lines.append

    add("# 数据库表结构")
    add("")
    add("> **本文件由脚本生成，不要手改。** 重建：`python scripts/schema_dump.py`")
    add("> 域 ↔ 表的归属、哪些表没有需求支撑 —— 那是判断，不在本文件，见[表与域.md](表与域.md)。")
    add("")
    add(f"- 生成时间：{datetime.now().strftime('%Y-%m-%d %H:%M')}")
    add(f"- 库文件：`{db_path}`")
    add(f"- 迁移版本：`{version}`（`VersionInfo.MAX(Version)`）")
    add(f"- 表：{len(main_tables)} 张 + 影子表 {len(shadow_tables)} 张")
    add("")

    add("## 表清单")
    add("")
    add("| 表 | 列数 | 行数 | 索引 | 外键 |")
    add("|---|---|---|---|---|")
    for t in main_tables:
        if t in unreadable:
            add(f"| `{t}` | 需扩展 | — | — | — |")
            continue
        cols = columns_of(conn, t)
        idx = indexes_of(conn, t)
        fks = fks_of(conn, t)
        rows = count_rows(conn, t)
        add(f"| `{t}` | {len(cols)} | {'' if rows is None else rows} | {len(idx)} | {len(fks)} |")
    add("")

    add("## 表详情")
    add("")
    for t in main_tables:
        add(f"### `{t}`")
        add("")
        if t in unreadable:
            add(f"读不到列信息：`{t in unreadable and unreadable[t]}`。"
                "该表由扩展创建，用扩展自带的工具查；原建表语句：")
            add("")
            add("```sql")
            add(create_sql.get(t, "").strip())
            add("```")
            add("")
            continue
        cols = columns_of(conn, t)
        add("| 列 | 类型 | 非空 | 默认 | 主键 |")
        add("|---|---|---|---|---|")
        for c in cols:
            add("| `{}` | {} | {} | {} | {} |".format(
                c["name"], c["type"] or "—",
                "是" if c["notnull"] else "",
                "" if c["default"] is None else f"`{c['default']}`",
                "是" if c["pk"] else ""))
        if idx:
            add("")
            add("索引：")
            for i in idx:
                kind = "唯一" if i["unique"] else "普通"
                origin = {"c": "自定义", "u": "唯一约束", "pk": "主键"}.get(i["origin"], i["origin"])
                add(f"- `{i['name']}`（{kind}，{origin}）：{', '.join(i['columns'])}")
        if fks:
            add("")
            add("外键：")
            for f in fks:
                add(f"- `{f['column']}` → `{f['ref_table']}.{f['ref_column']}`"
                    + (f"（ON DELETE {f['on_delete']}）" if f["on_delete"] else ""))
        add("")

    if shadow_tables:
        add("## 影子表")
        add("")
        add("FTS5 与 sqlite-vec 的内部表，由扩展自己维护。**不要手建、不要手改**，删表要连扩展一起删。")
        add("")
        for t in shadow_tables:
            add(f"- `{t}`")
        add("")

    return "\n".join(lines)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", help="SQLite 库路径，默认自动挑一个有数据的库")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="输出文件")
    ap.add_argument("--check", action="store_true", help="只比对，不写；不一致返回 1")
    args = ap.parse_args()

    db_path = pick_db(args.db)
    conn = sqlite3.connect(f"file:{db_path.as_posix()}?mode=ro", uri=True)
    try:
        text = build(conn, db_path)
    finally:
        conn.close()

    out = Path(args.out)
    if args.check:
        old = out.read_text(encoding="utf-8") if out.exists() else ""
        # 生成时间每次都不同，比对时忽略那一行
        strip = lambda s: "\n".join(l for l in s.splitlines() if not l.startswith("- 生成时间"))
        if strip(old) != strip(text):
            print(f"表结构文档已过期：{out}")
            return 1
        print("表结构文档与库一致")
        return 0

    out.parent.mkdir(parents=True, exist_ok=True)
    io.open(out, "w", encoding="utf-8", newline="\n").write(text + "\n")
    print(f"已生成 {out}（{len(text.splitlines())} 行）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

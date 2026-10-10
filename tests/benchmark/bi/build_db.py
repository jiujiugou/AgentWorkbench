#!/usr/bin/env python3
"""DuSQL: 从 db_schema.json + db_content.json 构建 SQLite 数据库。

产出 database/<db_id>.sqlite，供 eval_dusql.py 做执行准确率评测。

用法: python build_db.py [--out database]
"""
import argparse
import json
import sqlite3
import sys
from pathlib import Path

BASE = Path(__file__).resolve().parent
DATA = BASE / "data" / "DuSQL"

# 类型推断：整列可 int -> INTEGER；可 float -> REAL；否则 TEXT。
# 理由：DuSQL 的值以字符串给出（如 "2013" / "88.5" / "700万公司"）。
# 全建 TEXT 会让 WHERE 数值比较退化成字典序，gold 与 pred 的比对失去意义。


def infer_type(values):
    vals = [v for v in values if v is not None and str(v).strip() != ""]
    if not vals:
        return "TEXT"
    if all(_is_int(v) for v in vals):
        return "INTEGER"
    if all(_is_float(v) for v in vals):
        return "REAL"
    return "TEXT"


def _is_int(v):
    try:
        int(str(v).strip())
        return True
    except (ValueError, TypeError):
        return False


def _is_float(v):
    try:
        float(str(v).strip())
        return True
    except (ValueError, TypeError):
        return False


def _coerce(v, sqltype):
    """按推断类型把原始字符串转成 Python 值；失败则退回原字符串。"""
    if v is None:
        return None
    s = v if isinstance(v, str) else str(v)
    if sqltype == "INTEGER":
        try:
            return int(s.strip())
        except (ValueError, AttributeError):
            return s
    if sqltype == "REAL":
        try:
            return float(s.strip())
        except (ValueError, AttributeError):
            return s
    return s


def quote_ident(name):
    return '"' + str(name).replace('"', '""') + '"'


_UNSAFE = '/\\:*?"<>|\r\n\t'


def safe_filename(db_id):
    """db_id 含非法文件名字符（如 '世博会/园博会'），需映射成安全文件名。"""
    s = "".join("_" if ch in _UNSAFE or ord(ch) < 32 else ch for ch in str(db_id))
    s = s.strip().rstrip(".")
    return s or "unnamed"


def build_one(db_id, fname, schema, content, out_dir):
    tables = content.get("tables", {})
    path = out_dir / f"{fname}.sqlite"
    if path.exists():
        path.unlink()
    con = sqlite3.connect(str(path))
    try:
        cur = con.cursor()
        stats = {"tables": 0, "rows": 0, "typed": 0, "total": 0}
        for tname, t in tables.items():
            header = t.get("header") or []
            cells = t.get("cell") or []
            # 列名兜底：空列名/重复列名在 SQL 里无法引用
            cols = []
            seen = {}
            for i, h in enumerate(header):
                name = str(h).strip() if h is not None else ""
                if name == "" or name == "*":
                    name = f"col_{i}"
                if name in seen:
                    seen[name] += 1
                    name = f"{name}_{seen[name]}"
                else:
                    seen[name] = 0
                cols.append(name)
            if not cols:
                continue
            ncols = len(cols)
            rows = []
            for r in cells:
                if not isinstance(r, list):
                    continue
                r = list(r[:ncols]) + [None] * (ncols - len(r))
                rows.append(r)
            types = []
            for j in range(ncols):
                colvals = [r[j] for r in rows]
                types.append(infer_type(colvals))
            stats["typed"] += sum(1 for t_ in types if t_ != "TEXT")
            stats["total"] += ncols
            coldefs = ", ".join(
                f"{quote_ident(c)} {ty}" for c, ty in zip(cols, types)
            )
            cur.execute(f"CREATE TABLE {quote_ident(tname)} ({coldefs})")
            for r in rows:
                vals = [_coerce(v, ty) for v, ty in zip(r, types)]
                cur.execute(
                    f"INSERT INTO {quote_ident(tname)} VALUES "
                    f"({', '.join('?' * ncols)})",
                    vals,
                )
            stats["tables"] += 1
            stats["rows"] += len(rows)
        con.commit()
    finally:
        con.close()
    return stats


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(BASE / "database"))
    args = ap.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    raw_schemas = json.load(open(DATA / "db_schema.json", encoding="utf-8"))
    raw_contents = json.load(open(DATA / "db_content.json", encoding="utf-8"))
    schemas = {s["db_id"]: s for s in raw_schemas}
    contents = {c["db_id"]: c for c in raw_contents}

    dup_s = len(raw_schemas) - len(schemas)
    dup_c = len(raw_contents) - len(contents)
    print(f"schema dbs : {len(schemas)} (raw {len(raw_schemas)}, dup {dup_s})")
    print(f"content dbs: {len(contents)} (raw {len(raw_contents)}, dup {dup_c})")

    missing_content = [d for d in schemas if d not in contents]
    missing_schema = [d for d in contents if d not in schemas]
    if missing_schema:
        print(f"[warn] 有 content 无 schema: {len(missing_schema)} -> {missing_schema[:5]}")
    if missing_content:
        print(f"[warn] 有 schema 无 content: {len(missing_content)} -> {missing_content[:5]}")

    tot = {"tables": 0, "rows": 0, "typed": 0, "total": 0}
    empty = []
    db_map = {}
    for db_id, content in contents.items():
        fname = safe_filename(db_id)
        if fname in db_map.values():
            fname = f"{fname}_{abs(hash(db_id)) % 10000}"
        db_map[db_id] = fname
        st = build_one(db_id, fname, schemas.get(db_id), content, out_dir)
        for k in tot:
            tot[k] += st[k]
        if st["tables"] == 0:
            empty.append(db_id)

    json.dump(
        db_map, open(out_dir / "db_map.json", "w", encoding="utf-8"),
        ensure_ascii=False, indent=1,
    )

    print(f"built dbs  : {len(contents)}")
    print(f"tables     : {tot['tables']}")
    print(f"rows       : {tot['rows']}")
    print(
        f"numeric cols: {tot['typed']}/{tot['total']} "
        f"({tot['typed'] / tot['total'] * 100:.1f}%)"
        if tot["total"] else "numeric cols: n/a"
    )
    if empty:
        print(f"[warn] 空库: {len(empty)} -> {empty[:5]}")
    print(f"out dir    : {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

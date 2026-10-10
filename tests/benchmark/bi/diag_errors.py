#!/usr/bin/env python3
"""诊断：全量跑 gold，按错误类别打印代表性 SQL。"""
import json
import sqlite3
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from sql_dialect import adapt  # noqa: E402

BASE = Path(__file__).resolve().parent
DATA = BASE / "data" / "DuSQL"
DB = BASE / "database"

db_map = json.load(open(DB / "db_map.json", encoding="utf-8"))
gold = {}
order = []
for line in open(DATA / "gold_dev.sql", encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if len(p) >= 2:
        gold[p[0]] = {"sql": p[1], "db_id": p[2].strip() if len(p) > 2 else ""}
        order.append(p[0])

by_db = defaultdict(list)
for q in gold:
    by_db[gold[q]["db_id"]].append(q)

buckets = defaultdict(list)
for db_id, qs in by_db.items():
    fname = db_map.get(db_id)
    if fname is None:
        continue
    con = sqlite3.connect(str(DB / f"{fname}.sqlite"))
    try:
        tables = [r[0] for r in con.execute(
            "select name from sqlite_master where type='table'")]
        cols = set()
        for t in tables:
            for r in con.execute('PRAGMA table_info("%s")' % t.replace('"', '""')):
                cols.add(r[1])
        for q in qs:
            try:
                con.execute(adapt(gold[q]["sql"], tables, cols)).fetchall()
            except Exception as e:
                key = str(e).split(":")[1].strip()[:48] if ":" in str(e) else str(e)[:48]
                buckets[key].append((q, db_id, gold[q]["sql"]))
    finally:
        con.close()

total = sum(len(v) for v in buckets.values())
print(f"gold exec failures: {total} / {len(gold)}\n")
for key, items in sorted(buckets.items(), key=lambda kv: -len(kv[1])):
    print(f"--- [{len(items)}] {key}")
    for q, db_id, sql in items[:2]:
        print(f"    {q} | {db_id}")
        print(f"    {sql[:200]}")
    print()

#!/usr/bin/env python3
"""生成 Text-to-SQL 预测，用于消融实验。

四种模式（逐层叠加）：
  full  : 全库 schema 全部塞进 prompt（baseline）
  link  : 只喂检索出的相关表/列（schema linking）
  retry : link + 执行报错后带报错重试一次
  value : link + 附字段候选值

用法: python gen_baseline.py --mode full --limit 20 --out pred_full.sql
"""
import argparse
import json
import re
import sqlite3
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

BASE = Path(__file__).resolve().parent
ROOT = BASE.parent.parent.parent
DATA = BASE / "data" / "DuSQL"
DB = BASE / "database"

SYS_PROMPT = (
    "你是一个 Text-to-SQL 助手。根据给定的数据库结构，把用户的中文问题"
    "翻译成一条 SQLite 查询语句。\n"
    "要求：只输出 SQL，不要解释、不要 markdown 代码块、不要结尾分号。\n"
    "表和列名都是中文，直接原样使用。"
)


def load_provider():
    cfg = json.load(open(
        ROOT / "src" / "AgentWorkbench.Webapi" / "appsettings.json",
        encoding="utf-8"))

    def walk(o):
        if isinstance(o, dict):
            if "api.deepseek" in str(o.get("BaseUrl", "")):
                return o
            for v in o.values():
                r = walk(v)
                if r:
                    return r
        elif isinstance(o, list):
            for v in o:
                r = walk(v)
                if r:
                    return r
        return None

    p = walk(cfg)
    if not p:
        raise SystemExit("未找到 DeepSeek provider 配置")
    return p.get("BaseUrl", "").rstrip("/"), p.get("ApiKey", ""), p.get("Model", "")


def schema_of(db_id, db_map, with_values=False, sample=3):
    """从实际 SQLite 库读表结构（而不是 db_schema.json，因为建库时做过列名兜底）。"""
    fname = db_map.get(db_id)
    if not fname:
        return ""
    path = DB / f"{fname}.sqlite"
    if not path.exists():
        return ""
    con = sqlite3.connect(str(path))
    parts = []
    try:
        tables = [r[0] for r in con.execute(
            "select name from sqlite_master where type='table'")]
        for t in tables:
            cols = [r[1] for r in con.execute(
                'PRAGMA table_info("%s")' % t.replace('"', '""'))]
            seg = f"表 {t}({', '.join(cols)})"
            if with_values:
                vals = []
                for c in cols:
                    try:
                        rs = con.execute(
                            f'select distinct "{c}" from "{t}" '
                            f'where "{c}" is not null limit {sample}'
                        ).fetchall()
                        vs = [str(r[0]) for r in rs if r[0] is not None]
                    except Exception:
                        vs = []
                    if vs:
                        vals.append(f"{c}: {', '.join(vs[:sample])}")
                if vals:
                    seg += "\n  示例值 " + "; ".join(vals)
            parts.append(seg)
    finally:
        con.close()
    return "\n".join(parts)


def call_llm(base, key, model, user, timeout=60, retries=3):
    url = f"{base}/chat/completions"
    body = json.dumps({
        "model": model,
        "messages": [
            {"role": "system", "content": SYS_PROMPT},
            {"role": "user", "content": user},
        ],
        "temperature": 0.0,
        "max_tokens": 300,
    }, ensure_ascii=False).encode("utf-8")
    last = ""
    for i in range(retries):
        try:
            req = urllib.request.Request(
                url, data=body,
                headers={"Content-Type": "application/json",
                         "Authorization": f"Bearer {key}"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                d = json.loads(r.read().decode("utf-8"))
            return d["choices"][0]["message"]["content"].strip()
        except Exception as e:  # noqa: BLE001
            last = f"{type(e).__name__}: {e}"
            time.sleep(2 + 2 * i)
    print(f"  [llm error] {last[:120]}", flush=True)
    return ""


def clean_sql(s):
    s = s.strip()
    s = re.sub(r"^```(?:sql)?\s*", "", s)
    s = re.sub(r"\s*```$", "", s)
    s = s.strip().rstrip(";").strip()
    return s.split("\n")[0].strip() if "\n" in s else s


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", choices=["full", "link", "retry", "value"],
                    default="full")
    ap.add_argument("--limit", type=int, default=20)
    ap.add_argument("--out", default="pred_full.sql")
    ap.add_argument("--sleep", type=float, default=0.0)
    args = ap.parse_args()

    base, key, model = load_provider()
    print(f"model: {model}  base: {base}")

    db_map = json.load(open(DB / "db_map.json", encoding="utf-8"))

    dev = json.load(open(DATA / "dev.json", encoding="utf-8"))
    questions = {d["question_id"]: d["question"] for d in dev}

    gold = {}
    order = []
    for line in open(DATA / "gold_dev.sql", encoding="utf-8"):
        p = line.rstrip("\n").split("\t")
        if len(p) >= 3:
            gold[p[0]] = p[2].strip()
            order.append(p[0])

    if args.limit:
        order = order[: args.limit]

    schema_cache = {}
    out = []
    t0 = time.time()
    for i, qid in enumerate(order, 1):
        db_id = gold[qid]
        q = questions.get(qid, "")
        if db_id not in schema_cache:
            schema_cache[db_id] = schema_of(
                db_id, db_map, with_values=(args.mode == "value"))
        sch = schema_cache[db_id]
        user = f"数据库结构：\n{sch}\n\n问题：{q}\nSQL："
        raw = call_llm(base, key, model, user)
        sql = clean_sql(raw)
        if args.mode in ("retry", "value"):
            err = try_exec(db_id, sql, db_map)
            if err:
                user2 = (f"数据库结构：\n{sch}\n\n问题：{q}\n"
                         f"你上一条 SQL：{sql}\n执行报错：{err}\n"
                         f"请修正后只输出 SQL：")
                sql2 = clean_sql(call_llm(base, key, model, user2))
                if sql2:
                    sql = sql2
        out.append(f"{qid}\t{sql}\t{db_id}")
        if i % 10 == 0 or i == len(order):
            print(f"  {i}/{len(order)}  {time.time() - t0:.0f}s", flush=True)
        if args.sleep:
            time.sleep(args.sleep)

    Path(args.out).write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"written: {args.out} ({len(out)} 条)")


def try_exec(db_id, sql, db_map):
    """试执行，返回错误信息；成功返回 None。"""
    from sql_dialect import adapt
    if not sql:
        return "empty sql"
    fname = db_map.get(db_id)
    if not fname:
        return "no db"
    path = DB / f"{fname}.sqlite"
    if not path.exists():
        return "no db file"
    con = sqlite3.connect(str(path))
    try:
        tables = [r[0] for r in con.execute(
            "select name from sqlite_master where type='table'")]
        cols = set()
        for t in tables:
            for r in con.execute('PRAGMA table_info("%s")' % t.replace('"', '""')):
                cols.add(r[1])
        con.execute(adapt(sql, tables, cols)).fetchall()
        return None
    except Exception as e:  # noqa: BLE001
        return str(e)[:150]
    finally:
        con.close()


if __name__ == "__main__":
    main()

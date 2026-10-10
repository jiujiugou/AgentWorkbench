#!/usr/bin/env python3
"""DuSQL 执行准确率（Execution Accuracy）评测。

指标定义：对每条样本，在真实 SQLite 库上分别执行 gold SQL 与 pred SQL，
比较二者结果集；一致则该样本命中。这是 Text-to-SQL 学术界的通行指标，
可复算、不依赖任何 LLM 判分。

用法:
    # 自检：把 gold 当 pred 喂进去，准确率应接近 100%
    python eval_dusql.py --pred data/DuSQL/gold_dev.sql

    python eval_dusql.py --pred predictions.sql --limit 200
"""
import argparse
import json
import sqlite3
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

from sql_dialect import adapt

BASE = Path(__file__).resolve().parent
DEFAULT_GOLD = BASE / "data" / "DuSQL" / "gold_dev.sql"
DEFAULT_DB = BASE / "database"
DEFAULT_REPORT = BASE / "report.json"


def load_sql_tsv(path):
    """解析 qid \\t sql \\t db_id （DuSQL gold_dev.sql 格式）。"""
    rows = {}
    order = []
    with open(path, encoding="utf-8") as f:
        for ln, line in enumerate(f, 1):
            line = line.rstrip("\n")
            if not line.strip():
                continue
            parts = line.split("\t")
            if len(parts) < 2:
                continue
            qid = parts[0].strip()
            sql = parts[1]
            db_id = parts[2].strip() if len(parts) > 2 else ""
            if qid in rows:
                continue
            rows[qid] = {"sql": sql, "db_id": db_id}
            order.append(qid)
    return rows, order


def norm_value(v):
    if v is None:
        return None
    if isinstance(v, bool):
        return int(v)
    if isinstance(v, (int, float)):
        return round(float(v), 6)
    s = str(v).strip()
    try:
        return round(float(s), 6)
    except ValueError:
        return s


def norm_rows(rows):
    return [tuple(norm_value(v) for v in r) for r in rows]


def result_equal(a, b, match):
    """比较结果集。multiset 保留重复行，set 去重（Spider 官方口径）。"""
    if a is None or b is None:
        return False
    if len(a) != len(b):
        if match == "multiset":
            return False
    na, nb = norm_rows(a), norm_rows(b)
    if any(len(r) != len(na[0]) for r in na) if na else False:
        pass
    if na and nb and len(na[0]) != len(nb[0]):
        return False
    if match == "multiset":
        return Counter(na) == Counter(nb)
    return set(na) == set(nb)


def db_identifiers(con):
    """库内真实的表名/列名，供数字开头标识符加引号时判定。"""
    tables = [r[0] for r in con.execute(
        "select name from sqlite_master where type='table'")]
    cols = set()
    for t in tables:
        for r in con.execute('PRAGMA table_info("%s")' % t.replace('"', '""')):
            cols.add(r[1])
    return tables, cols


def run_sql(con, sql, idents=None, timeout=10.0):
    """执行前统一做 SQLite 方言适配（gold 与 pred 同规则，保持可比）。"""
    tables, cols = idents if idents else ((), ())
    try:
        cur = con.cursor()
        cur.execute("PRAGMA query_only = ON;")
        cur.execute(adapt(sql, tables, cols))
        try:
            rows = cur.fetchall()
        except sqlite3.ProgrammingError:
            rows = []
        return rows, None
    except Exception as e:  # noqa: BLE001 - 需吞掉任意 SQL 错误以继续评测
        try:
            con.rollback()
        except Exception:
            pass
        return None, f"{type(e).__name__}: {e}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gold", default=str(DEFAULT_GOLD))
    ap.add_argument("--pred", required=True)
    ap.add_argument("--db", default=str(DEFAULT_DB))
    ap.add_argument("--limit", type=int, default=0, help="只评测前 N 条（0=全部）")
    ap.add_argument("--match", choices=["multiset", "set"], default="multiset")
    ap.add_argument("--report", default=str(DEFAULT_REPORT))
    ap.add_argument("--dump-errors", type=int, default=10)
    args = ap.parse_args()

    db_dir = Path(args.db)
    db_map = json.load(open(db_dir / "db_map.json", encoding="utf-8"))
    gold, gorder = load_sql_tsv(args.gold)
    pred, _ = load_sql_tsv(args.pred)

    qids = [q for q in gorder if q in pred]
    if args.limit:
        qids = qids[: args.limit]
    missing_pred = [q for q in gorder if q not in pred]

    # 按 db 分组，一个库只开一次连接
    by_db = {}
    for q in qids:
        by_db.setdefault(gold[q]["db_id"], []).append(q)

    stats = Counter()
    errors = []
    t0 = time.time()

    for db_id, qs in by_db.items():
        fname = db_map.get(db_id)
        if fname is None:
            stats["db_missing"] += len(qs)
            for q in qs:
                errors.append({"qid": q, "kind": "db_missing", "db_id": db_id})
            continue
        path = db_dir / f"{fname}.sqlite"
        if not path.exists():
            stats["db_missing"] += len(qs)
            continue
        con = sqlite3.connect(str(path), timeout=10.0)
        try:
            idents = db_identifiers(con)
            for q in qs:
                g_res, g_err = run_sql(con, gold[q]["sql"], idents)
                if g_err is not None:
                    stats["gold_exec_error"] += 1
                    if len(errors) < args.dump_errors:
                        errors.append(
                            {"qid": q, "kind": "gold_exec_error",
                             "db_id": db_id, "detail": g_err[:200]}
                        )
                    continue
                p_res, p_err = run_sql(con, pred[q]["sql"], idents)
                if p_err is not None:
                    stats["pred_exec_error"] += 1
                    if len(errors) < args.dump_errors:
                        errors.append(
                            {"qid": q, "kind": "pred_exec_error",
                             "db_id": db_id, "detail": p_err[:200]}
                        )
                    continue
                stats["executed"] += 1
                # gold 结果为空的样本对恒空预测不设防，需单列口径
                if len(g_res) == 0:
                    stats["gold_empty"] += 1
                else:
                    stats["gold_nonempty"] += 1
                hit = result_equal(g_res, p_res, args.match)
                if hit:
                    stats["hit"] += 1
                    if len(g_res) != 0:
                        stats["hit_nonempty"] += 1
                else:
                    stats["mismatch"] += 1
                    if len(errors) < args.dump_errors:
                        errors.append(
                            {"qid": q, "kind": "mismatch", "db_id": db_id,
                             "gold_rows": len(g_res), "pred_rows": len(p_res)}
                        )
        finally:
            con.close()

    executed = stats["executed"]
    acc = stats["hit"] / executed if executed else 0.0
    nonempty = stats["gold_nonempty"]
    acc_ne = stats["hit_nonempty"] / nonempty if nonempty else 0.0

    print(f"gold file   : {args.gold}")
    print(f"pred file   : {args.pred}")
    print(f"gold total  : {len(gorder)}")
    print(f"evaluated   : {len(qids)}")
    print(f"executed    : {executed}")
    print(f"  hit       : {stats['hit']}")
    print(f"  mismatch  : {stats['mismatch']}")
    print(f"  gold exec error : {stats['gold_exec_error']}")
    print(f"  pred exec error : {stats['pred_exec_error']}")
    print(f"  db missing      : {stats['db_missing']}")
    if missing_pred:
        print(f"  pred missing    : {len(missing_pred)}")
    print(f"match mode  : {args.match}")
    print(f"gold 空结果 : {stats['gold_empty']} "
          f"({stats['gold_empty'] / max(executed, 1) * 100:.1f}%)")
    print(f"Execution Accuracy          : {acc:.4f}  ({stats['hit']}/{executed})")
    print(f"Execution Accuracy(非空gold) : {acc_ne:.4f}  "
          f"({stats['hit_nonempty']}/{nonempty})")

    report = {
        "dataset": "DuSQL",
        "metric": "execution_accuracy",
        "match_mode": args.match,
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "gold_file": str(args.gold),
        "pred_file": str(args.pred),
        "gold_total": len(gorder),
        "evaluated": len(qids),
        "executed": executed,
        "hit": stats["hit"],
        "mismatch": stats["mismatch"],
        "gold_empty": stats["gold_empty"],
        "gold_nonempty": stats["gold_nonempty"],
        "hit_nonempty": stats["hit_nonempty"],
        "execution_accuracy_nonempty": round(acc_ne, 4),
        "gold_exec_error": stats["gold_exec_error"],
        "pred_exec_error": stats["pred_exec_error"],
        "db_missing": stats["db_missing"],
        "pred_missing": len(missing_pred),
        "execution_accuracy": round(acc, 4),
        "elapsed_sec": round(time.time() - t0, 2),
        "errors_sample": errors,
    }
    Path(args.report).parent.mkdir(parents=True, exist_ok=True)
    json.dump(report, open(args.report, "w", encoding="utf-8"),
              ensure_ascii=False, indent=2)
    print(f"report      : {args.report}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""DuSQL 评测链路验收（判定型：正例绿 + 负控红）。

两条断言缺一不可：
  1. 正例（oracle）：gold 当 pred 喂进去，执行准确率必须 = 1.0
     -> 证明建库 + 方言适配 + 结果比对这条链是自洽的。
  2. 负控（negctl）：恒空查询当 pred，准确率必须 <= 5%
     -> 证明评测能"变红"。若负控也绿，说明比对恒真，整套指标是假的。

用法: python acceptance_bi.py
"""
import json
import sqlite3
import subprocess
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

BASE = Path(__file__).resolve().parent
DATA = BASE / "data" / "DuSQL"
DB = BASE / "database"
PY = sys.executable

GOLD = DATA / "gold_dev.sql"
NEGCTL = BASE / "pred_negctl.sql"
ORACLE_REPORT = BASE / "report_oracle.json"
NEGCTL_REPORT = BASE / "report_negctl.json"

MAX_GOLD_ERR_RATE = 0.02   # gold 自身不可执行的比例上限（数据集标注噪声）
MAX_NEGCTL_ACC = 0.05      # 负控准确率上限


def build_negctl():
    """为每条样本生成一条恒空查询：select * from <该库首表> where 1 = 0。"""
    db_map = json.load(open(DB / "db_map.json", encoding="utf-8"))
    first_table = {}
    for db_id, fname in db_map.items():
        p = DB / f"{fname}.sqlite"
        if not p.exists():
            continue
        con = sqlite3.connect(str(p))
        try:
            r = con.execute(
                "select name from sqlite_master where type='table' limit 1"
            ).fetchone()
            if r:
                first_table[db_id] = r[0]
        finally:
            con.close()

    n = 0
    missing = 0
    with open(GOLD, encoding="utf-8") as f, \
            open(NEGCTL, "w", encoding="utf-8") as out:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 2:
                continue
            qid, db_id = parts[0].strip(), parts[2].strip() if len(parts) > 2 else ""
            t = first_table.get(db_id)
            if not t:
                missing += 1
                continue
            out.write(f'{qid}\tselect * from "{t}" where 1 = 0\t{db_id}\n')
            n += 1
    print(f"negctl pred : {n} 条 (无表 {missing})")
    return n


def run_eval(pred, report):
    cmd = [PY, str(BASE / "eval_dusql.py"), "--pred", str(pred),
           "--report", str(report)]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        print(r.stdout, r.stderr)
        raise SystemExit(f"eval failed: {pred}")
    return json.load(open(report, encoding="utf-8"))


def main():
    print("=== 1/3 构建负控预测 ===")
    build_negctl()

    print("\n=== 2/3 正例 oracle（gold as pred）===")
    o = run_eval(GOLD, ORACLE_REPORT)
    o_err_rate = o["gold_exec_error"] / max(o["evaluated"], 1)
    print(f"  executed {o['executed']}/{o['evaluated']}  "
          f"acc {o['execution_accuracy']}  gold_err_rate {o_err_rate:.4f}")

    print("\n=== 3/3 负控 negctl（恒空查询）===")
    g = run_eval(NEGCTL, NEGCTL_REPORT)
    empty_rate = g["gold_empty"] / max(g["executed"], 1)
    print(f"  executed {g['executed']}/{g['evaluated']}  "
          f"acc {g['execution_accuracy']}")
    print(f"  gold 空结果 {g['gold_empty']} ({empty_rate * 100:.1f}%) "
          f"-> 恒空预测白拿的分")
    print(f"  acc(非空gold) {g['execution_accuracy_nonempty']}")

    checks = [
        ("oracle 准确率 = 1.0", o["execution_accuracy"] == 1.0,
         f"{o['execution_accuracy']}"),
        ("oracle 非空口径准确率 = 1.0",
         o["execution_accuracy_nonempty"] == 1.0,
         f"{o['execution_accuracy_nonempty']}"),
        ("oracle 覆盖率 >= 95%", o["executed"] / max(o["evaluated"], 1) >= 0.95,
         f"{o['executed']}/{o['evaluated']}"),
        ("gold 不可执行率 <= 2%", o_err_rate <= MAX_GOLD_ERR_RATE,
         f"{o_err_rate:.4f}"),
        ("负控非空口径准确率 <= 5%（必须变红）",
         g["execution_accuracy_nonempty"] <= MAX_NEGCTL_ACC,
         f"{g['execution_accuracy_nonempty']}"),
    ]

    print("\n=== 判定 ===")
    ok = True
    for name, passed, detail in checks:
        print(f"  [{'PASS' if passed else 'FAIL'}] {name}  ({detail})")
        ok = ok and passed
    print(f"\n{'ALL PASS' if ok else 'FAILED'}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())

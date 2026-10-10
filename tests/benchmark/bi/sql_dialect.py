#!/usr/bin/env python3
"""DuSQL gold SQL -> SQLite 可执行 SQL 的方言适配。

DuSQL 的 gold SQL 面向 MySQL 方言书写，直接在 SQLite 上执行失败率约 90%。
本模块做纯语法层适配，对 gold 与 pred 施加同一套规则，因此不改变二者可比性。

适配项（按影响条数排序，dev 集 2482 条实测）：
  1. TIME_NOW          —— DuSQL 自定义函数，映射为当前年份（100 条）
  2. 复合查询包装      —— SQLite 不允许 `(` 包裹、也不允许分支内 order by/limit（72 条）
  3. 标识符引号        —— 数字开头或含括号的列名/表名（22 条）
  4. 别名归一          —— 起别名后仍用原表名限定列（<MySQL 允许，SQLite 不允许>）
  5. limit 缺空格      —— 数据集笔误 `limit3`（2 条）

不修（视为数据集标注错误，评测时排除）：
  - 子查询 `from T1`（应为原表名）
  - 引用未 join 的表、子查询未给聚合列起别名
"""
import re

# 字符串字面量（含 '' / "" 转义）：改写前先摘出，避免误伤 where 条件里的值
_STR = re.compile(r"'(?:[^']|'')*'|\"(?:[^\"]|\"\")*\"")

_ALIAS = re.compile(
    r"(?i)\b(?:from|join)\s+"
    r"([^\s,();]+)"                      # 表名（中文或英文，裸写）
    r"\s+(?:as\s+)?"                     # 可选 as
    r"([A-Za-z_][A-Za-z0-9_]*)"          # 别名（DuSQL 用 T1/T2/a/b/...）
    r"(?=\s*(?:,|\)|$|\b(?:on|where|join|inner|left|right|full|cross|"
    r"group|order|limit|having|union|intersect|except|as)\b))"
)

_NOT_ALIAS = {
    "on", "where", "join", "inner", "left", "right", "full", "cross",
    "group", "order", "by", "limit", "having", "union", "intersect",
    "except", "and", "or", "not", "in", "like", "as", "select", "from",
    "desc", "asc", "set", "using", "natural", "outer",
}

_COMPOUND_OP = r"(?:union\s+all|union|except|intersect)"

# 无需引号的安全标识符：字母/下划线/中文开头，且只含字母数字下划线中文
_SAFE_IDENT = re.compile(r"^[^\W\d]\w*$", re.UNICODE)


def _stash_strings(sql):
    stash = []

    def repl(m):
        stash.append(m.group(0))
        return "\x00%d\x00" % (len(stash) - 1)

    return _STR.sub(repl, sql), stash


def _restore_strings(sql, stash):
    def repl(m):
        i = int(m.group(1))
        return stash[i] if 0 <= i < len(stash) else m.group(0)

    return re.sub(r"\x00(\d+)\x00", repl, sql)


def alias_mapping(sql):
    """抽取 {原表名: 别名}。同一表在句中只出现一次，映射安全。"""
    s, _ = _stash_strings(sql)
    mapping = {}
    for m in _ALIAS.finditer(s):
        table, alias = m.group(1), m.group(2)
        if alias.lower() in _NOT_ALIAS:
            continue
        mapping.setdefault(table, alias)
    return mapping


def _alias_rewrite(s):
    """对已摘除字符串字面量的 SQL，把 `原表名.` 重写为 `别名.`。"""
    for table, alias in sorted(alias_mapping(s).items(), key=lambda kv: -len(kv[0])):
        if alias == table:
            continue
        s = re.sub(
            r"(?<![\w.\u4e00-\u9fff])" + re.escape(table) + r"\s*\.\s*",
            alias + ".",
            s,
        )
    return s


def _fix_compound(s):
    """SQLite 不支持 `( sel ) OP ( sel )` 且不支持分支内 order by/limit。
    改写为 `select * from ( sel ) OP select * from ( sel )`。

    注意形态是两段式 `(A) OP (B)`，整体并没有被一对括号包住。
    """
    if not re.search(r"(?i)\b" + _COMPOUND_OP + r"\b", s):
        return s
    if not re.match(
        r"^\s*\(.*?\)\s*(?:" + _COMPOUND_OP + r")\s*\(.*\)\s*$", s, re.S | re.I
    ):
        return s
    s = re.sub(r"\)\s*(" + _COMPOUND_OP + r")\s*\(", r") \1 select * from (", s,
               flags=re.IGNORECASE)
    return re.sub(r"^\s*\(", "select * from (", s)


def _fix_time_now(s):
    """TIME_NOW 是 DuSQL 自定义函数（当前时间），SQLite 无此函数。
    gold 中一律与时间字段做年差运算，故映射为当前年份。"""
    return re.sub(
        r"(?i)\bTIME_NOW\b",
        "(CAST(strftime('%Y','now') AS INTEGER))",
        s,
    )


def _fix_limit(s):
    """数据集笔误：`limit3` 缺空格。"""
    return re.sub(r"(?i)\blimit\s*(\d+)", r"limit \1", s)


def _quote_idents(s, tables, columns):
    """数字开头或含括号等特殊字符的标识符，SQLite 无法裸解析。
    用库内真实的表名/列名集合判定，避免误伤数字字面量。"""
    known = set(tables) | set(columns)
    if not known:
        return s
    need = [t for t in known if not _SAFE_IDENT.match(t)]
    for tok in sorted(need, key=len, reverse=True):
        s = re.sub(
            r"(?<![\w\"'`])" + re.escape(tok) + r"(?![\w])",
            '"%s"' % tok,
            s,
        )
    return s


def adapt(sql, tables=(), columns=()):
    """完整适配入口。"""
    if not sql:
        return sql
    s, stash = _stash_strings(sql)
    s = _fix_compound(s)
    s = _alias_rewrite(s)
    s = _fix_time_now(s)
    s = _fix_limit(s)
    s = _quote_idents(s, tables, columns)
    return _restore_strings(s, stash)


def to_sqlite(sql):
    """仅做别名归一（不需要 schema 信息的轻量版）。"""
    s, stash = _stash_strings(sql)
    s = _alias_rewrite(s)
    return _restore_strings(s, stash)


if __name__ == "__main__":
    import sys

    sys.stdout.reconfigure(encoding="utf-8")
    cases = [
        "select T2.中文名 , T2.年龄 , T1.赛季 from 比赛记录 as T1 "
        "join 篮球运动员 as T2 on 比赛记录.球员id == 篮球运动员.词条id "
        "where T1.出场次数 <= 40",
        "select 名称 from 高校 where 词条id not in ( select 高校id from 奖项 )",
        "select 中文名 from 篮球运动员 as T1 where T1.名称 = '比赛记录.球员id'",
        "select max ( T2.市场份额 ) from 洗衣机品牌门店 as T1 join 洗衣机品牌 "
        "as T2 on 洗衣机品牌门店.品牌id == 洗衣机品牌.词条id "
        "where T2.2018年营业额 < 200700000000",
        "( select 城市id from 一带一路的中国城市 ) except ( select 贫困城市id from 对口帮扶城市 )",
        "select 名称 , 所属洲 from 国家 where 词条id not in "
        "( select 国家id from 2008北京奥运会奖牌榜 )",
        "select 名称 from 洗衣机品牌 where TIME_NOW - 成立时间 != 18",
        "select 省份 from 各省财政收入 where GDP总计(亿) == "
        "( select avg ( GDP总计(亿) ) from 各省财政收入 )",
        "( select 名称 from 参考书 order by 价格 asc limit 3 ) union "
        "( select 名称 from 参考书 order by 价格 desc limit 5 )",
        "select T2.节目名称 from 收视率 as T1 join 综艺节目 as T2 on "
        "收视率.节目id == 综艺节目.词条id group by T1.节目id order by count ( * ) desc limit3",
    ]
    cols = {"2018年营业额", "市场份额", "城市id", "贫困城市id", "名称",
            "所属洲", "成立时间", "GDP总计(亿)", "价格"}
    tables = {"2008北京奥运会奖牌榜", "一带一路的中国城市", "对口帮扶城市",
              "国家", "各省财政收入", "参考书"}
    for c in cases:
        print("IN :", c)
        print("OUT:", adapt(c, tables, cols))
        print()

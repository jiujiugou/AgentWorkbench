# DuSQL 智能问数评测（Text-to-SQL）

中文跨域 Text-to-SQL 基准，指标为 **Execution Accuracy**（执行结果一致率）。
本地可复算，不依赖任何 LLM 判分，也不需要联网。

数据集：DuSQL（百度，EMNLP 2020）—— 200 个库 / 813 张表 / 23,797 对，
覆盖 164 个领域，含匹配、计算、推理等真实问数形式。

## 快速开始

```bash
# 1. 下载数据集（2.7 MB）
curl -L -o data/DuSQL.zip https://dataset-bj.cdn.bcebos.com/qianyan/DuSQL.zip
python -c "import zipfile; zipfile.ZipFile('data/DuSQL.zip').extractall('data')"

# 2. 建库（208 个库 -> 201 个 SQLite 文件）
python build_db.py

# 3. 验收（正例绿 + 负控红）
python acceptance_bi.py
```

## 文件

| 文件 | 作用 |
|------|------|
| `build_db.py` | `db_schema.json` + `db_content.json` -> `database/<db_id>.sqlite` |
| `sql_dialect.py` | DuSQL(MySQL 方言) -> SQLite 适配层 |
| `eval_dusql.py` | 执行准确率评测，输出 `report.json` |
| `acceptance_bi.py` | 判定型验收：oracle 必须绿、负控必须红 |
| `diag_errors.py` | gold 执行失败分类诊断 |

## 为什么需要 `sql_dialect.py`

DuSQL 的 gold SQL 是 MySQL 方言，**直接在 SQLite 上执行失败率 90%**。适配项按影响条数排列：

| 问题 | 样例 | 条数 |
|------|------|------|
| `TIME_NOW` 自定义函数 | `where TIME_NOW - 成立时间 != 18` | 100 |
| 复合查询不允许括号包裹、分支内不允许 order by/limit | `( select ... ) union ( select ... )` | 72 |
| 数字开头/含括号的标识符裸写 | `T2.2018年营业额`、`GDP总计(亿)` | 22 |
| 起别名后仍用原表名限定列 | `from 比赛记录 as T1 ... on 比赛记录.球员id` | 大量 |
| `limit3` 缺空格（数据集笔误） | `order by count(*) desc limit3` | 2 |

适配对 gold 与 pred 施加同一套规则，不改变二者可比性。

## 当前基线（dev 集 2482 条）

| 项 | 值 |
|----|----|
| 可执行样本 | 2441 / 2482（98.35%） |
| **oracle（gold 当 pred）** | **1.0000** |
| 负控（恒空查询） | 0.2819 |
| 负控（剔除空 gold 后） | **0.0000** |
| gold 结果为空的样本 | 688（28.2%） |

不可执行的 41 条是**数据集标注错误**，非适配问题：子查询 `from T1`（应为原表名）28 条、
引用未 join 的表 4 条、子查询聚合列未起别名 9 条。

### 必须同时报告两个口径

**28.2% 的 gold 查询本身返回空结果**，因此一条恒空 SQL 能白拿 28% 的分数。
只报 `execution_accuracy` 会让"什么都查不出来"的模型显得有 28% 的能力。
故报告一律同时给出 `execution_accuracy_nonempty`（仅在 gold 结果非空的样本上计算）。

## 评测输出

```bash
python eval_dusql.py --pred <预测文件> --match multiset
```

预测文件格式与 `data/DuSQL/gold_dev.sql` 一致，TSV 三列：

```
<question_id>\t<SQL>\t<db_id>
```

`--match` 可选 `multiset`（默认，保留重复行，更严格）或 `set`（Spider 官方口径，去重）。

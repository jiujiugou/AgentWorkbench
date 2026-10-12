# 评测：<能力>（<里程碑>）

- 脚本：`scripts/eval-<milestone>.ps1`
- 金标集：`tests/benchmark/<milestone>/golden/`
- 负控集：`tests/benchmark/<milestone>/negative/`
- 日期：<YYYY-MM-DD>

## 金标集定义

| 样本 | 输入 | 金标 | 说明 |
| --- | --- | --- | --- |
| <id> | `<文件>` | `elements.json` / `findings.json` | <...> |

## 指标与阈值

> 判定方式默认**程序计算**（对照金标）；仅"无金标的开放问答质量"可用 LLM-as-judge，且**可选、非门禁**。

| 指标 | 定义 | 阈值 | 基线 | 本次 | 结论 |
| --- | --- | --- | --- | --- | --- |
| 抽取 F1 | <按字段/整体> | ≥ 0.80 | | | |
| 证据绑定率 | 有值结论中证据齐全比例 | = 100% | | | |
| 误报率（无关文档） | 高危且无证据清单数 | = 0 | | | |
| 非法输入拒绝率 | N2/N3/N4 正确拒绝比例 | = 100% | | | |
| 成本 / P95 延迟 | <口径> | ≤ 预算 | | | |

## 运行

```powershell
pwsh -File scripts/eval-<milestone>.ps1            # 起隔离实例 → 跑金标/负控 → 出报告
pwsh -File scripts/eval-<milestone>.ps1 -NoStart   # 服务已运行
```

- 退出码：指标全部达标 0；否则 1。
- 报告：`tests/benchmark/<milestone>/report.json`（含指标、采样次数、模型、时间）。

## 采样与可复现

- 温度/seed：<值>；采样次数：<n>，取<中位/均值>。
- LLM fixture：<record-replay 路径>（若启用）。

## 敏感性

- <阈值附近样本 / 边界样本 / 已知难点>

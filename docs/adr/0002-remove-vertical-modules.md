# ADR-0002：移除垂直业务模块，收敛为通用 Agent 底座

- 状态：已采纳
- 日期：2026-10-10
- 决策者：项目所有者

## 背景

仓库原本按「竖切能力」演进，先后接入两条垂直业务线：

- **Review**（P2 合同要素抽取 + 规则比对）：`IElementExtractor` / `IRuleChecker` / `IReviewReportWriter` 与规则库表。
- **ReviewAdvisor**（P3 审查 Agent）：逐条 finding 的取证 Loop + 引用后置校验。

随之而来的是 `Domain` 里的规则库条目、`Persistence` 里的 `RuleRepository` / `ReviewRepository` / `AdvisorRepository` 与 M005、M007–M013 迁移、`Webapi` 的 Review/Advisor Controller 与 `ReviewHub`、前端 Review/Rules 页面、Workflow 里的 `extract` / `compare` 步骤，以及 P2/P3 的设计与验收文档。

项目定位已明确为**通用 Agent 运行底座**（不绑定行业业务）。垂直模块把业务语义渗进了底座契约（Domain 记录类型、迁移编号、前端路由、验收口径），破坏了「新增能力 = 新增模块」的边界。

## 决策

整体移除垂直业务模块及其痕迹：

- 删除 `src/AgentWorkbench.Review`、`src/AgentWorkbench.ReviewAdvisor` 两个项目；
- 删除 `RuleRepository` / `ReviewRepository` / `AdvisorRepository` 与迁移 M005、M007–M013；
- 删除 Review/Advisor Controller、`ReviewHub`、前端 Review/Rules 页面与 `api/review.ts`；
- `Domain` 去掉规则库条目；Workflow 收敛为与业务无关的步骤类型（`agent` + `report`）；
- 移除 P2/P3 的设计、验收文档与相关测试、基准数据。

保留并作为底座主线的模块：Documents（PDF 管道）、Knowledge（FTS5 + sqlite-vec + RRF）、Providers（模型网关）、Agent（对话运行时）、AgentHarness（loop/context/memory/tools/trace）、Workflow（步骤编排）。

## 影响

- **依赖方向简化**：`Workflow → AgentHarness, Knowledge.Abstractions, Persistence, Domain`（不再经过 Review/ReviewAdvisor）；架构测试切片同步更新。
- **数据库**：新库不再创建 Review 相关表；已存在的本地库保留旧表（迁移程序集已无对应类，FluentMigrator 不会重建，也不会报错）。
- **能力缺口**：仓库不再有任何端到端业务验收场景；`scripts/` 目录从未进入版本库（历史文档中的 `acceptance-*.ps1` / `eval-*.ps1` 均为未落地引用）。底座的验收需按 `docs/测试标准/` 重新落地。
- **文档**：AGENTS.md、README、各模块 README 与 `docs/` 同步去掉垂直引用。

## 备选方案

1. **保留 Review，仅改为可插拔业务模块**：仍会把业务语义留在迁移编号与验收口径里，且底座没有第二个消费者来证明抽象正确——先删，等真有第二个场景再抽象。
2. **保留 ReviewAdvisor 作为「示例 Agent」**：示例会被当成底座的一部分维护，掩盖真实缺口（缺验收、缺负控）。
3. **只删前端与文档，保留后端模块**：半途状态，架构测试与依赖方向仍被污染。

选 1 的性质最轻但无法收敛边界；2、3 均让底座继续背负业务语义。

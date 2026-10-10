# ADR-0002：新增 Review 模块与 Agent 结构化输出契约

- 状态：提议
- 日期：2026-10-05
- 决策者：AgentWorkbench

## 背景
P2（要素抽取 + 模板比对工作流）是相对「检索问答」独立的新能力：
需要读取管道产物（chunks）、驱动 LLM 产出**结构化 JSON**、做规则比对并持久化
带证据的结论。现有模块中：Persistence 负责数据、Knowledge.Local 专注于检索问答，
仅 `IChatService.StreamAsync` 暴露流式文本，无法满足「非流式 + 强 schema」的抽取需求。

## 决策
1. **新增模块 `AgentWorkbench.Review`**（要素抽取 / 规则比对 / 报告），
   自注册 `AddNitroReview`；依赖方向单向：
   `Webapi → Review`；`Review → Agent, Knowledge.Abstractions, Persistence, Domain, Shared`。
2. **Agent 模块新增契约 `IStructuredChatService`**（纯增量）：
   `GetJsonAsync<T>(systemPrompt, userPrompt, jsonSchema, ct)`，内部用
   Microsoft.Extensions.AI 的 `ChatResponseFormat`/JSON Schema + 一次修复重试。
   **保持 P1 `IChatService.StreamAsync` 不变**。
3. 架构边界由 ArchUnitNET 同步纳入切片 `Review`（slnx / 切片覆盖 / 无环 / Domain 纯净）。

## 影响
- 正面：新能力零改动既有模块（只加模块 + 加契约）；结构化输出成为可复用扩展点；
  证据链复用 P1 `?chunk=seq`。
- 负面：多一个项目与一层契约；LLM 结构化输出需兜底（失败不落脏数据）。
- 数据：新增 `review_runs/elements/findings`，扩展 `rules`；`documents` 删除级联清理。

## 备选方案
- 放在 `Knowledge.Local`：与该模块「检索问答」职责混淆，且需引入写侧职责，否决。
- 放在 `Documents`：Documents 是离线管道，与在线工作流/LLM 调用职责不符，否决。
- 各控制器内联实现：破坏「只加不改」与可测试性，否决。
- 重新引入 Semantic Kernel 编排：与 ADR-0001、AGENTS 既定技术栈冲突，否决。

# AGENTS.md

## 项目
AgentWorkbench 是通用 AI Agent 工作台（MVP）：流式对话 + P0 PDF 数据管道 + P1 知识库 RAG + P2 合同要素抽取与比对。采用多项目模块化单体，新增能力 = 新增模块。

## 技术栈
.NET 10 / ASP.NET Core / Microsoft Agent Framework / Microsoft.Extensions.AI / SignalR
SQLite（Dapper + FluentMigrator）/ FTS5 + sqlite-vec / Vue 3 + Vite + TypeScript + Element Plus

## 构建与测试
dotnet build AgentWorkbench.slnx
dotnet test tests/AgentWorkbench.ArchitectureTests
dotnet test tests/AgentWorkbench.UnitTests
pwsh -File scripts/acceptance-p0.ps1
pwsh -File scripts/acceptance-p1.ps1
pwsh -File scripts/acceptance-p2.ps1
pwsh -File scripts/acceptance-p3.ps1
pwsh -File scripts/eval-p3.ps1
pwsh -File scripts/acceptance-harness.ps1
pwsh -File scripts/acceptance-p4.ps1
python scripts/build-industrial-corpus.py   # 工业语料 -> tests/corpus/industrial/*.pdf
python scripts/export-alarms.py             # NitroGateway 真实报警 -> CSV + PDF（需先跑软 PLC）
python scripts/eval-industrial.py           # 工业检索评测 -> tests/benchmark/industrial/report.json
cd web; npm run build

## 模块
| 模块 | 路径 | 说明 |
|------|------|------|
| Webapi | src/AgentWorkbench.Webapi | 组合根：Controllers / Hubs / Program.cs |
| Shared | src/AgentWorkbench.Shared | 配置/常量 |
| Domain | src/AgentWorkbench.Domain | 纯领域：Records / ChatTurn |
| Persistence | src/AgentWorkbench.Persistence | 连接 + FluentMigrator 迁移 + 仓储 |
| Documents | src/AgentWorkbench.Documents | P0 数据管道 |
| Knowledge.Abstractions | src/AgentWorkbench.Knowledge.Abstractions | 知识契约 |
| Knowledge.Local | src/AgentWorkbench.Knowledge.Local | FTS5 + sqlite-vec + RRF |
| Knowledge | src/AgentWorkbench.Knowledge | 聚合 AddNitroKnowledge |
| Providers | src/AgentWorkbench.Providers | 模型网关（供应商/模型 + IChatClient） |
| Agent | src/AgentWorkbench.Agent | 对话 Agent 运行时（Microsoft Agent Framework）+ 工具扩展点 + IStructuredChatService |
| Review | src/AgentWorkbench.Review | P2 要素抽取 + 规则比对工作流（规则确定性 + LLM 增强） |
| ReviewAdvisor | src/AgentWorkbench.ReviewAdvisor | P3 审查 Agent（Agent Loop：清单→带引用结论 + 补正建议） |
| AgentHarness | src/AgentWorkbench.AgentHarness | Agent Harness 运行时横切轴（loop/context/memory/tools/trace，基于 MAF） |
| Workflow | src/AgentWorkbench.Workflow | P4 工作流（Workflow Loop：步骤编排/单步重跑/提示词版本/重试/检查点，托管 P3 为 agent_step） |
| ArchitectureTests | tests/AgentWorkbench.ArchitectureTests | ArchUnitNET 边界强制 |

## 依赖方向（禁止反向/循环）
Webapi → 各模块
Agent → Providers, Domain
Review → Agent, Knowledge.Abstractions, Persistence, Domain
ReviewAdvisor → Review, Agent, Knowledge.Abstractions, Persistence, Domain
AgentHarness → Agent, Providers, Knowledge.Abstractions, Persistence, Domain
Workflow → Review, AgentHarness, Knowledge.Abstractions, Persistence, Domain
Knowledge.Local → Knowledge.Abstractions, Persistence, Providers, Agent
Documents → Knowledge.Abstractions, Persistence
Persistence/Providers/Knowledge.Abstractions → Domain
Domain → Shared；所有模块 → Shared

## 扩展点（新增 = 加类 / 加模块，0 改旧代码）
- IKnowledgeEngine + KnowledgeEngineRegistry（新知识引擎）
- IEmbeddingProvider + EmbeddingProviderRegistry（新向量模型）
- IReranker（重排）、IChatClientFactory（模型网关 → IChatClient）
- IAgentToolProvider（Microsoft Agent Framework 函数工具注入）
- IStructuredChatService（强类型 JSON 结构化输出）
- IElementExtractor + ElementExtractorRegistry（新要素来源）、IRuleChecker + RuleCheckerRegistry（新检查器）、IReviewReportWriter（新导出格式）
- 模块自注册：AddNitroPersistence / AddNitroProviders / AddNitroAgents / AddNitroKnowledge / AddNitroDocuments / AddNitroReview / AddNitroReviewAdvisor / AddNitroAgentHarness / AddNitroWorkflow

## 工作规则
1. 先调查 2. 最小修改 3. 验证（build + 架构测试 + 验收）4. 停止
- 测试与验收遵循 docs/测试标准/Agent-测试与验收标准.md：判定型（绿/红 + 正例绿·负控红）与度量型（指标+阈值+趋势）两条轨道；LLM/检索/生成类改动必须更新度量指标。关键行为先写不变量，负控必须能变红。**能由脚本/程序判定的测试禁止使用 AI（含 LLM-as-judge），LLM 断言用 mock/回放。**

## 约束
- 数据库变更必须用 FluentMigrator（Persistence/Migrations，M0xx 递增）
- 新增能力以新模块/新项目加入；模块间只依赖对方契约，禁止引用实现内部
- Domain 不得引用基础设施（架构测试强制）
- 不得提交 bin/ obj/ node_modules/ dist/；前端产物 web/dist 由 Dockerfile 拷入 wwwroot
- ADR 仅记录跨模块、且影响后续模块变迁的架构决策；单个模块内部实现细节、局部重构、bug 修复、依赖补丁版本升级等不写 ADR（准入标准见 docs/adr/README.md）

## 验收
- 标准：docs/测试标准/Agent-测试与验收标准.md（判定型 + 度量型双轨；模板见 docs/测试标准/模板/）
- P0：scripts/acceptance-p0.ps1（29 断言）
- P1：scripts/acceptance-p1.ps1（9 断言）
- P2：scripts/acceptance-p2.ps1（20 断言；LLM 断言未配模型 SKIP）
- P3：scripts/acceptance-p3.ps1（审查 Agent；本地 mock 驱动，H1/R0/A1–A5/S1/D1/RR1/AL1–AL4/N1–N7/L1 全自动判定，24 断言）+ scripts/eval-p3.ps1（M1/M2/M3 指标 → tests/benchmark/p3/report.json）
- H：scripts/acceptance-harness.ps1（Agent Harness；本地 mock 驱动，H1/HL1–HL4/HC1/HM1/HT1/HT3/HO1/NH4 全自动判定）
- P4：scripts/acceptance-p4.ps1（Workflow Loop；本地 mock 驱动，W1/W0/S1/S2/SR1/PV1/D1 + N1–N4 全自动判定）
- 工业检索：scripts/eval-industrial.py（度量型；Recall@5/MRR 三路对比 + 负控误命中率 → tests/benchmark/industrial/report.json）
- 单元：tests/AgentWorkbench.UnitTests（确定性纯逻辑；LLM 走 mock）
- 架构：tests/AgentWorkbench.ArchitectureTests（3 断言）
- 全过退出码 0

## 参考
- 设计：docs/设计步骤/ ｜ 验收：docs/验收标准/ ｜ 测试标准：docs/测试标准/ ｜ ADR：docs/adr/
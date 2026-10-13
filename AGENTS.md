# AGENTS.md

## 项目
AgentWorkbench 是通用 AI Agent 工作台底座（MVP）：流式对话 + PDF 数据管道 + 知识库 RAG + Agent Harness + Workflow。**零垂直业务语义**。采用多项目模块化单体，新增能力 = 新增模块（见 docs/adr/0002-remove-vertical-modules.md）。

## 技术栈
.NET 10 / ASP.NET Core / Microsoft Agent Framework / Microsoft.Extensions.AI / SignalR
SQLite（Dapper + FluentMigrator）/ FTS5 + sqlite-vec / Vue 3 + Vite + TypeScript + Element Plus

## 构建与测试
dotnet build AgentWorkbench.slnx
dotnet test tests/AgentWorkbench.ArchitectureTests
dotnet test tests/AgentWorkbench.UnitTests
cd web; npm run build

> **验收脚本尚未落地**：仓库没有 `scripts/` 目录，历史文档中的 `acceptance-*.ps1` / `eval-*.ps1` 均为未实现引用。端到端验收需按 docs/测试标准/ 重新落地（缺口见 docs/底座建设清单.md）。

## 模块
| 模块 | 路径 | 说明 |
|------|------|------|
| Webapi | src/AgentWorkbench.Webapi | 组合根：Controllers / Hubs / Program.cs |
| Shared | src/AgentWorkbench.Shared | 配置/常量 |
| Domain | src/AgentWorkbench.Domain | 纯领域：Records / ChatTurn |
| Persistence | src/AgentWorkbench.Persistence | 连接 + FluentMigrator 迁移 + 仓储 |
| Documents | src/AgentWorkbench.Documents | PDF 数据管道 |
| Knowledge.Abstractions | src/AgentWorkbench.Knowledge.Abstractions | 知识契约 |
| Knowledge.Local | src/AgentWorkbench.Knowledge.Local | FTS5 + sqlite-vec + RRF |
| Knowledge | src/AgentWorkbench.Knowledge | 聚合 AddNitroKnowledge |
| Providers | src/AgentWorkbench.Providers | 模型网关（供应商/模型 + IChatClient） |
| ModelRuntime | src/AgentWorkbench.ModelRuntime | 模型交互：统一调用语义（单次请求超时/重试/并发闸门/用量统计） |
| ToolExecution | src/AgentWorkbench.ToolExecution | 工具调用：工具扩展点 + 白名单目录 + 函数调用环 + 通用内置工具 |
| AgentLoop | src/AgentWorkbench.AgentLoop | 执行控制：迭代预算与 run 级超时 |
| Context | src/AgentWorkbench.Context | 上下文组装：检索片段 → 最终 user 文本（纯逻辑） |
| Memory | src/AgentWorkbench.Memory | 状态与记忆：会话存储契约 + 内存实现（sessionId → AgentSession）+ 长期记忆契约 |
| Memory.Persistence | src/AgentWorkbench.Memory.Persistence | 状态与记忆落库：会话快照 / 长期记忆 SQLite + 过期清理 + remember/recall 工具 |
| Safety | src/AgentWorkbench.Safety | 可靠性与安全：工具授权白名单策略 |
| Observability | src/AgentWorkbench.Observability | 可观测与评估：执行轨迹落库与回放 |
| Conversation | src/AgentWorkbench.Conversation | 对话 Agent 运行时（Microsoft Agent Framework）+ IStructuredChatService |
| AgentHarness | src/AgentWorkbench.AgentHarness | Agent 运行时编排门面：装配 model/tools/context/session/loop/trace，自身不实现能力 |
| Workflow | src/AgentWorkbench.Workflow | 工作流（Workflow Loop：步骤编排/单步重跑/提示词版本/重试/检查点；步骤类型 agent + report） |
| MockLlm | tests/AgentWorkbench.MockLlm | 确定性 OpenAI 兼容 Mock 模型（测试替身，永不调真模型） |
| ArchitectureTests | tests/AgentWorkbench.ArchitectureTests | ArchUnitNET 边界强制 |

## 依赖方向（禁止反向/循环）
Webapi → 各模块
Conversation → Providers, ModelRuntime, ToolExecution, Domain
ModelRuntime → （仅 Shared）
ToolExecution → Safety
AgentLoop → （仅 Shared）
Context → Knowledge.Abstractions
Memory → （仅 Shared）
Memory.Persistence → Memory, Persistence, ToolExecution
Safety → （仅 Shared）
Observability → Persistence, Domain
AgentHarness → ModelRuntime, ToolExecution, AgentLoop, Context, Memory, Safety, Observability, Providers, Knowledge.Abstractions
Workflow → AgentHarness, Knowledge.Abstractions, Persistence, Domain
Knowledge.Local → Knowledge.Abstractions, Persistence, Providers, Conversation, ToolExecution
Documents → Knowledge.Abstractions, Persistence, ToolExecution
Persistence/Providers/Knowledge.Abstractions → Domain
Domain → Shared；所有模块 → Shared

## 扩展点（新增 = 加类 / 加模块，0 改旧代码）
- IKnowledgeEngine + KnowledgeEngineRegistry（新知识引擎）
- IEmbeddingProvider + EmbeddingProviderRegistry（新向量模型）
- IReranker（重排）、IChatClientFactory（模型网关 → IChatClient）
- IAgentToolProvider（Microsoft Agent Framework 函数工具注入；一个工具一个类）
- IToolCatalog / IToolExecutionPipeline / IToolPolicy（工具目录与执行环、授权白名单）
- IModelRuntime（统一模型调用语义）· IAgentLoop（预算与超时）· IContextBuilder · ISessionStore / ISessionFactory · ILongTermMemoryStore / IMemoryScopeAccessor · IExecutionTraceStore
- IStructuredChatService（强类型 JSON 结构化输出）
- IWorkflowStepExecutor + WorkflowStepRegistry（新步骤类型）、IPromptStore（提示词来源/存储）、IWorkflowProgressNotifier（进度通道）
- 模块自注册：AddNitroPersistence / AddNitroProviders / AddNitroModelRuntime / AddNitroToolExecution / AddNitroConversation / AddNitroKnowledge / AddNitroDocuments / AddNitroContext / AddNitroMemory / AddNitroMemoryPersistence / AddNitroSafety / AddNitroObservability / AddNitroAgentLoop / AddNitroAgentHarness / AddNitroWorkflow

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
- 单元：tests/AgentWorkbench.UnitTests（确定性纯逻辑；LLM 走 mock）
- 架构：tests/AgentWorkbench.ArchitectureTests（依赖边界 + 无环）
- 评测（L4/L5/L6）：docs/测试标准/评测实现指南.md（MAF Evaluation 提供流水线/容器/缓存；判据、金标、阈值、基线与门禁必须自建）
- **端到端验收脚本尚未落地**：仓库无 `scripts/` 目录；docs/验收标准/ 与 docs/设计步骤/ 中引用的 acceptance/eval 脚本均未实现，相关内容以「未实现」标注为准
- 全过退出码 0

## 参考
- 设计：docs/设计步骤/ ｜ 验收：docs/验收标准/ ｜ 测试标准：docs/测试标准/ ｜ 评测实现：docs/测试标准/评测实现指南.md ｜ ADR：docs/adr/ ｜ 缺口清单：docs/底座建设清单.md

# AgentWorkbench.Webapi

组合根模块：宿主 `Program.cs`、Controllers 与 SignalR Hub。

## 功能

| 文件 | 说明 |
| --- | --- |
| `Program.cs` | 组合根：模块注册（`AddNitroPersistence/Safety/Providers/ModelRuntime/ToolExecution/Conversation/Knowledge/Documents/Context/Memory/MemoryPersistence/Observability/AgentLoop/AgentHarness/Workflow`）、Serilog、64MB 上传上限、启动即迁移 + 种子默认供应商与工作流、静态文件 + SPA、CORS、映射 Controllers 与 Hub |
| `Controllers/HealthController.cs` | 健康检查 |
| `Controllers/DocumentsController.cs` | P0 文档：上传 / 列表 / 详情 / 页 / 块 / 重解析 / 删除 / 下载原始 PDF |
| `Controllers/KnowledgeController.cs` | P1 知识库：健康 / 索引 / 检索 / 带引用问答 |
| `Controllers/SettingsController.cs` | 供应商与模型 CRUD / 激活 / 拉取模型 / 连通测试 |
| `Controllers/ConversationsController.cs` | 会话列表 / 新建 / 消息 / 删除 |
| `Controllers/HarnessController.cs` | Agent Harness：健康 / 运行 / 运行详情 |
| `Controllers/WorkflowController.cs` | 工作流：定义 / 运行 / 步骤 / 单步重跑 / 取消 / 恢复 / 提示词版本 |
| `Hubs/AgentHub.cs` | SignalR 对话：`Send`（普通对话）、`Ask`（RAG 问答）、`Stop`（停止生成）；事件 `ReceiveSources/ReceiveDelta/ReceiveDone/Error` |
| `Hubs/WorkflowHub.cs` | 工作流步骤级进度推送（`/hubs/workflow`） |

## 界面投影（本模块独有的职责）

`messages`（`conversations` 子表）是**宿主投影，不是底座能力，也不是模型记忆**。
它只回答「浏览器刷新后该显示什么」：由 `Hubs/AgentHub.cs` 决定写什么、何时写
（`:133` run 前写 user、`:360` 收尾写 assistant），`ConversationsController` 读，`web/` 消费。

- **性质**：可裁剪的展示层职责。换 CLI / 定时任务 / 无聊天窗的嵌入调用，它就不存在；
  拿掉它底座照常跑，只是刷新看不到历史。所以**归 Host，不归 Core**。
- `Core/Foundation/Persistence` 里的 `ConversationRepository` 只是被借用的通用表读写机制，
  不认识 `messageId`、不认识「刷新」、不认识流式与前端展示结构。**代码在 Core ≠ 职责归 Core**。
- 它和 `chat_messages`（模型上下文）**没有汇合点，也不做一致性承诺**。由此剩下的两个未定边界
  （失败轮要不要留痕、删会话时模型上下文跟不跟着清）见 `docs/core/runtime.md` §7.3 第 1 条。

## 输入身份契约（web 传输模型带来的必答题）

**问题**：HTTP / SignalR 是「调用」不是「事件」。超时重试会让同一次意图送达两次，而判重表只认
`requestId`，所以「哪两次算同一次输入」必须由发起方自报，且服务端要在最前面拒绝没自报的调用。
换 CLI / 定时任务根本没有重放这回事——**这是 web 宿主特有的面，解法只在 Host，不进 `src/Core`**：
换域 diff = 0，非 web 宿主也不必伪造输入号。这条边界是 2026-11-09 定下的。

**落在哪**：`Actions/InputIdentity.cs` 是唯一守卫，三条 web 入口都调它。

- `Hubs/AgentHub.cs`：`ChatRequest.ClientMessageId` 必填。判空在 `Generations.TryAdd` **之前**——
  缺了不建会话、不落 user 消息、不探活、不起 run、不建身份作用域。
- `Controllers/ActionsController.cs`：`ActionSubmitBody.ClientMessageId` 必填，缺了返回 400 +
  `client_message_id_required`。提交体**不再收 `RequestId` / `IdempotencyKey`**（客户端自报最终判重键
  等于把判重权交出去）；请求号由 Host 用 `InputIdentity.Derive` 算，无会话入口用归属 id 当隔离维度。
- `Controllers/HarnessController.cs`：`HarnessRunBody.ClientMessageId` 必填。这条入口**不构造
  `ActionRequest`，但会起工具环**——漏了它，模型在里面调 `propose_action` 照样退化成随机号。
  无会话时由 Host 生成 sessionId，并用同一个值 `Begin` 短作用域包住 `RunAsync`。
- `Actions/ActionSubjectProvider.cs`：`Begin` 的会话与消息标识改为必填，作用域内身份必非空，
  工具环里的 `IActionOriginProvider` 因而拿得到「这是哪一次输入」。

**没改什么**：`src/Core` 行为零改动。`ActionRequestId.Derive` 的「缺省回退随机号」保留，从三条 web 入口
都走不到；判重表、结果回放（`replayed_from_idempotency`）、超时只查证一律没动。

**已知代价（现存，不是待办，是取舍）**：`Core/Orchestration/Workflow/Steps/AgentStepExecutor.cs`
自己调 `IAgentHarness.RunAsync` 且不建 `IActionSubjectScope`，模型在那条路上递动作**仍会退化成随机号**。
它属于 Core 编排，按「解法只在 Host」的边界本次未动；要收口必须另定（改 Core 或在工作流运行时注入身份）。
其它绕过 Host 直调 Core 的新入口同理。**新增任何动作入口前，先回到本节把身份放进作用域或调用
`InputIdentity.Derive`。**

**怎么验**：`dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --filter "FullyQualifiedName~InputIdentityContractTests"`（H1–H10）。
H1/H3/H9 锁「三条入口缺身份零副作用」，H2/H8/H10 锁「身份真的进作用域」，H4 锁「同号重放只执行一次」，
H5 锁「换一次输入仍是新请求」（防误杀真实需求），H6 锁键序 / 参数语义，H7 锁提交体形状。
负控：删掉任一入口判空 → H1/H3/H9 红；把 `Derive` 改回随机 → H4/H6 红。

## 依赖

全部模块（组合根是唯一允许依赖所有模块的地方）：Shared、Domain、Persistence、Documents、Knowledge、Providers、
ModelRuntime、ToolExecution、Safety、AgentLoop、Context、Memory、Memory.Persistence、Observability、Conversation、AgentHarness、Workflow。
反向依赖**目前只由 csproj 保证**：`tests/AgentWorkbench.ArchitectureTests` 源码为空，
旧文里写的 `LayeringTests.Modules_should_not_depend_on_webapi` 不存在，改引用时不会有任何东西变红，
需人工确认。

> 注册顺序有要求：先 `AddNitroMemory` 再 `AddNitroMemoryPersistence`，后者用 `services.Replace`
> 把内存版 `ISessionStore` 换成落库版。写反了会静默用回内存版（重启即丢）。

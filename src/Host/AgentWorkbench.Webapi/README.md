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

## 依赖

全部模块（组合根是唯一允许依赖所有模块的地方）：Shared、Domain、Persistence、Documents、Knowledge、Providers、
ModelRuntime、ToolExecution、Safety、AgentLoop、Context、Memory、Memory.Persistence、Observability、Conversation、AgentHarness、Workflow。
反向依赖**目前只由 csproj 保证**：`tests/AgentWorkbench.ArchitectureTests` 源码为空，
旧文里写的 `LayeringTests.Modules_should_not_depend_on_webapi` 不存在，改引用时不会有任何东西变红，
需人工确认。

> 注册顺序有要求：先 `AddNitroMemory` 再 `AddNitroMemoryPersistence`，后者用 `services.Replace`
> 把内存版 `ISessionStore` 换成落库版。写反了会静默用回内存版（重启即丢）。

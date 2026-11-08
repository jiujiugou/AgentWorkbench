# AgentWorkbench.Domain

纯领域模块，只依赖 Shared，不得引用任何基础设施/实现模块（架构测试强制）。

## 功能

承载与基础设施无关的领域模型：

| 文件 | 说明 |
| --- | --- |
| `Documents/DocumentRecords.cs` | 文档状态机、文档 / 页 / 切分块 |
| `Providers/ProviderRecords.cs` | 模型供应商（OpenAI 兼容端点，Key 明文存储，待加密）、供应商下的可选模型（`is_default` 表示当前使用模型） |
| `Observability/AgentRunRecords.cs` | Agent 运行 / 步骤轨迹 |
| `Conversations/ConversationRecords.cs` | 会话 / 消息 |
| `Workflow/WorkflowRecords.cs` | 工作流定义 / 运行 / 步骤 / 提示词版本 |
| `ChatTurn.cs` | 一轮对话，用于回放历史 / RAG 上下文 |

> 按功能域分子目录存放（命名空间仍是 `AgentWorkbench.Domain`），一个文件对应一块领域记录，便于人读。

## 依赖

Shared。

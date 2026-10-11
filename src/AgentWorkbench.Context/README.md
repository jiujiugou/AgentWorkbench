# AgentWorkbench.Context

上下文模块（功能域 4 的「上下文」侧）：把一次模型调用要看的输入拼成最终 user 文本。
**纯逻辑、无 IO** —— 检索本身由 `IKnowledgeEngine` 提供。

## 功能

| 文件 | 说明 |
| --- | --- |
| `ContextBuilder.cs` | `IContextBuilder.Compose(input, hits)`：把检索命中拼成 `【知识库检索片段】…《文件名》片段#Seq (pX-Y) :: 摘要` 前置到用户输入；无命中则原样返回 |
| `ContextOptions.cs` | 配置节 `Harness:Context`：`Enabled=true`（是否做 RAG 注入）/ `RagTopK=5` |
| `ContextServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroContext` |

## 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IContextBuilder` | `Compose(input, hits)` | `ContextBuilder` | AgentHarness `RunAsync`（`UseKnowledge=true` 时） |

> 未落地：历史消息裁剪 / token 预算压缩（缺口清单）——当前只做「检索片段 → 上下文前缀」这一步组装。

## 依赖

Knowledge.Abstractions（`RetrievalHit`）、Shared。

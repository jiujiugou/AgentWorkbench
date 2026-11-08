# 上下文组装模块（Runtime.Context）

这个模块只干一件事：**把「历史 + 本轮输入 + 指令 + 片段」组装成一次模型请求的上下文，并负责注入形态与预算。**

它**不读数据库、不调检索、不认识业务**。历史从哪来（会话存储）、片段从哪来（检索 / 前置步骤），都由调用方决定。

---

## 一、为什么收拢到一个模块

收拢前，「什么进模型请求」散在三处，规则各不相同：

| 位置 | 原来的做法 | 问题 |
|------|-----------|------|
| `Runtime.Harness` | `new ChatMessage(User, input)`，历史交给 `InMemoryChatHistoryProvider` | 只有一条路，没有任何地方能统一加片段 |
| `Knowledge.Support/ChatService` | 自己 `BuildMessages(history, userMessage)` | 第二条组装路，与 Harness 的规则会分岔 |
| `Workflow/AgentStepExecutor` | 自己拼「文档上下文 + 前置步骤 + 检索依据」大字符串 | 第三条路，且拼的是业务语义 |

现在：**消息形态、历史截断、片段预算、指令注入**统一在 `IContextAssembler`；业务语义（哪些片段该进来、怎么排序）仍留在调用方。

---

## 二、分层

```
调用方（Harness / ChatService / Workflow）
        │  传入 ContextRequest（纯数据，不含存储）
        ▼
Runtime.Context     ← 只依赖 Shared + Microsoft.Extensions.AI / MAF
  ContextAssembler  → ContextBundle（Messages / Instructions / Fragments）
  ContextInjectingChatClient → 把 Instructions 作为 system 消息插到请求最前
```

依赖方向：`Runtime.Harness → Runtime.Context`、`Knowledge.Support → Runtime.Context`；**Context 不反向依赖任何模块**，也不依赖 `Runtime.Session`（会话是存储，组装是纯逻辑）。

---

## 三、和 MAF 的关系（为什么不用 AIContextProviders）

MAF 1.23 提供了三层上下文能力：

1. `ChatHistoryProvider` / `InMemoryChatHistoryProvider` —— 会话历史，**已使用**，创建入口收在本模块的 `CreateChatHistoryProvider()`。
2. `AIContext`（Messages / Instructions / Tools 三件套）—— 本模块的 `ContextBundle` 就是它的投影；MAF 未公开 `AIContext` 构造函数，因此用自有 record 承载，语义一一对应。
3. `AIContextProvider` —— **不挂到 Agent 上**。挂 `ChatClientAgentOptions.AIContextProviders` 会让 MEA 在我们装配的 `FunctionInvokingChatClient` 外面再包一层函数调用环，绕过 `ToolExecutionPipeline` 的守卫与轨迹采集（实测：同参重复调用不再判 `loop_detected`，工具异常不再归类 `tool_error`）。

替代做法：`ContextInjectingChatClient` 与原来的 `RunInstructionsChatClient` 同层，调用链一个节点不动。等 MAF 开放 `AIContext` 的公开构造、或允许 provider 在无 `AIAgent` 的链上运行，再把装饰器换成官方 `AIContextProviderChatClient` 即可，对外契约不变。

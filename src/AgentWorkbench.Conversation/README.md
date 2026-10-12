# 对话运行时模块（Conversation）

这个模块只干一件事：**把「历史 + 本轮输入」装配成消息交给模型，再把模型吐出的 token 流式回给调用方**。

它不含任何工具定义（工具在 `ToolExecution` / `Knowledge.Local` / `Documents`），不落库（对话历史的落库在
Webapi `AgentHub`），也不管执行预算（迭代与墙钟在 `AgentLoop`，单次请求超时 / 重试在 `ModelRuntime`）。

---

## 一、现在已经具备的能力

### 流式对话怎么走

- `IChatService.StreamAsync(history, userMessage, ct, systemPrompt?)` → `IAsyncEnumerable<string>`。
- `history`（`IReadOnlyList<ChatTurn>`）由调用方提供：本模块**不查库、不缓存**，只做
  `ChatTurn → ChatMessage` 的装配，末尾追加本轮 user 消息。
- `systemPrompt` 可覆盖默认系统提示词 —— RAG 问答就是这么把「强制引用」的约束加进去的。
- 模型端点经 `IChatClientFactory` 解析、经 `IModelRuntime.Wrap` 包一层，超时 / 重试 / 并发 / 用量统一。

### 工具与降级

- 工具来自 `IToolCatalog`（已按 `Safety` 白名单裁剪）；`ProviderOptions.SupportsTools=false` 时不带工具。
- 带工具的请求撞上客户端错误（`ClientResultException`，4xx）且**一个 token 都还没吐**时，
  自动降级为无工具重试一次。只重试一次，不会反复空转。

### 结构化输出

- `IStructuredChatService.GetJsonAsync<T>`：首次 JSON 解析失败 → 追加修复提示 + `temperature=0` 重试一次。
- `IChatClientProvider`：给需要原生函数调用的模块复用同一个网关，当前**没有消费者**。

### 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IChatService` | `IsConfiguredAsync`、`StreamAsync` | `ChatService` | Webapi `AgentHub`、Knowledge `RagService` |
| `IStructuredChatService` | `IsConfiguredAsync`、`GetJsonAsync<T>` | `StructuredChatService` | 无（预留） |
| `IChatClientProvider` | `GetClientAsync` | `ChatClientProvider` | 无（预留） |

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| 历史原样全量入参 | 长会话必然超上下文窗口 | `BuildMessages` 不裁剪、不计 token | 裁剪或摘要（能力落在 `Context`） |
| 落库不在本模块 | 换个入口（Harness / Workflow）就没有对话历史 | 写 `messages` 表的是 Webapi `AgentHub` | 明确「谁负责落库」，或抽成契约由编排层统一 |
| 两个会话机制并存 | 同一段对话，`AgentHub` 重放历史、`Harness` 复用会话，两套语义 | 无状态 vs `ISessionStore` | 定一条主路，写进契约 |
| 对话侧没有执行轨迹 | 对话入口发生了什么，事后查不到 | 轨迹只有 Harness 有 | 对话入口也留痕，或明确「对话不落轨迹」 |
| 注释与实现不符 | `AgentHub` 注释说「汇报工具调用事件」，实际只推 delta | 只有 `ReceiveDelta` / `ReceiveDone` / `Error` | 要么补事件，要么改注释 |
| 取消语义没有测试 | 「停止生成」是否真的停在下游，没有红绿 | Webapi 按连接维护 CTS | 取消正例 + 负控（不取消必须跑完） |
| 历史里的工具调用不落库 | 前端看不到中间过程，回放不完整 | `messages` 只存 user / assistant 文本 | 定了口径再落 |

---

## 三、加一个新的对话入口要做什么

1. 直接消费 `IChatService`（或 `IStructuredChatService`）。
2. **不要复制**降级 / 重试逻辑，也不要自己 `new ChatClientAgent` —— 网关与运行时策略在
   `Providers` / `ModelRuntime`。
3. 历史从哪来要自己说清楚：要么无状态传历史，要么走 `ISessionStore`。

---

## 四、红线

1. **不定义工具**。工具属于各自能力的模块。
2. **不绕开模型网关**：不直接读配置里的 key 建客户端。
3. **不把异常吞成空串**。错误要么冒泡，要么变成调用方能分辨的异常。
4. **降级只允许「无工具重试一次」**，禁止静默丢弃用户输入。

---

## 五、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

目前覆盖到的（`tests/AgentWorkbench.UnitTests/Conversation/`）：

- 消息装配：`history` 的角色与顺序正确，本轮输入在最后。
- tools 4xx 降级：带工具首次失败 → 第二次不带工具且成功；已经吐过 token 时不重试（不重复输出）。

还没有的：历史裁剪的正例 / 负控（能力未实现）、取消传播的判定用例、结构化输出重试的判定用例。

## 依赖

Domain、ModelRuntime、Providers、ToolExecution、Shared。


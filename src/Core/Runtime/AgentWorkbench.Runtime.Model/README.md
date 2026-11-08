# AgentWorkbench.ModelRuntime

模型交互模块（功能域 1）：把「一次模型调用」收敛成统一语义 —— 单次请求超时、网络错误重试、
在途并发闸门、用量统计。**供应商协议差异由 `Providers` 消化**，本模块只关心「一次调用怎么被约束和度量」。

## 负责的功能

| 功能 | 说明 | 落点 |
| --- | --- | --- |
| 统一装饰出口 | `Wrap(IChatClient)`，幂等（已包装的原样返回）；所有模型调用统一经此出口 | `ModelRuntimeService.cs` |
| 单次请求超时 | `RequestTimeoutSeconds`（0 = 不限）→ 超时抛 `TimeoutException`；与 run 级超时（AgentLoop 的 `AgentRunTimeoutException`）可区分 | `ModelRuntimeChatClient.cs` |
| 瞬时错误重试 | 仅连接类错误（`HttpRequestException` / `IOException` / `SocketException`）与 408 / 429 / 5xx；指数退避 `RetryBaseDelayMilliseconds × 2^(n-1)`，上限 `MaxRetries`；非瞬时错误一次都不重试 | `ModelRuntimeChatClient.cs` |
| 流式重试语义 | 流式请求只在「尚未吐出任何增量」时重试，避免把半截回答重复一遍 | `ModelRuntimeChatClient.cs` |
| 在途并发闸门 | `MaxConcurrentRequests`（0 = 不限），覆盖流式请求的整个生命周期 | `ModelRuntimeChatClient.cs` |
| 用量统计 | 请求数（每次**实际发出**的请求，重试尝试各计一次）+ 输入 / 输出 token；经 `GetUsage()` 读快照 | `ModelUsageMeter.cs` |
| 模块自注册 | `AddNitroModelRuntime` | `ModelRuntimeServiceCollectionExtensions.cs` |

扩展点 / 契约：`IModelRuntime.Wrap`、`IModelRuntime.GetUsage`。

## 框架提供 vs 自研

| 机制 | 来源 | 位置/说明 |
| --- | --- | --- |
| `DelegatingChatClient`（装饰基类） | **MEA** | `ModelRuntimeChatClient` 的父类 |
| 端点解析（多供应商/多模型、激活切换） | 自研 `Providers` | `IChatClientFactory` —— 本模块不做端点选择 |
| 超时 / 重试退避 / 并发闸门 / 用量累计 | 自研（策略层） | `ModelRuntimeChatClient` + `ModelUsageMeter` |

## 配置节

`Harness:ModelRuntime`：`RequestTimeoutSeconds`（0=不限）/ `MaxRetries=2` / `RetryBaseDelayMilliseconds=200` / `MaxConcurrentRequests`（0=不限）。

## 消费者

| 消费者 | 用法 |
| --- | --- |
| Conversation `ChatService` / `StructuredChatService` / `ChatClientProvider` | `_runtime.Wrap(_factory.GetClient(endpoint))` |
| AgentHarness `AgentHarnessService` | 工具环内层客户端先经 `Wrap` 再交给 `ToolExecutionPipeline` |

## 依赖

Shared。

<!--
不负责（边界）
- 端点选择：调哪家、哪个模型 → Providers（IChatClientFactory）。
- run 级预算 / 迭代上限 / 整轮墙钟超时 → AgentLoop。
- 上下文组装、检索片段拼接、按窗口裁剪历史 → Context；会话状态（sessionId → 历史）→ Memory。
- 用量的「决策与消费」：谁读 GetUsage()、超预算怎么办 → AgentLoop / Observability。本模块只负责如实计数。
- 供应商 native 协议适配 → Providers；本模块只消费统一后的 MEA 类型。

现状与偏差（下列与上面「负责的功能」不一致，尚未修复）
- 并发闸门实际失效：Wrap 在各调用方都是「每请求调一次」，而 SemaphoreSlim 是 ModelRuntimeChatClient 的实例字段 → 每个请求各自拿满许可，跨请求永不阻塞。要生效需把闸门提到单例层，或缓存包装结果。现有单测 Wrap_IsIdempotent 之所以绿，是因为测试内手动 wrap 两次，生产路径每次 inner client 都不同，幂等分支不会走到。
- 单次请求超时默认关闭：RequestTimeoutSeconds 默认 0，且 appsettings.json 中没有 Harness:ModelRuntime 节。实际兜底只剩 AgentLoop 的 run 级 30s（仅 Harness 路径），Conversation 直连路径没有兜底。
- 流式重试无测试：现有单测四条全在非流式路径上，「流式只在零增量时重试」属未验证声明。
- 错误分类绑在 OpenAI SDK 类型上：IsTransient 认 ClientResultException（System.ClientModel），与「供应商差异由 Providers 消化」的声明相冲突；接入非 OpenAI 兼容适配器后该判断会静默失效，退化为一律不重试。
- 供应商若不回传 UsageContent，流式 token 计数为 0（只记请求数）——这是口径如此，不是缺陷，但度量报表需按此解读。
-->

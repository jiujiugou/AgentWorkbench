# AgentWorkbench.Providers

模型网关模块：回答「用哪家、哪个模型」，对上屏蔽具体供应商。

## 负责的功能

| 功能 | 说明 | 落点 |
| --- | --- | --- |
| 端点解析 | 活跃供应商 + 默认模型（可被 `modelOverride` 覆盖）→ `ChatEndpoint(ProviderId, BaseUrl, ApiKey, Model)`；无活跃供应商时回退 `Provider` 配置节；两者都没有则返回 `null` | `ChatClientFactory.cs` |
| 统一出口 | 产出 MEA 的 `IChatClient`；上层只见 `ChatResponse` / `ChatResponseUpdate` / `AIContent`，不见供应商 SDK 类型 | `ChatClientFactory.cs` |
| 客户端缓存与热切换 | 按 `BaseUrl\|ApiKey\|Model` 缓存客户端；切换供应商或模型无需重启 | `ChatClientFactory.cs` |
| 供应商与模型管理 | 供应商 / 模型的注册、激活、默认模型指定 | `IProviderRepository`（Persistence）+ Webapi `SettingsController` |
| 连通性与模型探测 | 拉取 OpenAI 兼容 `/models`、测试连接耗时 | Webapi `SettingsController` |
| 失败可诊断 | 非成功响应记录完整 HTTP 状态码与响应体，再原样抛出 | `DiagnosticsChatClient.cs` |
| 模块自注册 | `AddNitroProviders`；`SeedDefaultProviderAsync` 首次把配置种子为一条激活供应商 | `ProvidersServiceCollectionExtensions.cs` |

扩展点：`IChatClientFactory` —— 支持同一供应商下多模型、免重启切换。

## 依赖

Domain、Persistence、Shared。

<!--
不负责（边界）
- 超时 / 重试 / 退避 / 并发闸门 / 用量统计 → ModelRuntime。
- 流式推送到客户端 → Conversation（`IChatService.StreamAsync`）；本模块只提供带流式能力的 `IChatClient`。
- 上下文组装、检索片段拼接、历史轮数裁剪 → Context。
- run 级预算与迭代上限 → AgentLoop。
- 工具授权白名单 → Safety；工具环装配 → ToolExecution。
- 非 OpenAI 兼容协议的原生适配（Anthropic / Gemini native / Bedrock 等）：当前只有「任意 OpenAI 兼容 /v1 端点」这一条线。真要接，落点是新增一个 IChatClient 实现（新 adapter 类），上层无需改动。

现状与偏差
- 模型能力元数据缺失：除全局 `ProviderOptions.SupportsTools` 布尔外，没有 per-model 能力标签（是否支持工具 / 视觉 / 上下文窗口大小）。
- 旧版本 README 曾把 `ChatService.cs` 列为本模块文件，并描述为「封装 Semantic Kernel」「StreamAsync 流式输出」——三处均不属实：该文件位于 Conversation 模块，且基于 Microsoft Agent Framework；流式推送也归 Conversation。相应行已删除，勿再补回。
- 旧版本「依赖」只写了 Domain、Shared，遗漏了 Persistence（`IProviderRepository` 是硬依赖）。
-->

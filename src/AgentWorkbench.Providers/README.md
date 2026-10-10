# AgentWorkbench.Providers

模型网关模块：多供应商/多模型运行时解析与流式对话，对上层屏蔽具体供应商。

## 功能

| 文件 | 说明 |
| --- | --- |
| `ProvidersServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroProviders` |
| `ChatClientFactory.cs` | 按供应商 / 模型解析出对话端点（供应商 + 模型 + 端点参数） |
| `ChatService.cs` | 对话生成服务：封装 Semantic Kernel，屏蔽具体供应商；`StreamAsync` 流式输出、`IsConfiguredAsync` 判断是否已配置 |

扩展点：`IChatClientFactory`。支持同一供应商下多模型、免重启切换。

## 依赖

Domain、Shared。

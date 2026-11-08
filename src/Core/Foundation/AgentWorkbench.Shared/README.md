# AgentWorkbench.Shared

配置与常量模块，被所有模块依赖，不依赖任何其他模块。

## 功能

提供各模块共用（或由宿主捆绑）的强类型配置选项：

| 文件 | 说明 |
| --- | --- |
| `Options/StorageOptions.cs` | 本地存储与上传限制配置 |
| `Options/ProviderOptions.cs` | 模型供应商配置 |
| `Options/KnowledgeOptions.cs` | 知识库（RAG）配置：引擎选择、检索参数、Embedding、重排；Embedding 子项 `Provider = none \| ollama \| openai`；重排 P1 默认关闭 |

## 依赖

无（其他所有模块 → Shared）。

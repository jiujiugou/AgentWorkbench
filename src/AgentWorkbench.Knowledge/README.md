# AgentWorkbench.Knowledge

知识库聚合模块（P1）：对外只暴露一行 DI 注册，把 Knowledge.Local 的各组件装配起来。

## 功能

| 文件 | 说明 |
| --- | --- |
| `KnowledgeServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroKnowledge`：绑定 `KnowledgeOptions`，注册 `IEmbeddingProvider`、sqlite-vec 向量存储、`VectorIndex`、`ChunkIndexer`、`IReranker`、`HybridRetriever`、`LocalKnowledgeEngine`、`IKnowledgeEngine`（经 `KnowledgeEngineRegistry.Resolve`）与 `IRagService` |

新增知识引擎实现 = 新增模块 + 在注册处切换，不改旧代码（扩展点：`IKnowledgeEngine` / `IEmbeddingProvider` / `IReranker`）。

## 依赖

Knowledge.Local（间接 Knowledge.Abstractions、Persistence、Providers）。

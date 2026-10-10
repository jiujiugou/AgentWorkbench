# AgentWorkbench.Knowledge.Abstractions

知识库契约模块（P1）：定义引擎、Embedding、重排等扩展点，供上层与实现方依赖，不含具体实现。

## 功能

| 文件 | 说明 |
| --- | --- |
| `IKnowledgeEngine.cs` | 知识引擎契约：能力位（前端据此决定按钮是否可用）+ 健康状态 |
| `KnowledgeConstants.cs` | 知识库常量 |
| `Embedding/IEmbeddingProvider.cs` | 文本向量化统一接口；含未配置时的空实现（调用即抛，供上层优雅降级为纯全文检索） |
| `Retrieval/IReranker.cs` | 重排抽象：对融合后的候选重排（P1 默认关，占位为 `NullReranker`） |
| `Retrieval/RetrievalHit.cs` | 一条检索命中：来源 + 正文 + 各路分数（用于调试面板与引用） |

## 依赖

Domain、Shared。

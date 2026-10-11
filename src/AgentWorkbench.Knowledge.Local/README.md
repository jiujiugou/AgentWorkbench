# AgentWorkbench.Knowledge.Local

本地自研知识引擎实现（P1）：FTS5 + sqlite-vec 混合检索 + 带引用问答。

## 功能

| 文件 | 说明 |
| --- | --- |
| `LocalKnowledgeEngine.cs` | 本地自研知识引擎：FTS5 + sqlite-vec 混合检索 + 带引用问答 |
| `KnowledgeEngineRegistry.cs` | 按配置解析知识引擎；未实现引擎（anythingllm / ragflow / opencontracts）降级为不可用 stub |
| `RagService.cs` | 检索增强问答：检索 → 拼上下文（强制引用）→ 流式生成；无命中则拒答 |
| `Indexing/ChunkIndexer.cs` | 将切分块写入索引（向量 + 全文） |
| `Indexing/VectorIndex.cs` | sqlite-vec 向量索引封装 |
| `Retrieval/HybridRetriever.cs` | 混合检索：向量（sqlite-vec）∪ 全文（FTS5/BM25）→ RRF 融合 |
| `Retrieval/ChunkVectorRecord.cs` | 向量集合中的一条 chunk 记录（正文回查 chunks 表，不在此重复存） |
| `Embedding/EmbeddingProviderRegistry.cs` | 按配置选择 Embedding 实现（`none` 时降级为空实现） |
| `Embedding/OllamaEmbeddingProvider.cs` | 基于 Ollama（OllamaSharp）的本地向量化，默认模型 `bge-m3` |
| `Embedding/OpenAiEmbeddingProvider.cs` | OpenAI 兼容 `/embeddings` 供应商（如硅基流动 bge-m3）作为私有化之外的回退 |
| `Tools/SearchKnowledgeTool.cs` | 工具 `search_knowledge`（只读）：检索带来源的原文片段。**放在本模块内**，避免工具平台（ToolExecution）反向依赖知识库；引擎经 `IServiceProvider` 惰性解析避免 DI 环 |

## 依赖

Knowledge.Abstractions、Persistence、Providers、Conversation（`IChatService` 供 RAG 问答）、ToolExecution（仅工具扩展点契约）、Domain、Shared。

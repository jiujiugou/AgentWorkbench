# 本地知识引擎（Knowledge.Local）

这个模块只干一件事：**给「外部知识库」提供一个本地实现 —— 把文档切成块、建好索引、按问题检索出带来源的原文片段**。

它是**外部知识**，不是记忆：这里没有会话、没有用户偏好、没有历史轮次。谁在问、上次聊了什么，
与本模块无关（历史轮次只作为入参透传给对话运行时）。

---

## 一、现在已经具备的能力

### 索引怎么建

- `ChunkIndexer` 把切分块写两路：FTS5 全文（BM25，trigram 分词，对中文子串友好）+ sqlite-vec 向量索引。
- 正文只存在 `chunks` 表，向量集合里只存引用（`ChunkVectorRecord`），不重复存正文。
- 没有可用的向量模型时（配置为 `none`）降级为纯全文检索，不报错。

### 检索怎么做

- `HybridRetriever`：向量检索 ∪ 全文检索 → **RRF 融合** →（可选重排，当前是 `NullReranker`）→ 截断到 `topK`。
- 每条命中都带来源：文件名、页码、片段序号、标题，以及向量分 / 文本分 / 融合分，前端能据此展示引用。

### 问答怎么做

- `RagService.StreamAnswerAsync`：检索 → 拼「只能依据资料回答 + 强制标注 `[编号]` 出处」的系统提示词 →
  经 `IChatService` 流式生成。
- **无命中或最高融合分低于 `MinFusedScore` 直接拒答**（回一句「资料不足以回答该问题。」），不硬编。
- 历史轮次由调用方传入并原样透传 —— 本模块不裁剪、不压缩。

### 对外暴露

- 只读工具 `search_knowledge`：**放在本模块内**，避免工具平台（`ToolExecution`）反向依赖知识库；
  引擎经 `IServiceProvider` 惰性解析，绕开 DI 环。
- `KnowledgeEngineRegistry`：按配置解析引擎；未实现的引擎（anythingllm / ragflow 等）降级为不可用 stub，
  能力位（`Capabilities`）告诉前端按钮该不该亮。
- 向量化两实现：Ollama（`bge-m3`，默认）与 OpenAI 兼容 `/embeddings`（回退），
  由 `EmbeddingProviderRegistry` 按配置选。

### 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IRagService` | `RetrieveAsync`、`StreamAnswerAsync` | `RagService` | Webapi `AgentHub`（`Ask`）、`KnowledgeController` |
| `IKnowledgeEngine` | 能力位、状态、索引、检索、问答 | `LocalKnowledgeEngine` | `AgentHarness`、Workflow `AgentStepExecutor` |
| `IEmbeddingProvider` | `EmbedAsync` | Ollama / OpenAI 兼容 | `ChunkIndexer`、`HybridRetriever` |
| `IReranker` | `RerankAsync` | `NullReranker`（占位） | `HybridRetriever` |

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| 与「记忆」的边界容易被混用 | 把会话历史当知识检索、把知识当记忆，两边都失真 | 边界已定（本模块无会话概念） | 保持隔离：知识只来自文档，会话历史只来自 `Memory` / `messages` |
| 检索片段没有预算 | 命中条数一多，上下文照样超窗 | `TopK` 固定，无数 token 计量 | 与历史共用一份预算（能力在 `Context`） |
| 没有检索质量度量 | 换 embedding / 改融合权重后，好坏全靠感觉 | 评测集待建（`tests/benchmark/`） | 固定语料 + 度量型指标（召回、命中率） |
| 无命中直接拒答 | 聊天场景体验偏硬 | `RagService` 的固定行为 | 明确策略：拒答还是提示「依据不足，需人工确认」 |
| 重排是空实现 | 融合后的顺序只能用 RRF，没法再优化 | `NullReranker` 占位（默认关） | 接一个真 reranker，并给出「接上之后指标变化」 |
| 索引与文档状态耦合 | 文档重解析后索引可能不一致 | 索引由 `Documents` 管道触发 | 定重建 / 失效口径，并能验证 |

---

## 三、加一个知识引擎要做什么

1. 实现 `IKnowledgeEngine`（能力位 + 状态 + 索引 + 检索 + 问答）。
2. 在 `KnowledgeEngineRegistry` 里注册解析规则。
3. 在 `Knowledge` 聚合模块的 `AddNitroKnowledge` 里完成装配。

---

## 四、红线

1. **只读**：检索与问答不写用户数据；索引只由文档管道触发。
2. **工具住在本模块**。查知识库的工具不能放到 `ToolExecution`。
3. **不把会话历史写进知识库**。知识库是外部知识，会话是记忆，两者不能互相污染。
4. **降级不等于失败**：检索失败要退化成「无依据的结论」并明确标注，而不是让整条 run / 整个工作流红掉。

---

## 五、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

目前覆盖到的（`tests/AgentWorkbench.UnitTests/Embedding/`）：向量化实现的解析与降级行为。

应该补上的：

- 正例：给定固定语料，检索必须命中已知片段并带正确的文件名 / 页码。
- 负控：超出语料的问题必须走到「拒答」路径，而不是编一个答案。
- 度量型：固定语料上的召回率 / 命中率，改动检索策略时给出前后对比（不是阈值判定，是趋势）。

## 依赖

Knowledge.Abstractions、Persistence、Providers、Conversation（`IChatService` 供 RAG 问答）、
ToolExecution（仅工具扩展点契约）、Domain、Shared。


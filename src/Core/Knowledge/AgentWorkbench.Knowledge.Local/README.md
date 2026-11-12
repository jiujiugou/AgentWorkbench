# 本地知识引擎（Knowledge.Local）

> 校准基线：`b4d5f2c`。本文只写判断与边界；接口、成员、默认值一律以代码为准，
> 用 `find src/Core/Knowledge/AgentWorkbench.Knowledge.Local -name '*.cs'` 取清单。

## 这一格是什么

**检索执行不在这里，判定和证据留在这里。**

2026-11-10 起 C# 不再做召回：模型调 `mcp__rag__search_knowledge`（Python MCP server，
主仓 `tools/mcp-rag/`，由 `Mcp:Servers` 拉起），它只读 C# 写的那份索引。

这不是性能选择，是产品判断：**检索是工具、是机制，不该进 Core；判定是判据，必须留在持有状态的一侧。**
按 ADR 0013，检索产生的证据快照也是状态——原文被删后，历史答案要能复核。

## 本模块还负责什么（判断，不是清单）

- **索引写入**：文档管道产出的 chunk 写进 FTS5 与向量索引。Python server 以 `mode=ro` 读它，
  两边共用一份才不会漂移。删掉写入，工具查的就是一个不再更新的死库。

- **引用判定**：校验模型标的 `[n]` 是否真存在于这次检索结果里、依据不足时拒答。
  MCP server 只返回 hits，它不替你判「编号有没有编造」。

- **证据链**：来源是审批卡片上「依据哪条规则」的数据源，必须落在 C#。

## 红线

1. **判定不许下沉到 Python**。可以在 server 里算分数，不能在 server 里算 verdict。
2. **MCP 工具走直调路径**，不过四道闸门、不入 `action_trace`；因此工具返回后必须把 hits
   写成证据快照，否则判据②「依据哪条规则」和审批卡片当场就断。
3. **索引写入只能由文档管道触发**，检索侧只读。
4. **降级不等于失败**：检索失败要说「没查到」，不许把「查不到」当成「没有」——
   这和 ERP 连不上时不能把「查不到」说成「没有这条记录」是同一条原则。
5. **不把会话历史写进知识库**。知识库是外部知识，会话是记忆，两者不能互相污染。

## 已知边界（不是完成态）

Python server 目前只跑 bigram 全文路、返回 `textScore`，**没有向量距离**；
`SufficiencyGate` 因此实际只命中 `no_candidates`，其余判定落在 `undetermined` / `no_vector`，
不会触发否决。等 Python 侧提供距离，C# 判定代码不用改就会恢复。
Py 仓 `freshness.py` 那条「索引过期不许用空结果冒充没有资料」也还没接。

删掉 `KnowledgeController` 之后，「先清旧向量再重建索引」失去唯一入口：
`DocumentsController` 的 reparse 只重新入队、不清旧向量，换 embedding 模型后会留孤儿向量。
这条与上一节第 4 条同源，归 M2 之前处理。

## 怎么验

- 判定型：引用越界必须被 `CitationVerifier` 抓到（正例绿 / 负控红）；零命中时 `McpKnowledgeVeto` 必须终止生成。
- 度量型：固定语料上的召回率与命中率，改检索策略前后对比，**不做阈值判定**（检索质量是概率判据）。
- 运行级：`node scripts/probe-hub.mjs "<涉及企业规章的问题>"`，看 `[Sources]` 与 `[Citations]` 是否都出。

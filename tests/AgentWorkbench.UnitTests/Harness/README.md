# Harness/ —— 集成层（不是单元测试）

本目录**属于集成层**，尽管它住在 `AgentWorkbench.UnitTests` 工程里。

判断一个用例属哪一层，只看它**隔离了什么**，不看它住在哪个工程：

- 本目录装配**真实生产链路**（`AgentHarnessService` + `ToolExecutionPipeline` + `AgentLoopService` + 真实会话/记忆），
  只把**模型与网络**换成替身 → **集成层**。
- 工程内其余目录（`Tools/` `Memory/` `Embedding/` `Conversation/` `ModelRuntime/` `Loop/` `Agent/` `Workflow/`）才是**单元层**。

> 注意 `Loop/`：它直接调 `AgentRunGuard.InspectToolCall` / `InspectCompletion`，是纯单元层。
> 它与本目录构成「同一不变量的单元 + 集成」双层，**不是冗余**——那是金字塔。

## 与 `Evaluation.AgentTask` 的边界

两者**同层**（两处 `Build()` 逐参数比对：11 项 9 项逐字相同），靠**用途**区分：

| 工程 | 唯一职责 |
|---|---|
| **本目录** | 单条 run、深断言、快反馈 —— 集成回归 |
| `tests/AgentWorkbench.Evaluation.AgentTask` | 批量驱动、汇总、失败路径入容器、轨迹回放不重跑 |

**因此：单条 run 的结局正负控唯一归属本目录**，不要再写到 `AgentTask`。

依据：`docs/测试标准/测试分层职责.md`

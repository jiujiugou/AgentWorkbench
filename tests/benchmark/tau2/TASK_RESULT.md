# τ²-bench × AgentWorkbench 验证任务执行结果（仅失败项）

---

## V2 — `strict_replay` 会不会炸？

**结论**: FAIL（会炸）

**根因**: 首次模拟运行已修改数据库（订单状态变为 "return requested"），评分器重放时通过 `environment.set_state()` 重建环境，但**没有重置数据库**。重放执行工具时环境状态不同，导致工具输出不一致。τ²-bench 的 Environment 无自动快照/回滚机制。

**关键证据**:
```
strict_replay=True FAILED: ValueError: Tool call returned different content than recorded ToolMessage

Recorded ToolMessage: "Error: Non-delivered order cannot be returned"
Replayed ToolMessage: 成功的返回结果 (完整订单 JSON)
```

**影响**: 同一个 environment 实例跑多次 simulation，第 2 次评分时 strict_replay 必挂（除非任务不改 DB）。CI/验收流程中，每个任务必须用全新的环境实例，或在评分前显式重置 DB。

---

## V8 — MockLlm 现状盘点与回放方案

**结论**: ~~FAIL（现状不满足回放需求，需重写/扩展）~~ → **已解决（2026-10-14）**：
`Program.cs` 已重写，下面「逐字段缺口表」里的每一项均已补齐，探针 `probes/mockllm_replay_probe.py`
21 项断言通过（含全新进程负控）。此节保留作为设计依据的存档。

### 逐字段缺口表

| 组件 | 现状 | 需要 | 影响 |
|------|------|------|------|
| `ChatCompletionMessage` DTO | 只有 `role`、`content`、`tool_call_id` | + `tool_calls` (List<ToolCall>) | 无法解析历史中的 assistant tool_calls |
| `ToolCall` 结构 | 响应里手工构造匿名对象 | 统一 DTO：`id, type, function: {name, arguments}` | 便于请求/响应双向复用，支持多工具 |
| 场景机制 | 4 个硬编码场景 | 新增 `replay` 场景：按 fixture 按轮次回放 | 支持确定性回放 |
| harness-loop 逻辑 | 只检查 `!hasToolResult`，有 tool 结果仍返回 tool_call | 第 N 轮精确控制 | 当前无法自然结束 |
| 多 tool_calls 支持 | 完全不支持 | 响应/流式均支持数组 | τ²-bench 真实轨迹含多工具 |
| 流式 tool_calls | 写死 `index=0`，仅单工具 | `index` 递增，支持多工具并发流式 | OpenAI 协议合规 |
| Fixture 格式 | 无 | JSONL：每行 `{turn_index, response_type, payload}` | 可版本化、可审查 |
| 控制面 | 仅 scenario 切换/计数/重置 | + fixture 上传/下载、当前轮次查询、序列校验 | 便于自动化测试 |

### 实测关键失败行为

1. **harness-loop 场景**: 第 2 调(含 tool 结果)仍返回 tool_call，不转文本
2. **多 tool_calls**: normal 场景直接忽略历史中的多工具调用，返回文本
3. **请求 DTO 缺 tool_calls**: 无法判断上一轮是否已调工具

---

## V9 — Webapi 有没有可复用的「单步模型调用」入口？

**结论**: 无现成单步入口，需新增

### 现有端点 vs 需求

| 现有端点 | 类型 | 缺口 |
|----------|------|------|
| `POST /api/harness/run` | 完整 run 级 | 含 AgentLoop 多轮循环、工具执行、RAG、记忆、轨迹落库 |
| `ChatService.StreamAsync` | 内部流式 | 非 HTTP 端点，且仍是完整对话流 |

### τ²-bench 适配器需要的 step 级接口

给 `messages[] + tools[]` → 返回 **单条** assistant 消息（文本或 tool_calls），**不执行工具**、**不循环**、**不落库**。

### 决策建议：新增 `ModelInvocation` 模块

**必须同步的 4 个架构测试文件**：

| 文件 | 修改点 |
|------|--------|
| `TestArchitecture.cs` | `AssemblyNames` + `"AgentWorkbench.ModelInvocation"` |
| `LayeringTests.cs` | `infrastructure` 正则 + `"^AgentWorkbench\\.ModelInvocation"` |
| `ModuleCycleTests.cs` | `expected` 数组 + `"ModelInvocation"` |
| `AgentWorkbench.slnx` | 新增项目引用 |

### 新增文件清单

| 文件 | 说明 |
|------|------|
| `src/AgentWorkbench.ModelInvocation/ModelInvocation.csproj` | 新项目 |
| `src/AgentWorkbench.ModelInvocation/IModelInvoker.cs` | 契约 |
| `src/AgentWorkbench.ModelInvocation/ModelInvokerService.cs` | 实现 |
| `src/AgentWorkbench.ModelInvocation/ModelInvocationServiceCollectionExtensions.cs` | `AddNitroModelInvocation` |
| `src/AgentWorkbench.Webapi/Controllers/ModelInvocationController.cs` | `POST /api/model/invoke` |

### 单步调用契约草案

```csharp
// Request
public sealed record InvokeRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AITool>? Tools,
    ChatOptions? Options);

// Response
public sealed record InvokeResponse(
    ChatMessage Message,          // 单条 assistant 消息（文本 或 tool_calls）
    ModelUsageSnapshot Usage,
    string Model);
```

---

## 后续行动优先级

1. **V9 先行**：新增 `ModelInvocation` 模块（解除 V10/V11 阻塞）
2. **V8 重写**：按 `mockllm_report.md` 方案实现 fixture 回放
3. **V2 规避**：确定性跑每个任务用全新环境实例，或评分前显式重置 DB
# V9 — Webapi 有没有可复用的「单步模型调用」入口？

**结论**: 无现成单步入口，需新增

---

## 1. 现有端点清单

| Controller | 端点 | 入参 | 出参 | 用途 |
|------------|------|------|------|------|
| `HarnessController` | `POST /api/harness/run` | `HarnessRunBody(SessionId?, Input, UseKnowledge?)` | `HarnessRunResult(RunId, SessionId, Text, Iterations, Model, LatencyMs, Steps[])` | **完整 run**：含循环、工具执行、RAG、记忆、轨迹落库 |
| `HarnessController` | `GET /api/harness/health` | — | `HarnessCapabilities` | 探活/能力位 |
| `HarnessController` | `GET /api/harness/runs/{id}` | `id: long` | `HarnessRunResult?` | 查历史 run |
| `ConversationsController` | CRUD `/api/conversations` | — | 会话/消息列表 | 通用聊天上下文持久化 |
| `ChatService` (内部) | `StreamAsync(history, userMessage)` | `IReadOnlyList<ChatTurn>, string` | `IAsyncEnumerable<string>` | 流式聊天，**非直接 HTTP 端点** |

---

## 2. `AgentHarness.RunAsync` 做了什么（单步缺什么）

```csharp
// AgentHarnessService.RunAsync 核心流程：
1. Resolve provider endpoint
2. _runtime.Wrap(factory.GetClient(endpoint))           // 包装：超时/重试/闸门/用量
3. _pipeline.Create(chatClient, ToolExecutionLimits)   // 工具执行管道
4. BuildAgent(execution.Client)                         // ChatClientAgent + Tools
5. 知识检索 + Context 组装 (可选)
6. _loop.ExecuteAsync(agent.RunAsync(...))             // **多轮循环**（含工具调用、重试、预算）
7. 轨迹落库 + 返回聚合结果
```

**关键差异**：
- `RunAsync` 是 **run 级**（含 `AgentLoop` 多轮迭代、工具执行、上下文组装、记忆、轨迹）
- τ²-bench 适配器需要 **step 级**：给 `messages[] + tools[]` → 返回 **单条** assistant 消息（文本或 tool_calls），**不执行工具**、**不循环**、**不落库**

---

## 3. 决策建议：扩展 `AgentHarness` vs 新增模块

| 维度 | 方案 A：扩展 `AgentHarness` + `IAgentHarness` | 方案 B：新增 `ModelInvocation` 模块 |
|------|-----------------------------------------------|--------------------------------------|
| **新增代码量** | 少：接口 + 1 方法 + 1 端点 + 复用现有依赖 | 多：新 csproj + 接口 + 实现 + 端点 + DI 注册 + 架构测试同步 |
| **依赖方向** | Webapi → AgentHarness（现有） | Webapi → ModelInvocation（新增） |
| **职责边界** | ⚠️ 模糊：Harness 本意是「编排门面」，单步模型调用属于「模型运行时」范畴 | ✅ 清晰：ModelRuntime 已有「统一调用语义」，单步调用是其自然延伸 |
| **复用性** | 仅 Harness 内部可用 | 可供 Conversation、Workflow、AgentHarness、外部适配器共用 |
| **架构测试影响** | 无新模块，仅接口扩展 | **需同步 4 个文件**（见下） |
| **推荐度** | ⭐⭐ (快但混职责) | ⭐⭐⭐⭐⭐ (长期正确，符合模块化单体设计) |

**推荐：方案 B（新增 `ModelInvocation` 模块）**

理由：
1. 符合「新增能力 = 新增模块」架构原则
2. `ModelRuntime` 已提供 `IChatClient` 统一包装，单步调用是其「原子操作」出口
3. 避免 `AgentHarness` 变成大杂烩（已有 Loop/Context/Memory/Tools/Trace）
4. 便于 τ²-bench 适配器直接依赖 `IModelInvoker`，不经过 Harness 编排逻辑

---

## 4. 方案 B 实施清单（若采纳）

### 新增文件
| 文件 | 说明 |
|------|------|
| `src/AgentWorkbench.ModelInvocation/ModelInvocation.csproj` | 新项目，引用 `ModelRuntime`、`ToolExecution`、`Shared` |
| `src/AgentWorkbench.ModelInvocation/IModelInvoker.cs` | 契约：`Task<ChatResponse> InvokeAsync(InvokeRequest, CancellationToken)` |
| `src/AgentWorkbench.ModelInvocation/ModelInvokerService.cs` | 实现：`_runtime.Wrap` → `chatClient.GetResponseAsync(messages, tools)` |
| `src/AgentWorkbench.ModelInvocation/ModelInvocationServiceCollectionExtensions.cs` | `AddNitroModelInvocation` |
| `src/AgentWorkbench.Webapi/Controllers/ModelInvocationController.cs` | `POST /api/model/invoke` |
| `tests/benchmark/tau2/probes/modelinvocation_fixture.jsonl` | 测试 fixture（可选） |

### 必须同步的架构测试文件（4 处）
| 文件 | 修改点 |
|------|--------|
| `TestArchitecture.cs` | `AssemblyNames` 数组 + `"AgentWorkbench.ModelInvocation"` |
| `LayeringTests.cs` | `infrastructure` 正则 + `"^AgentWorkbench\\.ModelInvocation"` |
| `ModuleCycleTests.cs` | `expected` 数组 + `"ModelInvocation"` |
| `AgentWorkbench.slnx` | 新增项目引用（或 `dotnet sln add`） |

### 依赖图（新增）
```
Webapi → ModelInvocation → ModelRuntime, ToolExecution, Providers, Shared
```

---

## 5. 单步调用契约草案

```csharp
// Request
public sealed record InvokeRequest(
    IReadOnlyList<ChatMessage> Messages,      // 完整历史（含 tool 结果）
    IReadOnlyList<AITool>? Tools,             // 可用工具（可为空）
    ChatOptions? Options);                    // Temperature/MaxTokens 等

// Response
public sealed record InvokeResponse(
    ChatMessage Message,                      // 单条 assistant 消息（文本 或 tool_calls）
    ModelUsageSnapshot Usage,                 // 本次用量
    string Model);                            // 实际模型名
```

---

## 6. 给 τ²-bench 适配器的建议

- 适配器应指向 `POST /api/model/invoke`（新端点），**不指向 `/api/harness/run`**
- 适配器自己维护会话历史、工具执行（由 τ²-bench orchestrator 做）、终止判断
- `.NET` 侧只负责：「给 messages+tools → 模型 → 返回 1 条消息」

---

## 7. 结论

- **现状**：无单步端点，`HarnessController.Run` 是完整 run 级编排
- **决策**：新增 `ModelInvocation` 模块（符合架构原则，职责清晰，复用面广）
- **工作量**：~6 个新文件 + 4 处架构测试同步，约 2-3 小时
- **阻塞 V10**：V10 依赖 V9 确定接口形状，再做隔离验证
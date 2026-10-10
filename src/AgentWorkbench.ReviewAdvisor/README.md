# AgentWorkbench.ReviewAdvisor

P3 审查 Agent 模块（Agent Loop）：把 P2 产出的每条问题清单，交给一个**受限 Agent** 在只读工具白名单内取证，
产出**带可核对引用**的结论与补正建议；证据不足则明确降级为「资料不足」。

## 功能

| 文件 | 说明 |
| --- | --- |
| `ReviewAdvisorService.cs` | 逐 finding 编排：初筛证据 → 有证据才进 Agent Loop → 汇总结论 + 轨迹落库；`NoEvidence` 短路（不调用模型） |
| `Agent/ReviewAgentLoop.cs` | **受限 Agent Loop**：注册 3 个只读工具，模型迭代取证直到给出 JSON 结论或触及步数预算；越权工具终止并留痕 |
| `Agent/AdvisorAgentModels.cs` | `AdvisorConclusionDraft`、`AdvisorStepDraft`、`AdvisorAnswer`（模型 JSON 契约）、`AdvisorPrompts`（系统提示词 + `Version`） |
| `Evidence/EvidenceLocator.cs` | **程序化**证据校验：引用 chunk 是否真实存在、evidence 是否原文命中（`IsGrounded`）、关键词定位 |
| `AdvisorWorker.cs` + `AdvisorRunQueue.cs` | 后台单读者队列（`IAdvisorRunQueue`） |
| `AdvisorModels.cs` | 能力位 / 视图 / 异常（未配模型、run 未就绪） |
| `ReviewAdvisorServiceCollectionExtensions.cs` | 自注册入口 `AddNitroReviewAdvisor` |
| Webapi `AdvisorController` | HTTP 面：`api/reviewadvisor/health` · `POST/GET review/runs/{id}/advisor` · `conclusions` · `advisor/trace` |

## 接口

| 接口 | 成员 | 实现 |
| --- | --- | --- |
| `IReviewAdvisorService` | `GetCapabilitiesAsync`、`CreateJobAsync`、`GetJobAsync`、`GetConclusionsAsync`、`GetStepsAsync`、`ExecuteAsync` | `ReviewAdvisorService` |
| `ReviewAgentLoop`（具体类，非接口） | `RunAsync(finding, context, seedObservations, maxSteps)` | 本模块 |
| `IAdvisorRunQueue` | `EnqueueAsync`、`ReadAllAsync` | `AdvisorRunQueue` |
| `IChatClientProvider`（Agent） | `GetClientAsync` | 需要原生 function calling，故不走向结构化输出 |
| `IAdvisorRepository`（Persistence） | job / conclusions / steps 读写 | — |

**Loop 内置只读工具**（`ReviewAgentLoop.BuildTools`）：`retrieve_evidence(query)` · `read_chunk(seq)` · `locate_evidence(keyword)`，
全部限定在**当前文档**范围内。

## 框架提供 vs 自研

| 机制 | 来源 | 位置/说明 |
| --- | --- | --- |
| `FunctionInvokingChatClient` 原生函数调用环 | **MEA** | `ReviewAgentLoop.RunAsync` |
| `MaximumIterationsPerRequest`（步数预算=循环上限） | **MEA** | 由 `maxSteps`（默认 6）注入 |
| `TerminateOnUnknownCalls`（未知/越权工具即终止） | **MEA** | 白名单工具外的调用触发终止 |
| `AllowConcurrentInvocation=false`、`FunctionInvoker` 回调 | **MEA** | 串行执行 + 轨迹采集 |
| `AIFunctionFactory.Create` 生成工具 Schema | **MEA** | `BuildTools` |
| `FunctionCallContent` / `ChatResponse.Messages` 解析未完成调用 | **MEA** | `LastFunctionCalls` |
| 工具**白名单**（只注册 3 个 + `AllowedTools` 二次校验） | 自研（策略） | 白名单是「只注册」+「比对 `AllowedTools`」双保险 |
| **无证据短路**（检索/定位都没命中 → 不进模型，直接 insufficient） | 自研（程序保证） | `ReviewAdvisorService.GatherInitialEvidenceAsync` + `NoEvidence` |
| **引用后置校验**（结论引用必须能在真实 chunk 中原文命中，否则降级） | 自研（程序保证） | `EvidenceLocator.IsGrounded` + `Finalize` |
| 预算耗尽/越权/输出不可用 → 统一 `insufficient` 结论 | 自研（策略） | `Insufficient(...)` |
| 宽松 JSON 解析（截取首个 `{` 到末个 `}`，失败即降级） | 自研 | `TryParse` |
| 逐 finding 编排、结论/轨迹落库、job 状态机 | 自研 | `ReviewAdvisorService` |
| 提示词（含「不得给出法律意见」约束）与版本号 | 自研 | `AdvisorPrompts`（P4 起由 Workflow 的 `IPromptStore` 版本化） |

> 关键区分：**框架保证「循环能跑、越权能停」，本模块保证「无证据不下结论、有结论必有原文引用」。**

## 依赖

Review、Agent、Knowledge.Abstractions、Persistence、Domain、Shared。
（MAF/MEA 类型经 Agent 模块传递引用，本模块 **csproj 未直接引用** `Microsoft.Agents.AI`。）

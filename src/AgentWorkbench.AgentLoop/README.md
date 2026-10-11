# AgentWorkbench.AgentLoop

执行控制模块（功能域 3）：一次 Agent 运行的**预算与超时**。
迭代循环本体由 MAF 的 `FunctionInvokingChatClient` 承担（见 `ToolExecution`），
本模块只回答「允许循环多久、允许循环几次」。

## 功能

| 文件 | 说明 |
| --- | --- |
| `IAgentLoop.cs` | 契约：`MaxIterations`、`TimeoutSeconds`、`ExecuteAsync<T>(action, ct)`；超时抛 `AgentRunTimeoutException`（继承 `TimeoutException`，便于区分「整轮跑太久」与「单次请求太久」） |
| `AgentLoopService.cs` | linked CTS + `CancelAfter(TimeoutSeconds)` 执行传入动作，超时统一转译 |
| `AgentLoopOptions.cs` | 配置节 `Harness:Loop`：`MaxIterations=6` / `TimeoutSeconds=30` |
| `AgentLoopServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroAgentLoop` |

## 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IAgentLoop` | `MaxIterations`、`TimeoutSeconds`、`ExecuteAsync<T>` | `AgentLoopService` | AgentHarness `RunAsync`（`MaxIterations` 传给 `ToolExecutionLimits`） |

> 未落地（缺口清单 P1-1 / P1-2）：重复调用检测、token/次数预算 —— 当前只有总数上限与墙钟超时。

## 依赖

Shared。

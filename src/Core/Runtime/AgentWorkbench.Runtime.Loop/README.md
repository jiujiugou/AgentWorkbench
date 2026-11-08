# 执行控制模块

这个模块只干一件事：**管住一次 Agent 运行的「停」** —— 允许循环几次、允许循环多久，什么时候必须提前停，
以及停下来之后**以什么状态收尾**。

它不做执行循环本身。模型说要调工具、工具结果回填、再问模型，循环往复直到模型给出答案 —— 这套东西框架里已经有了
（MEA 的 `FunctionInvokingChatClient`，由工具调用模块装配）。底座不重写一遍。
这个模块只管这几件事：**循环几次、循环多久、重复调用要不要掐、token 烧多少要掐、跑满算不算答完**。

---

## 一、现在已经具备的能力

代码就这几个文件：契约 `IAgentLoop.cs`、实现 `AgentLoopService.cs`、环内守卫 `AgentRunGuard.cs`、
配置 `AgentLoopOptions.cs`、自注册 `AgentLoopServiceCollectionExtensions.cs`（`AddNitroAgentLoop`）。
守卫用的词表（终态 / 归类码 / 守卫契约 / 用量口径）放在 `Shared/Execution/`，
这样工具调用模块和编排层都能引用它，本模块不必反向依赖它们。

### 循环归谁管

- 循环本体不是本模块的。它在 MEA 的 `FunctionInvokingChatClient` 里，由 `ToolExecution` 模块装配
  （见那份 README 的「一次调用怎么落地」）。
- 对外只有一个契约 `IAgentLoop`，四个成员：`MaxIterations`、`TimeoutSeconds`、`CreateGuard(usage)`、
  `ExecuteAsync<T>(action, ct)`。前两个回答「允许循环几次、多久」，第三个给这一次 run 发一个环内守卫，第四个圈住整轮。
- 谁在问：`AgentHarness` 的 `RunAsync`。它把 `MaxIterations` 交给工具模块当工具环预算，
  用 `TimeoutSeconds` 圈住整轮，并在开跑前 `CreateGuard`（把模型用量的读数接进去）。

### 允许循环几次

- 默认 6 轮，配置节 `Harness:Loop:MaxIterations`。
- 这个数字不在这儿生效：`AgentHarness` 把它读出来，作为 `ToolExecutionLimits(MaxIterations)` 传给工具模块，
  最终落到 MEA 的 `MaximumIterationsPerRequest`。本模块只负责把它如实报出去。
- 实测过（脚本化模型，预算 2）：一共 3 次模型请求 = 2 轮工具调用 + 1 轮收口；最后一轮框架会把 `tools`
  清空，逼模型必须用文本收尾，然后**正常返回**。
- 跑满不等于答完：正常返回不等于答完了。守卫在整轮返回后再问一次（`InspectCompletion`）：
  最后一次工具调用的轮次已经顶到 `MaxIterations - 1`，说明模型还想再要一轮而框架已经不给工具了 ——
  这就是「被截断」，标 `iteration_limit`；模型自己提前改用文本收尾则标 `succeeded`。
  事先不问，两者在轨迹里长得一模一样。

### 允许循环多久

- 默认 30 秒，配置节 `Harness:Loop:TimeoutSeconds`。
- 实现是 linked CTS + `CancelAfter(TimeoutSeconds)`：调用方自己的令牌会被链进来，
  所以「调用方取消」和「本模块超时」都能触发，二者在 catch 里靠 `cancellationToken.IsCancellationRequested` 区分。
- 超时统一转成 `AgentRunTimeoutException`（继承 `TimeoutException`，消息里带上秒数），
  好和「单次模型请求太久」（模型交互模块抛的 `TimeoutException`）区分开。
- 想精确区分这两种超时，得先 `catch (AgentRunTimeoutException)`；泛 `catch (TimeoutException)` 会把两种一起吞掉。
- 取消是原样冒泡的：调用方主动取消时抛 `OperationCanceledException`，本模块不转译、不吞。

### 卡在同一个调用上：重复调用检测

- 指纹 = 工具名 + 参数文本。参数文本在工具模块序列化时按参数名排过序，键序稳定，才可比。
- **只统计「连续」重复**：中间夹了别的调用就重新计数，避免误伤正常的多轮调用。
- 连续重复达到 `Harness:Loop:MaxRepeatedToolCalls`（默认 3）即终止，标 `loop_detected`；`0` = 关闭检测。

### 管住钱：token 预算

- 口径 = 本 run 的输入 + 输出 token 增量。守卫在构造时取一次用量基线，之后读「当前值 − 基线」。
- 超过 `Harness:Loop:MaxTokensPerRun` 即终止，标 `budget_exceeded`。默认 0 = 不限制（需要限额的宿主自己给数字）。
- 用量读数由编排层接过来（AgentHarness 同时依赖本模块与模型交互模块），本模块只看
  `Shared/Execution/IAgentUsageSource`。

### 判定点放在哪

- 环内、**工具调用真正执行之前**：`IAgentRunGuard.InspectToolCall` 由工具模块在函数调用钩子里调用；
  返回非空就跳过这次调用、把原因记在轨迹里，并让框架干净收口（不是抛异常 —— 抛异常会被框架当成「工具失败」喂回模型）。
- 这一点很关键：重复调用和 token 消耗的信息只有到环内、工具调用发出时才有；事后追认等于钱已经烧了。
- 判定必须是**真停**：停下来的理由会一路带到 `AgentHarness`，落进 `ExecutionTrace`。

### 停下来之后：终态与归类码

- 状态（`agent_runs.status`，≤16 字符）回答「以什么状态结束」：`succeeded` / `iteration_limit` / `loop_detected` /
  `budget_exceeded` / `timeout` / `cancelled` / `failed`。
- 归类码（`agent_runs.error_code`）回答「为什么」：`loop_detected` / `budget_exceeded` / `iteration_limit` /
  `run_timeout` / `model_timeout` / `cancelled` / `model_unavailable` / `tool_error` / `unhandled_error`。
- 词表与守卫契约在 `Shared/Execution/`：`AgentRunStatus`、`AgentRunCode`、`AgentRunStop`、
  `ToolCallObservation`、`IAgentRunGuard`、`AgentUsage` / `IAgentUsageSource`。
- 「判」在本模块，「归类落库」由编排层（AgentHarness）做 —— 只有一串人话的失败，调用方没法决定重试还是降级。

### 还是没有的

- 暂停 / 人工确认：没有。本模块只有「继续跑」和「停」两种结果。
- 单个工具自己的时间上限：没有（那是工具模块的事），本模块只圈整轮。
- 并发 run 的 token 口径：守卫本身按 run 独立，但用量来源是进程级计数器，串行跑才准
  （见 AgentHarness 的 `RuntimeUsageSource`）。

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| 模型卡在同一个调用上反复循环 | 白烧钱，跑到预算用光才算完 | 已做：`AgentRunGuard` 在环内判「同工具 + 同参数」连续重复（默认 3 次） | 重复调用到阈值即终止，状态标 `loop_detected` |
| 预算只管墙钟，管不住钱 | 一次任务能把 token 烧光而「没超时」 | 已做：`MaxTokensPerRun`，守卫读用量增量；默认关（0），需限额的宿主给数字 | 超预算即终止且状态标 `budget_exceeded`，不是 `succeeded` |
| 跑满迭代和正常收尾分不清 | 事后看不出「是答完了还是被截断了」 | 已做：收尾问 `InspectCompletion`，顶到最后一轮标 `iteration_limit`；模型主动收尾标 `succeeded` | 两种收尾在轨迹状态上可区分 |
| 失败只有一坨字符串 | 无法据此决定重试还是降级 | 已做：`AgentRunCode` 归类码，落 `agent_runs.error_code`（迁移 M022） | 超时 / 预算 / 取消 / 循环各有独立标识 |
| 取消也落 `failed` | 分不清「用户停了」和「跑挂了」 | 已做：调用方取消落 `cancelled` + `cancelled`（不是 `failed`） | 取消落独立的 `cancelled` |

---

## 三、加一项执行控制要做三件事

1. **加一个配置项**：`AgentLoopOptions` 上加字段和默认值（配置节 `Harness:Loop`）。
2. **在 `AgentLoopService` 里判定它**：能拦在 `ExecuteAsync` 里的（墙钟、外部信号）就在这儿拦；
   要靠循环内信息才能判的（重复调用指纹、token 用量），得让下游把信号递上来，
   或者回到 `FunctionInvoker` 那一层拦。
   （本轮落地用的就是「把信号递上来」：`AgentRunGuard` 实现 `IAgentRunGuard`，工具模块在 `FunctionInvoker` 里问它，
   编排层负责装配。）
3. **让结果能落进轨迹**：判定完必须能表达成「以什么状态结束的」，并让 `AgentHarness` 落进
   `ExecutionTrace.Status`。做不到第 3 步，前两步等于没做 —— 停了但没人知道为什么停，比不停更难查。

---

## 四、红线

1. **不重写框架已有的东西**。循环本体、工具调用协议、最后一轮的收口请求都是框架的。
   动手前先问：框架能不能配出来？能就别写。
2. **零垂直业务语义**。底座代码里不出现合同、工业、仓储、单证这类词。
3. **依赖单向**：本模块只依赖 Shared，不反向依赖 Harness / ToolExecution / ModelRuntime
   （模块间不出现环由架构测试强制）。
4. **超时必须是真取消**，不能只是外层等一个时间然后放弃 —— 令牌要真的传进正在跑的动作里。
5. **停下来了就不能记成成功**。「预算用光」和「答完了」是两件事，轨迹状态必须分得开。

---

## 五、怎么算验收通过

判定型的用例必须**正例能绿、负控能红**；能靠脚本判定的，禁止用 AI 来判。

目前覆盖到的：

- `tests/AgentWorkbench.UnitTests/Loop/AgentLoopTests.cs`（3 条）：正常返回（动作返回什么就传出来什么，
  `MaxIterations` / `TimeoutSeconds` 如实暴露）；超时（抛 `AgentRunTimeoutException`，秒数对得上）；
  取消（原样抛 `OperationCanceledException`，不被误判成超时）。
- `tests/AgentWorkbench.UnitTests/Loop/AgentRunGuardTests.cs`（10 条）：守卫本体 ——
  循环检测（连续同工具同参数到阈值即停；**负控**：关检测 → 永不停；非连续重复不停；同工具换参数不算重复）、
  token 预算（超预算即停；**负控**：预算关掉或没有用量来源 → 永不停；口径只算本 run 增量，前一次 run 的消耗不算）、
  收尾分类（最后一轮还在要工具 → `iteration_limit`；**负控**：模型提前收尾、或一次工具都没调，都不算跑满）。
- `tests/AgentWorkbench.UnitTests/Harness/AgentHarnessExecutionControlTests.cs`（12 条）：端到端到落库 ——
  重复调用 → `loop_detected`（被拦的那次也留轨迹）；**负控**：关检测 → 跑满 `MaxIterations` 标 `iteration_limit`；
  超预算 → `budget_exceeded`；**负控**：关预算 → 跑满标 `iteration_limit`；跑满 → `iteration_limit`（不是 `succeeded`）；
  模型收尾 → `succeeded` 无归类码；取消 → `cancelled` 且取消真的穿进工具内部；
  连续工具失败 → `tool_error`；run 墙钟超时 → `run_timeout`；未配模型 → `model_unavailable`。

还没覆盖的（对应总验收表 B2 / B6）：

- 迭代预算的轮数对账（预算 2 → 恰好 2 轮）目前只在循环检测 / 收尾分类的用例里间接覆盖，没有独立用例。
- 取消穿透只在编排层验过；单个工具「收到取消也不理」（第三方阻塞调用）时怎么强制隔离仍未做。

## 依赖

Shared。

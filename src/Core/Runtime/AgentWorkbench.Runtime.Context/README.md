# 请求装配模块（Runtime.Context）

- **编译单元**：`AgentWorkbench.Runtime.Context.csproj`（net10.0）
- **依赖**：`Shared` + `Microsoft.Agents.AI` / `Microsoft.Extensions.AI` 抽象。**不反向依赖** `Runtime.Harness`、
  `Runtime.Session`、`Memory.Persistence`，也不认识工具 / 模型实现。
- **所属部件**：① 请求装配。部件的职责边界（管什么 / 不管什么 / 怎么算验收）见 `docs/core/请求装配.md`；
  本文件只解释「这个编译单元为什么这么切」，不复制部件文档。

## 一、为什么独立成这个 csproj

一次 run 里「模型被调用之前、且不属于工具往返」的那一段，需要一个**不碰存储、不碰业务**的落点。
它夹在编排层（知道本次 run 的输入）和框架（知道怎么发请求）之间：既不能放进 `Harness`（会混进编排语义），
也不能塞给 `Session` / `Persistence`（那是「历史从哪来」，不是「这一轮要发什么」）。

历史上消息组装散在三处（Harness 自己 `new ChatMessage`、`ChatService.BuildMessages`、
Workflow `AgentStepExecutor` 拼大字符串），规则各不相同。现在收成一个编译单元，
业务语义（哪些内容该进来）仍留在调用方。

## 二、和 MAF 的分工

MAF 1.23 / MEA 已提供三样机制，按「绝对不造轮子」直接用，不再自写等价物：

| 需求 | 用框架的什么 | 本模块做什么 |
| --- | --- | --- |
| 消息形态 | `ChatMessage` | 把本轮输入单独成一条 `user` 消息 |
| 指令注入 | `ChatOptions.Instructions` | 空指令返回 `null`，有内容压掉首尾空白 |
| 会话历史 | `ChatHistoryProvider` | 只转交 DI 注入的 provider，不决定存哪 |

**本模块只剩这两件判定**：本轮输入的消息形态、停用 / 空指令时不注入。其余都交给了框架。

两点必须说清：

- **不挂 `ChatClientAgentOptions.AIContextProviders`**，也不自写 `IChatClient` 装饰器。前者会让
  MEA 在我们装配的 `FunctionInvokingChatClient` 外面再包一层函数调用环，绕过 `ToolExecutionPipeline`
  的守卫与轨迹采集（实测：连续同参调用不再判 `loop_detected`、工具异常不再归类 `tool_error`）。
  结构门禁 `F38` 把这条钉住。
- **没有片段预算，也没有历史条数预算**。两者都曾以「调用方可传入」的形式存在，但生产没有任何调用方
  （`HarnessRunRequest` 既无 `Fragments` 也无 `History`），属于死配置、死代码，已于 2026-11-13 删除。
  片段注入建立那天再加回来，不预留字段。

## 三、红线

1. **零垂直业务语义**：不出现合同、工业、仓储、单证这类词（AC-02，`F37` 源码门禁）。
2. **职责单向**：只依赖 `Shared` 与 MAF / MEA 抽象；不读库、不检索、不认识 Harness 流水线。
3. **注入点不在这里**：`Compose` 只产出数据（`ContextPlan`），把 `Instructions` 放进
   `ChatOptions` 是 `Runtime.Harness/BuildAgent` 的事。本模块不包装任何 `IChatClient`。
4. **指令只设一次**：MAF 合并 agent 级 / run 级 `ChatOptions` 时，两级 `Instructions`
   是换行拼接而非覆盖，两处都设会让同一句指令重复进每轮请求。
5. **不预留没有调用方的字段**：加参数前先答「生产谁传它」，答不上来就不加。

部件文档、测试输出与 `agent-parts.toml` 各自独立，互不生成；本文件不列测试文件名与条数（会漂）。

# Core / Tools

- **适用侧**：底座 ｜ **寿命**：现状（就近维护）｜ **最后校准**：2026-11-09
- **对应代码**：`src/Core/Tools/`（Tools.Abstractions / Tools.Builtin / ActionGateway）+ `src/Core/Foundation/.../ActionStore/`
- **回答**：模型输出怎么变成一张结构化请求、四道网关在哪、落库与并发怎么保证、换域改什么
- **不回答**：当前域有哪些能力（各 `Domains/*/ActionSpecs`）、单模块内部实现（各 README）、界面怎么点

> **工具层只做一件事：把不可信的模型输出变成一份可校验的请求。** 它不认识任何能力名，也不判断业务含义。
> 与之相对，v1 知识问答那条链在 [runtime.md](runtime.md)；两者共用工具环，但主线不经过 Runtime。

## 1. 路由（先读这三个，别全仓 grep）

| 想知道 | 先读 |
| --- | --- |
| 模型怎么看到「能办哪些事」 | `Tools.Builtin/ProposeActionTool.cs` 的 `BuildDescription` |
| 模型现在看到哪个域的能力 | `Host/Actions/ActionServiceCollectionExtensions.cs` 的 `ResolveActionSet`（默认 Erp；`Actions:ActionSet` 可切 Fake / Gitea） |
| 四道校验的顺序与失败默认 | `ActionGateway/FailClosedActionGateway.cs` 头注释 |
| 挂起 / 审批 / 幂等 / 超时只查证 | `ActionGateway/ActionRunService.cs` 头注释 |

其余按需：`Tools.Abstractions/IAgentToolProvider.cs`（工具扩展点唯一契约）、`ActionGateway/README.md`（本层边界与待裁决）、`Persistence/ActionStore/`（落库实现）。

## 2. 链的形状（跨模块，单个 README 里没有的一段）

```
用户一句话
  → Hub / harness 工具环
  → propose_action(actionKey, argumentsJson)      模型的唯一出口：只填单，不执行
  → ActionRequest { subject 来自身份端口, arguments 原样透传 }
  → ActionRunService.SubmitDeferredAsync
  → 四道：Schema → Permission → BusinessRule → Audit   （任一不过 = 拒绝，执行体零调用）
  → RequiresApproval ? 挂起等人工 : 直接执行
  → 幂等领取（同一 requestId 只一次副作用）
  → IActionExecutor（动作域适配器，真正碰外部系统）
  → 超时/未知 → 只查证，绝不重放写动作
  → 审计与终态落 SQLite
```

分工表（**谁负责什么，比类名重要**）：

| 谁 | 在哪 | 只做什么 |
| --- | --- | --- |
| 端口形状 | `Core/Ports` | `ActionRequest` / `ActionSpec`、`IActionSubjectProvider`、`IActionRunStore` 等；零依赖、零业务词 |
| 翻译 | `Core/Tools/Tools.Builtin` | 读清单拼描述、参数只判格式、身份从端口取、调收单入口、只回结论码 |
| 判定 | `Core/Tools/ActionGateway` | 四道顺序、默认拒绝、挂起恢复、幂等、审计写通道 |
| 存储 | `Core/Foundation/Persistence/ActionStore` | SQLite 实现；幂等靠 `request_id` 主键唯一约束，不靠进程内字典 |
| 身份 | `Host/Actions/ActionSubjectProvider` | `AsyncLocal` 作用域 + HttpContext 回退；Hub 在整段生成期间 `Begin` |
| 业务 | `src/Domains/*` | 能力清单、描述、参数 Schema、审批标记、权限与规则、适配器 |

## 3. 请求号从哪来（幂等的父级）

去重表按 `requestId` 判重，**requestId 怎么来，直接决定幂等有没有用**。

```
requestId = SHA256( conversationId | clientMessageId | actionKey | 规范化参数 )[..32]
```

- 前三项来自宿主作用域（端口 `Core/Ports/ActionOriginProvider.cs`；Host 实现在 `Actions/ActionSubjectProvider.cs`，`AsyncLocal` 三元组，Hub 在**会话确定之后** `Begin`）。
- 参数先规范化再参与计算：对象键排序、字符串走序列化器转义。**同一组参数换个键序必须算出同一个号**，否则「原样重试」会因为格式差异被当成新请求。
- **`clientMessageId` 必填，由 Host 的三条 web 入口强制**（`Actions/InputIdentity.cs`）：SignalR 的 `ChatRequest`、HTTP 动作递单的 `ActionSubmitBody`、HTTP 直接起 run 的 `HarnessRunBody` 都要求非空，缺了直接拒绝、不起 run / 不进网关。**服务端现编的号对重试无效**——重发是另一次调用，只会再编一个新号（这里说的是动作判重，不代表对话 run 幂等，见下表）。
- 派生规则本身仍在 `Tools.Builtin/ActionRequestId.cs`（Core）。**`src/Core` 行为零改动**：Host 只补「必须先有身份」这一格，并复用同一套 Derive。HTTP 入口无会话，用归属 id 当隔离维度，且**不再接受客户端自报 `requestId` / `idempotencyKey`**。

> **边界（2026-11-09 决定）**：超时重试 = 至少一次投递，这是 web 传输模型带来的必答题，
> 解法**只放 Host**，不进 Core（换域 diff = 0，非 web 宿主也不必伪造输入号）。
> Core 里 `Derive` 的「缺省回退随机号」保留，从三条 web 入口都走不到；
> 但**绕过 Host 直接调 Core** 仍会触发回退——现存的一处是
> `Core/Orchestration/Workflow/Steps/AgentStepExecutor.cs`（它自己调 `IAgentHarness.RunAsync`，
> 不建 `IActionSubjectScope`）。契约与代价见
> `src/Host/AgentWorkbench.Webapi/README.md`「输入身份契约」。

**这把键挡得住什么、挡不住什么**（别夸大）：

| 情形 | 结果 |
| --- | --- |
| 模型在同一次输入里**原样**重试同一个动作 | 挡住：同号 → 去重表回放既有结果（`replayed_from_idempotency`） |
| 模型**改参数**重试 | 放行：参数变 → 新号。这是「修正」，不是「重复」 |
| 用户手点两次发送 | 放行：前端每次生成新 `clientMessageId`，那是两次真实输入 |
| 客户端断线补发（将来） | **挡不住整段 run**。复用同一个 `clientMessageId` 只能让动作请求号稳定、`action_idempotency` 回放既有结果；Hub 仍会再建会话、再落 user 消息、再起一次 run、再写历史与轨迹。当前前端没有自动重发；要开，须先在 Host 补输入级去重（键 = 认证主体 + 输入号，会话 id 此刻未定），且终态事件要带回输入号 |
| 同一 `clientMessageId` 重复调 `/api/harness/run` | 两次独立 run：本入口每次调用都现起 run，无会话时 sessionId 由 Host 现编 |
| 请求**不带** `clientMessageId` | **拒绝**：`client_message_id_required`，零 run、零网关调用 |
| HTTP 手工递单 | 现在由 Host 用同一套 Derive 派生；提交体只收 `ClientMessageId`，客户端不再自报判重键 |

## 4. 三条边界（破了等于把执行权交给模型）

1. **模型输出一律不可信**：只判「是不是合法 JSON 对象」，业务含义交给域注入的 Schema 校验。畸形 JSON 直接拒绝，不进网关。
2. **身份不从参数来**：`subject` 只从 `IActionSubjectProvider` 取；参数里塞 `subject` / `role` 一律忽略；取不到身份直接拒绝。
3. **只回结论码**：`status` / `requestId` / `code`，不回 `Data`、不回外部单号；四道顺序与失败默认不在这里重排。

> 判据③「拿掉模型，系统还能安全地拒绝」就落在这三条上：同一张手工填的单子递进网关，结论必须一致。

## 5. 坑（只给指针，不复制注释）

| 现象 / 约束 | 为什么 | 去哪核 |
| --- | --- | --- |
| 新工具「没生效」 | 白名单按名裁剪，**裁掉静默不报错**；现为 7 个（含 `propose_action`） | `Runtime.Tools/ToolCatalog.cs`；`appsettings.json` 的 `Harness:Tools:Whitelist` |
| 工具拿不到身份 | SignalR 长连接的后续调用不保证还有 HttpContext | `AgentHub.RunAsync` 的 `Begin`；`ActionSubjectProvider` 的注释 |
| 并发下重复执行 | 「先查再插」有竞态窗口 | `SqliteActionRunStore.TryClaimIdempotencyAsync`（`INSERT OR IGNORE` + 主键） |
| 审计写失败却放行 | 审计是第四道，写不进去必须拒绝 | `SqliteTraceStore.WriteAsync` 的 catch 分支 |
| 新库缺表 | DDL 有两处消费者，各写一套必分叉 | `ActionStoreSchema.Statements`（唯一事实源）；迁移 `M030_ActionRunStore` |
| 工具描述里出现业务判断 | 描述来自域注入的数据，底座只拼接 | `ProposeActionTool.BuildDescription`；AC-02 禁词门禁 |
| 请求号每次都不一样 | 工具里随机生成 requestId，去重表永远命中不了，「同一件事只做一次」当场失效 | `Tools.Builtin/ActionRequestId.cs`；`ProposeActionTool.ProposeAsync` 的派生调用 |
| 入口放行没有身份的请求 | 服务端现编号 → 重放算出新号 → 判重表白搭 | **Host 专属**：`Actions/InputIdentity.cs`、`AgentHub.RunAsync` / `ActionsController.Submit` / `HarnessController.Run` 的判空 |
| 同样的参数算出不同的号 | 参数原文参与计算，键序/转义写法一变就换号 | `ActionRequestId.Canonicalize` |

## 6. 换域改什么（AC-01 在工具层的落点）

- 只改 `Actions:ActionSet` 的值；组合根 `ResolveActionSet` 已有三个分支（`ErpActionSet` 默认 / `FakeActionSet` / `GiteaActionSet`）。**`src/Core` 的 diff 必须为空**。
- 第四域接入才需要加分支 + 一个项目引用；仍不改 Core。
- 能力清单、描述、参数 Schema、`RequiresApproval` 全部来自域注入的 `ActionSpec`，工具描述自动跟着变，不用改翻译代码。
- 明确不许：在 Core 写 `quantity > 0`、金额分档、状态机这类判断——那是域的 Schema / Rule / Policy。

## 7. 验证（逐字可跑）

```
dotnet build AgentWorkbench.slnx --no-restore --nologo
dotnet test tests/AgentWorkbench.CoreTests/AgentWorkbench.CoreTests.csproj --no-restore --nologo
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --nologo
```

- 翻译管道的边界：`ActionGateway/ProposeActionToolTests.cs`（清单外、畸形 JSON、仿冒身份、无身份、只回结论码、拔掉模型）。
- 去重键：同文件的 P11–P16（同输入回放、换输入各执行一次、参数变则新请求、无来源退化、键序无关、纯函数派生）。每组都做过负控变异：改回随机号 / 去掉排序 / 不规范化字符串 / 回退返回固定号，对应用例都会变红。
- 落库与并发：`ActionGateway/SqliteActionStoreTests.cs`（重启后仍在、并发只领一次、跨重启恢复、审计失败拒绝）。
- 换域：`ActionGateway/DomainSwapAc01Tests.cs`（`src/Core` 源码哈希前后一致）。
- 条数与红绿以实测为准，不写死；本会话 shell 的 restore 坏，只能 `--no-restore`（原因见 `AGENTS.md`）。

## 8. 我不知道 / 待核（带日期）

- **2026-11-09**：`POST /api/harness/run`（HTTP 同步入口）已用真实模型跑通端到端：`glm-4.7` 下 `erp.query` 被业务规则拒、`erp.draft` 成功且落库、`erp.submit` 挂起 → 审批 → `succeeded`（外部单号 `PO-2026-0001`），审计七条事件齐全。**SignalR Hub 那条仍未验**；且默认模型 `glm-4-flash-250414` 实测不调工具（直接反问「要查哪个订单」），按仓库默认配置启动时模型不会递单子。
- **2026-11-09**：并发同 `requestId` 时，领取失败的分支返回已落库的既有结果，**不带 `replayed_from_idempotency` 标记**；响应上是否要区分「真正执行」与「回放」未定。审计里只有一次 execute 事件可查。
- **2026-11-09**：Gitea 已挂组合根（`Actions:ActionSet=GiteaActionSet`），并做了双向对照：Gitea 域下 `issue.create` 返回 `pending/approval_required`、`erp.submit` 返回 `spec_not_found`；Erp 域下正好相反。`src/Core` 零改动。**但没配 token**，动作 fail-closed 且零 HTTP，只验证到「能力清单可读 + 域切换生效」；真实 HTTP 写入（M3）未验证。
- **2026-11-09**：输入身份收口后，两条 HTTP 请求体都变了——`POST /api/harness/run` 新增必填 `clientMessageId`，`POST /api/actions` 删除 `requestId` / `idempotencyKey`、改必填 `clientMessageId`。本文件第 1 条那次真实端到端脚本**尚未按新契约复跑**；旧脚本不带字段会被 400 拒。SignalR 前端一直传该字段，不受影响。

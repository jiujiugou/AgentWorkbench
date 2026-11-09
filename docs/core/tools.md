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

## 3. 三条边界（破了等于把执行权交给模型）

1. **模型输出一律不可信**：只判「是不是合法 JSON 对象」，业务含义交给域注入的 Schema 校验。畸形 JSON 直接拒绝，不进网关。
2. **身份不从参数来**：`subject` 只从 `IActionSubjectProvider` 取；参数里塞 `subject` / `role` 一律忽略；取不到身份直接拒绝。
3. **只回结论码**：`status` / `requestId` / `code`，不回 `Data`、不回外部单号；四道顺序与失败默认不在这里重排。

> 判据③「拿掉模型，系统还能安全地拒绝」就落在这三条上：同一张手工填的单子递进网关，结论必须一致。

## 4. 坑（只给指针，不复制注释）

| 现象 / 约束 | 为什么 | 去哪核 |
| --- | --- | --- |
| 新工具「没生效」 | 白名单按名裁剪，**裁掉静默不报错**；现为 7 个（含 `propose_action`） | `Runtime.Tools/ToolCatalog.cs`；`appsettings.json` 的 `Harness:Tools:Whitelist` |
| 工具拿不到身份 | SignalR 长连接的后续调用不保证还有 HttpContext | `AgentHub.RunAsync` 的 `Begin`；`ActionSubjectProvider` 的注释 |
| 并发下重复执行 | 「先查再插」有竞态窗口 | `SqliteActionRunStore.TryClaimIdempotencyAsync`（`INSERT OR IGNORE` + 主键） |
| 审计写失败却放行 | 审计是第四道，写不进去必须拒绝 | `SqliteTraceStore.WriteAsync` 的 catch 分支 |
| 新库缺表 | DDL 有两处消费者，各写一套必分叉 | `ActionStoreSchema.Statements`（唯一事实源）；迁移 `M030_ActionRunStore` |
| 工具描述里出现业务判断 | 描述来自域注入的数据，底座只拼接 | `ProposeActionTool.BuildDescription`；AC-02 禁词门禁 |

## 5. 换域改什么（AC-01 在工具层的落点）

- 改 `Actions:ActionSet` 并在组合根 `ResolveActionSet` 里加分支；**`src/Core` 的 diff 必须为空**。
- 能力清单、描述、参数 Schema、`RequiresApproval` 全部来自域注入的 `ActionSpec`，工具描述自动跟着变，不用改翻译代码。
- 明确不许：在 Core 写 `quantity > 0`、金额分档、状态机这类判断——那是域的 Schema / Rule / Policy。

## 6. 验证（逐字可跑）

```
dotnet build AgentWorkbench.slnx --no-restore --nologo
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --nologo
```

- 翻译管道的边界：`ActionGateway/ProposeActionToolTests.cs`（清单外、畸形 JSON、仿冒身份、无身份、只回结论码、拔掉模型）。
- 落库与并发：`ActionGateway/SqliteActionStoreTests.cs`（重启后仍在、并发只领一次、跨重启恢复、审计失败拒绝）。
- 换域：`ActionGateway/DomainSwapAc01Tests.cs`（`src/Core` 源码哈希前后一致）。
- 条数与红绿以实测为准，不写死；本会话 shell 的 restore 坏，只能 `--no-restore`（原因见 `AGENTS.md`）。

## 7. 我不知道 / 待核（带日期）

- **2026-11-09**：未做真实 SignalR 工具调用的端到端验证（`Provider:ApiKey` 为空、需登录 cookie），只验到宿主启动 + DI/迁移；工具委托与公开方法在测试里是同一条路径。
- **2026-11-09**：并发同 `requestId` 时，领取失败的分支返回已落库的既有结果，**不带 `replayed_from_idempotency` 标记**；响应上是否要区分「真正执行」与「回放」未定。审计里只有一次 execute 事件可查。
- **2026-11-09**：`ResolveActionSet` 仍只认 `FakeActionSet` / 默认 `ErpActionSet`，Gitea 没挂组合根——「换域只改一个配置」目前只在两个域上被真实验证过。

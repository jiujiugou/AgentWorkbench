# Core / Runtime

- **适用侧**：底座 ｜ **寿命**：现状（就近维护）｜ **最后校准**：2026-11-09
- **对应代码**：`src/Core/Runtime/`（6 个项目）
- **回答**：这条链怎么走、先读哪个文件、哪里不能碰、怎么验证
- **不回答**：单模块内部怎么实现（各项目 README 已写）、动作网关与翻译工具（→ [core/tools.md](tools.md)）、端口契约（`core/ports.md` 待写）、完成度与测试条数

> **本文件是地图，不是说明书。** 6 个子项目各自已有 README，细节在那里、也在代码注释里；这里不复制，只给「遇到 X 去查 Y」。
>
> **最重要的现状**：Runtime 这条链只服务 **v1 知识问答**；v3 主线的受控动作链不走这里。模型的入口是 `propose_action`（`Core/Tools/Builtin`），四道网关与落库见 [core/tools.md](tools.md)。别把本文件读成「产品主线怎么跑」。
>
> **2026-11-09 变化**：对话历史改为逐条落 `chat_messages`（`SqliteChatHistoryProvider`），
> 不再随会话快照整份覆盖。§7 先讲清两个职责各自走哪条路，再列连锁问题——那一节比这里重要。

## 1. 路由（先读这三个，别全仓 grep）

| 想知道 | 先读 |
| --- | --- |
| 一次提问从 Hub 到答案怎么走 | `Runtime.Harness/README.md` §一「一次 run 的流水线」 |
| 工具怎么被模型看到、调用怎么落地、审批在哪 | `Runtime.Tools/README.md` §一 |
| 上下文怎么注入、为什么用装饰器 | `Runtime.Context/README.md` §三 |

其余按需：`Runtime.Loop/README.md`（迭代预算 / 重复调用 / token 预算 / 终态归类）、`Runtime.Session/README.md`（会话与记忆、与 Context 的边界）、`Runtime.Model/README.md`（框架提供 vs 自研、配置节）。

代码入口（名字会变，**用 grep 定位，别记行号**）：`AgentHub.RunAsync` → `IAgentHarness.RunAsync` → `AgentHarnessService.BuildAgent` → `ToolExecutionPipeline`（装配 `FunctionInvokingChatClient`）→ `IAgentToolProvider.GetTools()`。

## 2. 链的形状（跨模块，单个 README 里没有的一段）

```
ChatView (SignalR)
  → /hubs/agent   Send(通用) / Ask(知识)
  → AgentHub.RunAsync            建/取会话、起一次 harness 流、按事件推进前端
  → AgentHarnessService          装配 ChatClientAgent；ChatOptions.Tools = ToolCatalog.GetTools()
                                 并把 sessionId 写进 session.StateBag（aw.session_id）
  → IChatClient 装饰器链          Context 注入指令/片段 → FunctionInvokingChatClient 工具环
  → ChatHistoryProvider          每次模型调用前取历史、run 结束追加本轮消息
                                 （落库版 = SqliteChatHistoryProvider → chat_messages）
  → 内置只读工具（6 个，见白名单）
  → Hub 事件推回前端
```

- `Send` 与 `Ask` 只差 `knowledgeMode`，**不是两条独立链**。
- `IAgentToolProvider` 是工具提供方唯一契约，单独成工程以避免反向依赖整个执行管道；`ToolCatalog` 按名字裁剪白名单。
- **主线不在这条链上**：受控动作走 `propose_action` → `ActionRunService` → `Core/Tools/ActionGateway` → 落库，见 [core/tools.md](tools.md)。

## 3. 代码推不出的坑（本文件最值钱的部分，只给指针）

| 现象 / 约束 | 为什么 | 去哪核 |
| --- | --- | --- |
| 不要给 `ChatClientAgent` 挂 `AIContextProviders` | MEA 会在我们装配的函数调用环外再包一层，绕过守卫与轨迹（实测：同参重复不再判 `loop_detected`、工具异常不归类 `tool_error`） | `Runtime.Context/README.md` §三；`IContextAssembler.Wrap` 的注释 |
| `Harness:Loop` 必须显式配 | 不配真模型必 504；现为 `MaxIterations=6` / `TimeoutSeconds=300` / `MaxRepeatedToolCalls=3` | `appsettings.json`；`Runtime.Loop/README.md` |
| 新工具「没生效」先查白名单 | `Harness:Tools:Whitelist` 按名裁剪，**裁掉静默不报错**；现为 7 个（含 `propose_action`） | `ToolCatalog.cs`；`appsettings.json` |
| 底座侧不许出现业务词 | AC-02；Runtime 尤其容易在 Context 拼装处被塞进业务语义 | 在 `src/Core` 扫业务词（禁词门禁） |
| 工具异常不能在环外吞掉 | 工具环负责归类 `tool_error` / `loop_detected`，在外面 catch 会丢轨迹 | `Runtime.Tools/README.md` §一 |
| `AgentHub` 的 `Generations` 只防「同一连接」连接并发 | 它是连接级防抖，保 `Stop()` 能找到正确的 CTS；两个标签页是两条连接，它完全无感。同一会话的并发由 `ISessionRunGate` 拦 | `AgentHub.RunAsync` 注释；本文 §7.6 |

> **环境坑就地记，不指向其他文档**：`dotnet restore` 在本会话 shell 坏（`NuGet.targets(780)` 的 `path1` 为空），只能 `--no-restore`；`curl` 打 localhost 加 `--noproxy '*'`；`global.json` 锁定 10.0.201，别移走；后端运行时锁 `bin/**/*.dll` 会让整解决方案 build 失败（MSB3027），先停服务再 build。

## 4. 改动规矩（可检查）

- 新增能力 = 加类 / 加模块，**0 改旧代码**；模块间只依赖对方契约。
- 上下文注入只能走 `IChatClient` 装饰器；Agent 配置处 `grep AIContextProviders` 应为 0。
- 新工具必须实现 `IAgentToolProvider` **且**名字加进白名单，否则静默不可见。
- `Core` 下任何项目不得 import `Domains`；参数对底座是不透明 JSON。
- 改这里**不必**改上层骨架文档——只有边界变了才改（AC-01-doc）。

## 5. 验证（逐字可跑）

```
dotnet build AgentWorkbench.slnx --no-restore --nologo
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --no-build --nologo
cd web; npm run build
```

- 本会话 shell 的 restore 坏（`NuGet.targets(780)` 的 `path1` 为空），只能 `--no-restore`。
- 测试条数与红绿以实测为准，不写死。

## 6. 待核 / 我不知道（带日期）

- **2026-11-08**：11-01 端到端真跑曾出现「模型零检索、凭常识编六条建议，系统判 `unsupported` 但答案照给」。当前来源面板已改由工具完成事件驱动，**同类问题是否仍存在未复测**；不把旧结论当现状。
- `Runtime.Model` 引用 `Core/Foundation/Domain`（v1 文档业务记录）但零使用，是否该清未定。
- `AgentHarnessService` 构造依赖较多，是否有收拢空间未评估。
- **2026-11-09**：失败轮要不要在界面留痕、删除/TTL 时模型上下文跟不跟着清——两个边界条件尚未定，
  见 §7.3 第 1 条；指令快照（「模型当时看到了什么」）未实现，见 §7.3 第 10 条。`IChatMessageStore`
  契约仍在实现模块、未归位，见 §7.1 注。

## 7. 会话历史：两个职责，三段归属

**这一节是给下一个人（包括三个月后的你）读的。改造之前的失效模式不重复讲，只讲现在的职责边界。**

### 7.1 先把名字叫对

「两个历史源」是个会误导人的说法。这里不是同一份数据存了两遍、后来走岔了，而是**两个不同的职责，各自有一条路径，没有汇合点**：

- **模型上下文**：回答「下一轮模型该看到什么」（用户消息、模型回复、工具调用与结果、指令与注入内容）。
- **界面投影**：回答「人回头看时该显示什么」（聊天正文、工具完成提示、token 用量、任务开始/结束）。

它们唯一的共同点是都描述同一段对话。**所以「两边不一致」不是缺陷，是两件事本来就不该对得上**；X1/X4 实测到的差异属于正常结果，不是待修的 bug。10-13 起它们就是有意的分离（`AgentHub.cs:143` 那行注释「专供界面恢复」是当时的决定），11-09 只是把模型那一侧从整份快照换成逐条追加。

**三段归属**（「界面」和「审计」常被混成一个词，但不是一回事）：

| 职责 | 拥有者 | 物理表 | 换域要改吗 | 换 CLI/定时任务还成立吗 | 拿掉它底座还能跑吗 |
| --- | --- | --- | --- | --- | --- |
| 模型上下文 | `Core/Runtime.Session`（契约）、`Core/Memory.Persistence`（实现）、`Core/Runtime.Context`（装配点） | `chat_messages` | 不改 | 成立 | **不能**（每轮失忆，回到案例一） |
| 审计轨迹 | `Core/Engineering/AgentWorkbench.Observability` | `agent_runs` / `agent_steps` | 不改 | 成立 | **不能**（§4.1 五字段答不上） |
| 界面恢复 | **`Host/AgentWorkbench.Webapi`**（`AgentHub:133/360` 决定写、`ConversationsController` 读） | `messages`（`conversations` 子表） | 不改 | **不成立**（没有界面） | **能**，只是刷新看不到 |

两条容易被绕进去的地方：

- **「代码在 Core」不等于「职责归 Core」**。`ConversationRepository.cs` 确实在 `Core/Foundation/Persistence`，但它只是通用表读写机制，不认识 `messageId`、不认识「刷新」、不认识流式与前端展示结构。决定写什么、何时写、失败写不写的是 Host。
- **AC-01 是必要条件不是充分条件**。表里三行「换域都不改」只证明它们不承载业务语义；真正把界面恢复分出去的是后两列（换入口不成立、拿掉底座照跑）。别用 AC-01 推出「都该归 Core」。

`Core` 里这两块已经各有其位，**不需要新增模块，也不需要找一个统一负责人**——本来就不是同一个需求。

**物理层：三张表各管一件事**

| 表 | 谁写 | 存什么 | 写的方式 |
| --- | --- | --- | --- |
| `chat_messages` | `SqliteChatHistoryProvider` | 模型看到与产出的对话往返，含 `functionCall` / `functionResult` | **只追加**（`INSERT`，按 `id` 升序读） |
| `conversations` 的消息 | `AgentHub.RunAsync` | 界面正文（user / assistant 文本） | 只追加 |
| `agent_sessions` | `SqliteSessionStore`（每次 run 的 finally） | StateBag 里除历史之外的东西，现在只有 `aw.session_id` | **整份覆盖**（`Upsert`） |

键名契约在 `Runtime.Session/AgentSessionKeys.cs`：编排层写 `aw.session_id`，provider 读同一个键。MAF 的 `AgentSession` 不带 id，这个键是唯一来源。

**注**：`IChatMessageStore` 契约目前定义在 `Memory.Persistence` 里，按本仓「契约留本模块、实现放持久化模块」的红线应挪到 `Runtime.Session`。这是 Core 内部归位，不改行为，尚未做。

### 7.2 已经解决的

**丢（历史被整份覆盖）**：同一会话被两个 run 并发推进时，后完成的 run 曾会把整份快照写回去，先完成那轮从模型记忆里消失。现在两边各 `INSERT` 自己的行，读回时都在。证据：`ChatHistoryPersistenceTests.C1`（负控：改成「先删后插」，C1/C2 立刻红）。

**乱（两个 run 交错写同一会话）**：追加式不丢，但两个 run 仍会把对话交错写进一个会话，模型在半截上下文里继续推理。现在由会话闸挡住，第二个请求返回 `session_busy`。见 §7.6。

### 7.3 连锁问题（含一条已澄清的伪问题）

1. **「两个历史源不一致」是命名造成的伪问题，不是缺陷（2026-11-09 澄清）。** 见 §7.1：两条路径各自有各自的作用，没有汇合点，也不存在一致性可承诺。`HistoryDivergenceTests` X1–X4 实测到的四种形状仍然有效，但要**按性质分开读**：

   | 形态 | 界面（`messages`） | 模型（`chat_messages`） | 性质 |
   | --- | --- | --- | --- |
   | 正常一轮带工具（X1） | 2 条：user + assistant 文本 | 4 条：含 `functionCall` / `functionResult` | **正常**：界面本来就不展示执行过程 |
   | system 指令与注入片段（X4） | 不存 | 也不存（注入在 provider 下游） | **正常**：指令是一次 run 的注入，不是一段对话；但由此引出下面第 10 条 |
   | 失败的一轮（X2） | 1 条孤立提问（无答复） | **0 条**——provider 的 Store 一次都没被调用 | **边界未定**：界面这条路径没定义失败时写什么 |
   | 删除会话（X3） | 随外键级联删掉 | 无外键，**原样留着** | **边界未定**：模型这条路径没定义删除语义 |

   所以真正剩下的只有**两个待定边界条件**，不是「要不要合并 / 谁是真相」：

   - **失败轮**：界面要不要给那条提问留一个明确的失败标记（而不是一条看起来没答的孤问）。语义归 `Runtime.Harness` 定，Hub 执行。参考：codex 有 `turn_aborted` 记录，pi 明确「`pending` 不得作为已完成的 assistant 回复持久化」。
   - **删除 / TTL**：删会话或会话过期时，模型上下文跟不跟着清；不清就明说它是审计资料、永久保留（同第 9 条）。参考：codex 删会话就是删那一个 rollout 文件，两个读者同时消失，不存在中间态。

   **方向（不现在做）**：codex 用同一份 append-only 文件里的两种行类型（`response_item` = 模型看到的，`event_msg` = TUI 重放的）区分两个职责；pi 用同一棵 JSONL 树 + `excludeFromContext` / `display` 标记做投影，并把 system 消息也记进去。都是**一份真相 + 两种读法**，所以它们没有「一致性」这个问题。我们做不到直接照搬：① 这是被嵌进 ERP 的 web 底座，多入口、多租户、跨重启，不是单进程 CLI；② MAF 的 `ChatHistoryProvider` 只接受 `IEnumerable<ChatMessage>`，塞不进 `turn_aborted` / `token_count` 这类事件。触发点是客户真要审计回放、且那个口子验通了，属于 M2/M3。
2. **`agent_sessions` 的覆盖机制还在。** 现在它只存一个 `aw.session_id`，丢了也无所谓。但一旦有新的 provider 往 StateBag 里放实质状态（压缩摘要、用户偏好），`lost update` 立刻复发，而且不会有人记得这件事。**加新 provider 前先读这一条。**
3. **动作重复执行一点没解决。** 这次只保证「历史不重复」，不保证「同一句话只办一次」。`clientMessageId` 缺失仍退化成随机 `requestId`，判重表认不出是同一件事，结果是两张单。见 [core/tools.md](tools.md) §3。
4. **审批后不重跑四道闸门。** `ActionRunService.ApproveAsync` 拿到 pending 就直接执行，挂起期间权限被收回也照跑。
5. **历史没有条数上限。** provider 全量读回，裁剪仍归 Context；但 Context 的 trim 只影响「本次发给模型的内容」，不减少 SQLite 的读取量。会话很长时读会变慢，需要时在 store 加 `LIMIT`（注意：加 `LIMIT` 就是在 provider 里做裁剪，会与 §四的边界冲突，先想清楚再动）。
6. **老数据没迁移。** 升级后，老会话在 `agent_sessions` 里的历史不再被读。症状是「界面记录都在，模型全忘了」。当前接受，因为库里只有测试对话；有真实数据时必须补一次性迁移。
7. **跨机多实例仍不行。** SQLite 单文件，同机多进程可读写，跨机不行。
8. **关掉 `SessionEnabled` 不等于关掉历史。** provider 按 `sessionId` 查 `chat_messages`，与 `AgentSession` 对象无关，所以历史照常工作；代价是 StateBag 里的非历史状态每轮丢失。**不要为了「彻底消除 lost update」直接关它**，先确认没有别的 provider 依赖 StateBag。
9. **`chat_messages` 只增不减。** 不随会话 TTL 清理，也不随会话删除连带删；目前只有 `AppendAsync` / `LoadAsync`，没有 Delete。后果两条：磁盘无界增长；「删了会话，模型还记着」（与第 1 条的删除边界是同一件事，定边界时一起定）。要么并入 `MemoryRetentionService` 按最后活跃时间清，要么定性为审计资料、永久保留 + 配额告警。**这条比第 5 条更硬**——第 5 条只影响单次读取，这条影响整个库。
10. **「模型当时看到了什么」没有数据源（X4 引出的真缺口）。** `chat_messages` 只记对话往返；system 指令与注入片段由 `ContextInjectingChatClient` 在 provider 下游注入，不落库；`agent_runs` 只有 model / iterations / latency。于是 §4.1 五字段里的「看到什么」答不上——不是答得不好，是没数据。补法：在 `ExecutionTrace` 加指令快照（正文或 hash + 片段 id），归 `Core/Engineering/Observability`，Harness 收尾填值；**不要塞进 `chat_messages`**（一轮 run 可能有多次模型调用，会和「一轮对话」混在一起）。参考 codex 的 `session_meta.instructions` / `turn_context` / `world_state`、pi 的持久化 `system` 消息。

### 7.4 一个不该再讨论的问题：要不要开 `RequirePerServiceCallChatHistoryPersistence`

**不开。** 实测（`HistoryProviderProbeTests.H1`）：一次 run 结束，历史里已经有 `user → functionCall → functionResult → assistant` 四条，工具往返没有丢。开那个开关只会让 `FunctionInvokingChatClient` 把对话当成「服务端管理」并清掉自己累积的历史，进而改变守卫与轨迹的行为——收益为零，风险非零。

### 7.5 怎么验

```
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --filter "FullyQualifiedName~ChatHistoryPersistenceTests"
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --filter "FullyQualifiedName~HistoryProviderProbeTests"
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --filter "FullyQualifiedName~HistoryDivergenceTests"
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --filter "FullyQualifiedName~SessionRunGateTests"
```

前三组 5 + 2 + 4 条（C1–C5 / H1–H2 / X1–X4）：C1 是「历史不会被覆盖」的直接证据，H1 是「不用开
`RequirePerServiceCallChatHistoryPersistence`」的直接证据，X1–X4 **锁的是两条路径的形状，不是功能验收**——
X1/X4 是职责不同的正常结果，X2/X3 对应 §7.3 第 1 条那两个待定边界
（负控：provider 不落库 → X1/X3 红）。第四组 5 条（G1–G5）见 §7.6。

### 7.6 会话互斥（L1，进程内）

**不变量**：同一 `sessionId`，同一时刻最多一个 run 在推进；第二个请求必须被**拒绝**，
不是并发执行，也不是静默丢弃。

| 违反后果 | 谁强制 | 怎么测 |
| --- | --- | --- |
| 两个 run 交错写 `chat_messages`，模型读到半截对话；审批与幂等判断建立在错乱上下文上 | `ISessionRunGate`（`Runtime.Session/SessionRunGate.cs`，进程内 `ConcurrentDictionary`） | G1 同会话第二个必须拿不到；G2 不同会话必须都能拿；G3 重复释放不影响后来者；G4/G5 两个入口都回 `session_busy`。负控：去掉 `_released` → G3 红 |

**三条代码里看不出来的决定：**

1. **落点在 Harness，不在 Hub。** Hub、`POST /api/harness/run`、Workflow 三个入口都走
   `AgentHarnessService`，闸放在 `RunAsync` / `StreamAsync` 开头，一处约束全部；放 Hub 只补了一处。
2. **拒绝，不排队。** 排队没有上限，第二句话可能几分钟后才被处理，用户早已忘了上下文。
   抢不到时 `StreamAsync` 产出**一个 `Failed` 事件**（`Code=session_busy`）就结束——不抛异常，
   否则会被 Hub 的 `catch` 当成服务故障，和「这条会话正在回答」混在一起。
   `RunAsync` 返回 `Status=failed, Code=session_busy, RunId=0`；**0 表示没有开始跑、也没落轨迹**。
3. **Hub 那道 `Generations` 闸与它无关，也不能互相替代。** `Generations` 是**连接级防抖**，
   保的是 `Stop()` 能找到正确的 CTS；两个标签页是两条连接，它完全无感。删了它会坏 `Stop`，
   靠它挡并发则挡不住。

**覆盖边界（必读）**：L1 的锁在内存里，**只有单进程成立**。进程崩溃时锁随进程消失，不会留僵尸锁
（这是相对数据库租约的唯一优势）；但同一台机器上跑两个进程、或跨机部署时，L1 一律不成立。

多实例需要 **L2：数据库租约**（未实现）：

```sql
session_leases (
  session_id TEXT PRIMARY KEY,
  holder     TEXT NOT NULL,   -- 实例标识（pid + 启动批次）
  fence      INTEGER NOT NULL,-- 单调递增，防旧持有者写入
  expires_at TEXT NOT NULL
)
```

抢占是一条带条件更新的 upsert（`ON CONFLICT ... DO UPDATE ... WHERE expires_at < now`），
租约时长取 run 墙钟上限的两倍（600 秒），不需要心跳；释放用 `DELETE ... AND fence = @mine`。
**这条 upsert 是整套方案里唯一不能用 EF LINQ 表达、只能走 `ExecuteSqlRaw` 的语句。**
上多实例前不要用 L1 兜底——它是单进程事实，不是部署保证。

---

## 写法

见 [docs/README.md](../README.md) §6「模块文档写法」。本节不重复定义——同一套规矩只写一处。

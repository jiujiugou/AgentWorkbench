# 会话与记忆模块（Runtime.Session）

这个模块干三件事：**让同一个 `sessionId` 在一段时间内复用同一个会话对象并把它存得住**、
**给长期记忆一个载体（存 / 查 / 忘）**、**保证同一个 `sessionId` 同时只有一个 run 在推进**。

它不做历史策略、不决定「材料怎么进上下文」、也不做检索排序之外的组装。会话里装了什么由装配它的编排层
（`AgentHarness`）决定；历史怎么裁剪/压缩、注入几条、占多少预算，都是 `Context` 的事。

### 这一格是什么（先定性，别按「功能」理解）

**模型上下文是运行时状态载体，不是可选功能。** 模型每次调用都是无状态的新请求，「它记得上一轮
说过什么、调过哪个工具」全靠跨轮状态被存下来、下一轮再喂回去。所以它没有能力位、没有开关、
不能「需要了再引用」；拿掉它，底座退化成每轮失忆（案例一：模型答「没办过」→ 用户重说 → 第二张单）。

本模块拥有的是**契约与不变量**（`ISessionStore`、`aw.session_id`、`ISessionRunGate`），不是实现：
落库在 `Memory.Persistence`，注入形态与预算在 `Runtime.Context`，界面显示什么在 `Host`。
本格不含业务语义，换动作域时 `src/Core` 一行不改。三段完整归属与判据见 `docs/core/runtime.md` §7.1。

---

## 一、现在已经具备的能力

### 会话怎么被复用

- 契约是 `ISessionStore.GetOrCreateAsync(sessionId, factory)` + `ISessionStore.SaveAsync(...)`。
  命中就返回已有会话（落库版会反序列化），没命中就调用 `factory` 建一个新的，再按 `sessionId` 存下来。
- `ISessionFactory` 把「建 / 序列化 / 反序列化」抽象出来，由 `AgentHarness` 用当次 run 的 agent 实现
  （`AgentSessionFactory`）。存储实现因此不需要认识具体的模型客户端。
- 两个实现：
  - `SessionStore`（本模块，进程内 `ConcurrentDictionary`）：重启即丢，适合单测 / 精简环境。
  - `SqliteSessionStore`（`Memory.Persistence`）：会话序列化后落 `agent_sessions` 表，重启后同 `sessionId` 能续上。
- 消费者只有 `AgentHarness` 的两个入口（`RunAsync` / `StreamAsync`）：run 结束会 best-effort `SaveAsync`，
  成功/失败/取消/超时都写。

### 同一个会话不会被两个 run 同时推进（2026-11-09 新增）

历史改成追加之后仍然需要这道闸：追加解决了**丢**，没解决**乱**。两个 run 会把各自的对话交错写进同一个
会话，模型在半截上下文里继续推理，审批与幂等判断也建立在这段错乱的上下文上。

- 契约：`ISessionRunGate.TryAcquireAsync(sessionId)`。抢到返回句柄（`DisposeAsync` 即释放），
  已被占用返回 `null`。
- 语义是**拒绝，不是排队**：排队没有上限，第二句话可能几分钟后才被处理，用户早已忘了上下文。
- 落点在 `AgentHarnessService` 的**两个入口**，不在 Hub —— Hub、`/api/harness/run`、以后的定时任务都走
  Harness，放 Hub 只补了一处。
- 抢不到的表现：`RunAsync` 返回 `RunId=0` + `AgentRunCode.SessionBusy`（不落轨迹，这次不算一次 run）；
  `StreamAsync` 产出单个 `Failed` 事件。**都不抛异常**——抛了会被 Hub 的 catch 当成「服务出错」，
  和「这条会话正在回答」混在一起。
- 进程崩溃时锁随进程消失，**不会留僵尸会话**；这是它相对数据库租约的唯一优势，也是它只在单进程成立的代价。
  跨进程 / 跨机要 L2（`session_leases` + `fence`），见 `docs/core/runtime.md` §7.6。
- `AgentHub.Generations`（键是 `ConnectionId`）是另一道闸，**只做连接级防抖、保 `Stop` 可用**，
  不承担会话互斥；两者不重叠也不能互相替代。

### 历史去哪了（2026-11-09 变更）

`agent_sessions` **不再承载对话历史**。历史改由 `ChatHistoryProvider` 逐条写 `chat_messages`
（落库实现：`Memory.Persistence/ChatHistory/SqliteChatHistoryProvider`），
所以本模块的快照只剩非历史状态——当前只有 `aw.session_id` 一个键。

`ISessionStore` 的契约与两个实现都没变，但「快照里装什么」变了：
两个 run 并发覆盖同一会话时，伤害从「丢掉整段对话、模型失忆」降到「丢掉一个 id」。
覆盖机制本身**还在**——只要有新 provider 往 StateBag 放实质状态就会复发；
**别为了彻底消除覆盖就顺手关掉 `SessionEnabled`**，理由见 `docs/core/runtime.md` §7.3 第 8 条。

`sessionId` 的传递不走构造函数：Harness 在每次 run 前写
`session.StateBag[AgentSessionKeys.SessionId]`，provider 从同一个键读回。
MAF 的 `AgentSession` 本身不带 id，这个键是唯一来源。

### 会话里装的是什么

- 会话是 MAF 的 `AgentSession`，状态按 key 分区放在 `StateBag` 里。
- **2026-11-09 起快照里没有历史了**：历史由 `SqliteChatHistoryProvider` 逐条写 `chat_messages`，
  `StateBag` 只剩 `aw.session_id`（编排层写入的会话标识）。
  序列化 `AgentSession` 得到的就是「其余键」——现在只有一个 id。
- 序列化仍走 MAF 官方的 `AIAgent.SerializeSessionAsync` / `DeserializeSessionAsync`。
- ⚠️ 调用时 `JsonSerializerOptions` 必须传 `null`：传自定义实例不带 MAF 的 source-generated 元数据，
  Serialize / Deserialize 会抛 `NotSupportedException`。见 `AgentSessionFactory` 注释。

### 长期记忆怎么落地

- 契约是 `ILongTermMemoryStore`：`RememberAsync` / `RecallAsync` / `ForgetAsync` / `PurgeExpiredAsync`。
  **只进出结构化数据**（`MemoryItem`），绝不返回拼好的 prompt 文本，也不决定注入策略。
- **隔离维度是作用域**（`MemoryScope` = `type` + `id`）：`session` / `user` / `agent` / `global`。
  召回只发生在本作用域内，外加可选的 `global` 兜底，不会串到别人的记忆里。
- **覆盖维度是 key**：同 `(scope, key)` 的写入是覆盖（保留原 id 与使用统计），没有 key 则每次新增。
- **保留期**：`MemoryWrite.Retention` 或 `MemoryOptions.LongTermRetentionDays` 决定 `expires_at`；
  过期的行召回时看不见，并由后台服务删除。
- 写入/召回的入口（MVP）是 agent 工具：`remember` / `recall`（在 `Memory.Persistence` 注册）。

### 记忆工具的作用域从哪来

工具在被模型调用时读 `IMemoryScopeAccessor.Current`（`AsyncLocal` 实现），由 `AgentHarness` 在每次 run
开始时设为 `MemoryScope.Session(sessionId)`。

**为什么不让模型用参数指定 scope_id**：那等于把隐私边界交给模型。模型只能选「本作用域 / 全局」这种粗粒度
开关（`global=true`），具体 id 由编排层决定。

### 会话快照不会无限增长（历史表另说，见 §二）

- `MemoryOptions`（配置节 `Harness:Memory`）：

  | 键 | 默认 | 含义 |
  | --- | --- | --- |
  | `SessionEnabled` | `true` | 是否维持会话内记忆 |
  | `SessionTtlMinutes` | `1440` | **会话快照**的空闲过期时间（分钟）；`0` = 不过期。⚠️ 2026-11-09 起它**不再决定模型记忆的长度**——历史在 `chat_messages`，不随 TTL 清理 |
  | `MaxCachedSessions` | `1000` | 进程内缓存条数上限；`0` = 不限 |
  | `EvictionSweepSeconds` | `60` | 落库版过期清理周期（秒） |
  | `LongTermEnabled` | `true` | 是否注册 `remember` / `recall` 工具 |
  | `LongTermScope` | `session` | 工具写入的默认作用域（`session` 或 `global`） |
  | `MaxMemoryChars` | `2000` | 单条记忆内容上限，超出拒绝写入 |
  | `RecallCandidateK` | `20` | 召回候选上限默认值（取库上限，不是注入条数） |
  | `LongTermRetentionDays` | `0` | 长期记忆默认保留天数；`0` = 不过期 |

- 内存版会话：命中时判 TTL，超上限按 LRU 淘汰（`TimeProvider` 可注入，测试可确定性推进时钟）。
- 落库版：读时判 `expires_at`（过期即删并当新会话），并由 `MemoryRetentionService` 定期清理。
- **两个「不增长」都只管快照与记忆行**。`chat_messages` 一条都不删：会话 TTL 到期、会话删除都不连带清，
  所以「一天不用就忘」这个语义对模型记忆**已经失效**（配置还在、行为没了）。要清得做删除/保留期，
  见 §二 表格最后两行。

### 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `ISessionStore` | `GetOrCreateAsync`、`SaveAsync` | `SessionStore` / `SqliteSessionStore` | `AgentHarness` 两个入口 |
| `ISessionRunGate` | `TryAcquireAsync` | `SessionRunGate`（进程内） | 同上，两个入口各抢一次 |
| `ISessionFactory` | `CreateAsync`、`SerializeAsync`、`DeserializeAsync` | `AgentSessionFactory` | 会话存储 |
| `ILongTermMemoryStore` | `RememberAsync`、`RecallAsync`、`ForgetAsync`、`PurgeExpiredAsync` | `SqliteLongTermMemoryStore` | `remember` / `recall` 工具（阶段 3 还会给 Harness） |
| `IMemoryScopeAccessor` | `Current`、`Begin` | `AsyncLocalMemoryScopeAccessor` | 记忆工具 |
| `ISessionStateRepository` / `IMemoryItemRepository` | 表读写 | `Sqlite*Repository` | 落库版存储、保留服务 |

落库版由 `AgentWorkbench.Memory.Persistence` 提供，组合根里 `AddNitroMemoryPersistence` 用 `Replace`
覆盖 `AddNitroMemory` 注册的内存版 `ISessionStore`，并注册长期记忆载体与记忆工具。

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| 长期记忆自动召回 | 记忆只在模型主动调 `recall` 时生效 | 只有显式工具（阶段 2） | Harness 取候选 → Context 注入（阶段 3） |
| 记忆的语义召回 | 关键词召回命中率低 | `LIKE` 关键词 | 需先定 FTS5 / 向量口径与评测指标 |
| 历史无限长 | 上下文窗口撑爆 | 落库存的是**完整**历史，不裁剪 | 裁剪/压缩归 `Context`，不在本模块做 |
| 并发同 `sessionId` 的 run | ~~两个 run 共用一个会话，消息可能串~~ **已解决（单进程）** | 2026-11-09：历史改逐条追加落 `chat_messages`（解决「丢」）；`ISessionRunGate` 按 sessionId 互斥、抢不到回 `session_busy`（解决「乱」）。`agent_sessions` 仍整份覆盖，但只剩非历史状态 | 跨进程 / 跨机未覆盖，需 L2 数据库租约（`session_leases` + fence）；见 `docs/core/runtime.md` §7.6 |
| 历史表没有保留期 | `chat_messages` 只增不减：不随会话 TTL 清理，会话删除也不连带删。磁盘无界增长，且「删了会话但模型还记着」 | 只有 `AppendAsync` / `LoadAsync`，没有 Delete | 二选一：并入 `MemoryRetentionService` 按最后活跃时间清；或定性为审计资料、永久保留 + 配额告警 |
| 多实例共享会话/记忆 | SQLite 单文件不是分布式存储；且会话闸的锁在内存里，两个实例互相看不见 | 同机多进程可读，跨机不行；多实例下会话互斥等于没有 | 分布式后端另开模块，不改本契约；会话互斥需 L2 租约，不能靠进程内锁 |

> 前三条（自动召回 / 语义召回 / 历史无限长）是**能力增强**，后三条是**缺陷或边界**。混在同一张表里
> 排队，会让「重复下单」那种真缺陷被稀释成「六分之一待办」——分类见 `docs/core/runtime.md` §7.3。

---

## 三、换一个存储要做什么

1. 会话：实现 `ISessionStore`（+ 需要的 `ISessionStateRepository`）。
2. 长期记忆：实现 `ILongTermMemoryStore`。
3. 在新模块或组合根里换掉注册。
4. 不要动 `AgentHarness` —— 它只认契约。

判断标准：**契约留在本模块，实现可以在本模块（进程内）或新模块（持久化）**，但依赖方向不能反向。

**换存储不会自动换来多实例互斥。** `ISessionRunGate` 与 `ISessionStore` 是两条独立的契约：把会话
换成 Postgres，`SessionRunGate` 仍然只在单进程有效。要跨实例，得再实现一个走数据库的
`ISessionRunGate`（租约 + fence），**不是换掉 `ISessionStore` 就顺手解决了**。

---

## 四、与 Context 的边界（防重叠）

- **数据面（本模块 / `Memory.Persistence`）**：会话存取；长期记忆的存/查/忘；只出**结构化数据**，不拼 prompt。
- **组装面（`Context`）**：把传入材料拼成最终 user 文本；注入条数/长度/预算；历史裁剪与压缩。纯逻辑、禁止 IO。
- **编排面（`AgentHarness`）**：Memory 取数据 → Context 组装 → 调模型；并负责设置记忆作用域。

四条硬规则：Memory 不拼 prompt；Memory 不管注入策略（`RecallAsync` 的 `CandidateK` 只是取库上限）；
Context 不查库、不引 `Memory`；历史只有一个策略点（Memory 存完整历史，Context 决定模型可见视图）。

依赖方向**当前只由 csproj 保证**：`Runtime.Session` 与 `Runtime.Context` 都只引用 `Shared`，
所以「Memory → Persistence」「Context → Memory/Persistence」在结构上不可能发生。
但**没有自动门禁**——`tests/AgentWorkbench.ArchitectureTests` 只剩 `obj/`，源码为空，
给这两个 csproj 加一条 `<ProjectReference>` 不会有任何东西变红。改的时候人工确认。

> 例外说明：`remember` / `recall` 返回的是**工具结果**（给模型看的一句话），属于工具契约，不是系统注入的
> prompt。系统注入仍然只在 `Context`。

---

## 五、红线

1. **不把历史策略塞进本模块**。会话是容器，不是上下文策略；裁剪/压缩归 `Context`。
2. **不引用 `Persistence`**。依赖方向目前只由 csproj 保证（`ArchitectureTests` 源码为空、没有自动门禁，
   见 §四）；落库实现放 `Memory.Persistence`，契约留本模块。
3. **会话按 `sessionId` 隔离，记忆按 scope 隔离**。禁止跨 id 复用会话，禁止跨 scope 召回。
4. **作用域不让模型指定**。scope_id 只能来自编排层（`IMemoryScopeAccessor`）。
5. **会话互斥只在单进程成立，不得当成分布式锁**。`SessionRunGate` 的锁在内存里，两个实例各有一份、
   互相看不见。上多实例前必须做 L2（数据库租约 + fence），**不要靠加进程内锁冒充**——这和
   `AgentHub.Generations` 那道「看起来防住了」的连接级闸是同一个坑。
6. **没有能变红的负控，不新增能力**。

---

## 六、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

已覆盖（工作区实测能跑的，都在 `tests/AgentWorkbench.ApplicationTests/`）：

- 会话对象：`Runtime/SessionStoreTests.cs` F4.1–F4.4——同 id 命中、TTL 过期、LRU 淘汰、写回刷新访问时间。
- 历史落库：`Runtime/ChatHistoryPersistenceTests.cs` C1–C5——并发不丢、顺序、会话隔离、
  无 sessionId 不落库、空批次 no-op。C1 的负控：把追加改回「删后插」，C1/C2 必红。
- provider 与 MAF 的契约：`Runtime/HistoryProviderProbeTests.cs` H1–H2——一次 run 的历史含完整工具往返；
  单条消息序列化不丢 `functionCall`。
- 两条路径的形状：`Runtime/HistoryDivergenceTests.cs` X1–X4——带工具的一轮条数不同、
  失败轮界面有孤问而模型零条、删除会话后模型历史仍在、注入的 system 指令不进历史。
  这四条是**事实锁定，不是功能验收，也不是缺陷清单**：X1/X4 是两个职责本来就不同的正常结果；
  X2/X3 对应两个尚未定义的边界条件。见 `docs/core/runtime.md` §7.1 / §7.3 第 1 条。
- 会话互斥：`Runtime/SessionRunGateTests.cs` G1–G5——同会话第二个拿不到、不同会话可并行、
  重复释放不影响后来者、两个入口都回 `session_busy`。负控：去掉 `_released` 保护 → G3 红。
- 预期红（规格未实现，不是失败）：`Runtime/SessionContractTests.cs` F4.7——工具注册默认值。

以下条目**当前没有任何测试**。它们原先写在 `tests/AgentWorkbench.UnitTests/Memory/`，
而该项目在工作区不存在（现存三个测试项目中，`ArchitectureTests` 也只剩 `obj/`、源码为空）。
`grep` 可证：`LongTermMemoryStore`、`MemoryRetentionService`、`RememberTool` 在 `tests/` 源码里零引用
（只出现在编译产物里）。

- 会话重启续接（原表述：销毁 store + agent、复用同一库后同 `sessionId` 的历史续上）
  —— 注：改成落库历史后，这条的正确形态是「`chat_messages` 里在，反序列化出来的会话里没有」，与旧表述不同。
- 长期记忆正例 / 负控（作用域隔离、`(scope,key)` 覆盖、保留期、超长拒绝）。
- 保留服务清理（过期会话行与过期记忆行）。
- 组合根 `Replace` 断言、`LongTermEnabled=false` 不注册工具。

尚未覆盖：真·跨进程重启（验收脚本未落地）、记忆的自动召回（阶段 3）、历史表的保留期与删除、
**跨进程 / 多实例的会话互斥（L2 数据库租约，未实现）**。

## 依赖

本模块只引用 `Shared`。落库实现（`AgentWorkbench.Memory.Persistence`）实测引用四项：
`Runtime.Session`、`Persistence`、`Shared`、`Tools.Abstractions`。

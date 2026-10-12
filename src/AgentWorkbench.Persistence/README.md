# 持久化模块（Persistence）

这个模块只干一件事：**给「要活过重启」的东西提供表、迁移和仓储**。

它不做业务判断：写什么、什么时候写由各模块决定；它只保证「说写就写进去了，重启也还在」。

---

## 一、现在已经具备的能力

### 连接怎么开

- `IDbConnectionFactory.Create()` 返回**已打开**的连接，并已设 `journal_mode=WAL` 与 `foreign_keys=ON`。
- 数据库路径由 `StoragePaths` 从配置与运行环境解析（生效的是 `src/AgentWorkbench.Webapi/data/agentworkbench.db`，
  根目录的 `data/` 是旧的）。

### 迁移怎么跑

- FluentMigrator，扫描本程序集的 `Migrations`，编号 `M0xx` 递增；启动时 `InitializeDatabase()`
  在建好 app、收请求之前 `MigrateUp()`（见 Webapi `Program.cs`）。
- `Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true`，所以列名用 snake_case。

现有表与迁移：

| 迁移 | 表 | 干什么 |
| --- | --- | --- |
| M001 / M002 / M003 / M004 | `documents` / `document_pages` / `chunks` / `chunks_fts` | 文档管道与 FTS5 全文索引 |
| M006 | `providers` / `provider_models` | 模型网关 |
| M014 / M022 | `agent_runs`（+ `error_code`） | 一次 Harness run 的状态与归类码 |
| M015 / M021 | `agent_steps`（+ `error`） | run 的每一步：工具 / 参数 / 结果 / 失败原因 |
| M016 | `conversations` / `messages` | 对话历史（刷新、重启后可恢复） |
| M017 | `workflow_definitions` | 工作流定义 |
| M018 | `prompt_versions` | 提示词版本 |
| M019 | `workflow_runs` | 工作流运行 |
| M020 | `workflow_steps` | 步骤级状态（= 检查点） |

> 迁移号不连续（缺 M005 / M007–M013）：那是剥离垂直模块后留下的空号，不是丢文件。

### 仓储有哪些

`IDocumentRepository` / `IProviderRepository` / `IAgentRunRepository` / `IConversationRepository` /
`IWorkflowRepository`，都在 `AddNitroPersistence` 里注册。仓储只做数据访问，不放业务判断。

---

## 二、还没处理的问题（接下来要干的活）

| 问题 | 不处理的后果 | 现在什么状态 | 做好的标准 |
| --- | --- | --- | --- |
| 没有会话表 | Harness 的会话重启即丢 | 无表、无仓储 | 新表 + 仓储（P1-3，注意 `Memory` 的依赖方向约束） |
| 执行记录不能按条件查 | 排查失败只能翻库 | `IAgentRunRepository` 只有按 id 取 | 查询方法 + 索引（P3-3） |
| 对话与轨迹不关联 | 三条历史轨道对不上号 | `conversations.id` 与 `agent_runs.session_id` 各写各的 | 关联方案（加列或统一 id） |
| 一次 run 的写入不是原子的 | 可能留下半条记录 | run 与 steps 走两条连接 | 收进同一事务 |
| 没有持久化层测试 | 迁移 / 仓储改坏没人发现 | 无集成测试项目 | 临时库上跑「迁移 + 仓储往返」 |
| `messages` 只涨不落 | 表无限增长 | 只有外键级联删除 | 保留策略 |
| 时间统一存 ISO 字符串 | 排序与时区靠约定 | 全是 UTC `o` 格式 | 写进口径说明并统一 |

---

## 三、加一张表要做四件事

1. 新迁移 `M0xx`（下一个递增号，禁止改已发布的迁移）。
2. 需要的领域记录（`Domain`）。
3. 仓储接口 + 实现。
4. `AddNitroPersistence` 注册。

---

## 四、红线

1. **数据库变更必须走 FluentMigrator**，不允许手写 DDL 直接改库。
2. **不改已发布的迁移**，只加新号；回滚逻辑 `Down()` 要能对上 `Up()`。
3. **仓储不放业务判断**，也不吞异常。
4. **不提交** `bin/` `obj/` `node_modules/` `dist/` 与数据库文件。

---

## 五、怎么算验收通过

判定型用例必须正例能绿、负控能红；能由脚本判定的禁止用 AI 判。

目前覆盖到的：只有架构测试（分层与无环）覆盖本模块 —— 即「依赖方向没走反」。

应该补上的（需要 SQLite 测试夹具）：

- 正例：空库跑一遍全部迁移 → 表齐全 → 仓储写入后能按 id 读回。
- 负控：`Down()` 之后再 `Up()` 必须幂等成功（证明迁移可回滚）。
- 负控：写到不存在的 `conversation_id` 必须因外键失败，而不是静默写入孤儿行。

## 依赖

Domain、Shared。


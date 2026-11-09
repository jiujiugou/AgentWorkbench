# AGENTS.md

> 最后校准 2026-11-09。定位与范围以 `docs/需求/产品目标与范围.md`（**v3.0 · 已确认**）为**唯一正本**；本文只写工作规则与已核实的代码事实，不重新定义需求。文档入口、分线维护与变更台账见 `docs/README.md`；**写文档前先读它的 §6（全仓唯一一处写法定义）**。

## 写文档前（三条硬规矩）

1. 先读 `docs/README.md`：分线、三类寿命、状态位三要素。
2. 模块文档只照它的 §6 写法；**任何局部文档不再重复定义这套规矩**（含 `core/*.md`）。
3. 改完文档跑一次测试：`DocsHygieneTests` 校验状态位、相对链接、以及「局部文档重定义写法」，缺状态位直接红。

## 项目
AgentWorkbench：**面向企业私有化部署的受控动作执行底座**。对外交付「底座 + 一个动作域」的完整系统；第一动作域是制造业 ERP（ERPNext 自托管），不是产品本身。

一句话方向：**不做「会聊 ERP 的 AI」，做「能让 AI 安全碰 ERP 的那一层」** —— 保证不越权、不重复执行、办错了能追回。

底座五层（Runtime/Harness、编排、知识、记忆与状态、工程保障）+ 工具层「管辖」半边**完全不认识业务**；业务语义全部集中在动作域。C# 主服务是唯一对外入口、唯一状态持有者、唯一 PDP；Python 侧为无状态能力进程（ADR 0013，见 Py 仓）。

## 事实源状态（2026-11-09 校准）
| 类别 | 状态 |
|---|---|
| **文档入口** | `docs/README.md` —— 阅读顺序、**底座 / 应用分线维护**、三类寿命、执行计划模板 |
| 正本 | `docs/需求/产品目标与范围.md` v3.0 · **已确认**；**当前工作区是删除态**，仓库 `HEAD` 仍是全文（`git show "HEAD:docs/需求/产品目标与范围.md"` 可取）；11-08 的精简版从未提交 |
| 不变量 | **无独立文件**；三条方向判据与四条边界在正本 §1–§3（`constitution.md` 11-08 已移除，未重建） |
| 现状 | `docs/core/runtime.md`（v1 知识问答链）/ `docs/core/tools.md`（v3 受控动作链）；`底座.md` / `应用.md` 已移除，未重建 |
| 过程 | `docs/变更台账.md`（只追加）/ `docs/plans/`（每轮一份，做完归档，不删不改） |
| ADR 0013–0018 + 能力矩阵 | **不在本仓**，在 `D://Code//AgentWorkbench.Py//docs//adr//` 与 `docs\架构\` |
| 本仓 `docs/adr/` | 只有 0001–0007（全 F2，v1）；编号 10-29 后重排 |
| 冻结 / 不作判据 | `docs/REMADE.md`（v1 导航）、v1 功能需求与 33 条 REQ、阶段1 设计、113 条验证编号 |
| **已移除的引用一律作废** | 本文下方凡出现 `docs/需求/M0-阶段报告.md`、`docs/设计/*`、`docs/验证与现状/*`、`M1/M2 设计 §x`、`（M0 报告 L2）` 等字样，指向的文档 11-08 已从工作区删除、**保持删除不恢复**，原件在 git `8911c88`；不再作依据 |
| 不存在，别引用 | 根 `README.md`、`docs/接口/`、`docs/测试标准/`、`docs/架构调整/`、`docs/底座建设清单.md` 等；旧 21 份文档 10-29 被清空且未提交 git，不可恢复 |
| 模块 README | 内容滞后（仍用 Providers / AgentLoop / Safety 等旧名），只当轮廓，不当事实源 |

## 技术栈
.NET 10 / ASP.NET Core / Microsoft Agent Framework / Microsoft.Extensions.AI / SignalR
SQLite + FTS5 + sqlite-vec（仓储层 10-31 起试点 EF Core，迁移现状见 `Persistence/Migrations` M001–M030）
Vue 3 + Vite + TypeScript + Element Plus

**身份与鉴权**：ASP.NET Core Identity 全套——`AddIdentityCore<AppUser>` + `IdentityDbContext` + EF store，表名 `aspnet_*`（M029）。密码哈希、登录、失败锁定、角色全部由框架负责，**仓内没有自写哈希、没有自建用户表**。业务扩展只有 `AppUser.TenantId` / `DisplayName` / `IsActive`，由 `AppUserClaimsPrincipalFactory` 补成 `tenant_id` / `GivenName` Claims。演示账号 `wang`（owner）/ `zhao`（member）/ `li`（owner），统一密码 `Demo1234`。

## 构建与测试
```
dotnet build AgentWorkbench.slnx --no-restore --nologo
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-restore --no-build --nologo
cd web; npm run build
```
- **本会话 shell 的 restore 仍坏**（`NuGet.targets(780)` 的 `path1` 为空，补齐环境变量无效），只能走 `--no-restore`；不带该参数的构建需用户终端复核。新建的零依赖工程（Ports / ActionGateway / Erp / FakeActionSet / Gitea）的 `obj/project.assets.json` 由零依赖模版复制改写生成，该文档 11-08 已移除，原件在 git `8911c88`。
- `AgentWorkbench.slnx` 按 Core / Domains / Host 分组；项目数以 `AgentWorkbench.slnx` 实测为准，不在此写死；`Runtime.Harness` 下重复的 `AgentWorkbench.AgentHarness.csproj` 已删。
- 旧一级目录 `src/AgentWorkbench.*`（17 个）已于 11-08 全部清除，无残留。
- 另 4 个测试项目（ApiContract / Architecture / DataIntegration / EndToEnd）在 11-07 22:18 被清空并按删除态提交；**架构门禁现由 `ApplicationTests/ActionGateway/DomainSwapAc01Tests.cs` 承担**，是否重建独立 `ArchitectureTests` 待用户决定（M0 报告 L2）。
- `scripts/` 现有 6 个文件（dev.sh / demo.sh / probe-hub.mjs / schema_dump.py / contract_matrix.py / **erp_live_probe.py**）；历史文档引用的 `acceptance-*.ps1` / `eval-*.ps1` **均未实现**。
- ERPNext 采集/验证：`python scripts/erp_live_probe.py --verify`（只读、退出码即结论）/ `--record`（采集并生成夹具与证据）。需要 `ERPNEXT_BASE_URL` / `ERPNEXT_API_KEY` / `ERPNEXT_API_SECRET`；**无实例时不要手写夹具**（依据文档 11-08 已移除，原件在 git `8911c88`）。
- 旧文档里的 `tests/AgentWorkbench.UnitTests`、`tests/AgentWorkbench.MockLlm` 已不存在。

## 目录布局（2026-11-07 分层重组）
```
src/
  Core/                  底座，业务无关，换域时整块不动
    Ports/               ← 唯一允许 Domains import 的工程（零依赖）：三类型 + 四道校验口
                           + run 状态机 / 审计反查契约；ErrorEnvelope 仍是字段待定的占位
    Foundation/          Shared · Domain · Persistence
    Runtime/             Harness · Loop · Model · Context · Session · Tools
    Orchestration/       Workflow
    Tools/               Tools.Abstractions · Tools.Builtin · ActionGateway（M1 四道校验 + 六步链）
    Knowledge/           Knowledge.Abstractions · Local · Support · Documents
    Memory/              Memory.Persistence
    Engineering/         Observability
  Domains/
    Erp/                 ← 第一动作域（M1 假实现），换域时整个换掉
    FakeActionSet/       ← 第二套假动作集，AC-01 换域演练载体
    Gitea/               ← 第三动作域：gitea.com 真实实例，方案第 1/2/3 步样板（业务对象粒度能力目录）
  Host/
    AgentWorkbench.Webapi/   组合根
```
- **依赖方向**：`Domains → Core.Ports` 单向；`Core` 下任何项目不得 import `Domains`（ADR 0017 依赖倒置：底座定义端口，业务侧实现并注入）。跨边界不共享业务类型，`ActionSpec` 参数对底座是不透明 JSON。
- `Core/Foundation/AgentWorkbench.Domain` 装的仍是 v1 文档业务记录（DocumentRecord / ChunkRecord / WorkflowRecords），是已知遗留；`Runtime.Model` 引用了它但零使用，不应长期留在底座基础层。
- `Ports` / `Domains/Erp` / `Domains/FakeActionSet` / `Domains/Gitea` 均已是真实工程，目录里的 `.gitkeep` 不再代表占位。
- **能力目录契约（11-08）**：`ActionSpec` 除四个位置参数外，还有 6 个 `required init` 字段（DisplayName / Description / RiskLevel / RequiresApproval / Version / InputSchemaJson），编译器强制每个域补齐。`IActionSpecRegistry` 有 `ListAsync`。`RiskLevel` **只用于展示，禁止参与决策**；审批看 `RequiresApproval`，与 `SideEffecting` 解耦。

## 模块（24 个项目，按层；下表用简称，实际路径见上）
| 层 | 模块 | 职责 |
|---|---|---|
| 边界 | Ports | 底座与动作域唯一契约：三类型 / 四道校验口 / TraceEvent / run 状态机 / 审计反查；零依赖零业务词 |
| 基础 | Shared · Domain · Tools.Abstractions | 配置常量（零依赖）/ 纯领域 Records（→ Shared）/ 工具契约（零依赖） |
| 基础设施 | Persistence · Runtime.Model · Observability | 连接+M001–M026 迁移+仓储 / 模型调用统一语义（超时·重试·并发·用量）/ 执行轨迹落库与回放 |
| 执行控制 | Runtime.Context · Runtime.Loop · Runtime.Session · Runtime.Tools · Tools.Builtin · ActionGateway | 上下文组装（消息/指令/片段预算）/ 迭代预算与 run 级超时 / 会话状态 / 函数调用环+白名单+审批 / 通用内置工具 / M1 四道 fail-closed 网关 + run 状态机 + 六步链 |
| 知识 | Knowledge.Abstractions · Knowledge.Support · Knowledge.Local | 引擎·Embedding·重排契约 / 上下文组装（历史+本轮输入）/ FTS5 + sqlite-vec + RRF 本地引擎 |
| 数据 | Documents | 上传 → 队列 → 解析 → 清洗 → 切分 |
| 编排 | Runtime.Harness · Workflow · Memory.Persistence | 装配 model/tools/context/session/loop/trace（自身不实现能力）/ 步骤编排·单步重跑·提示词版本·重试·检查点 / 会话快照与长期记忆落库 |
| 宿主 | Webapi | 组合根：Program.cs / Controllers / SignalR Hubs |
| 动作域 | Erp · FakeActionSet · Gitea | 假 ERP 四原语 / 第二套假动作集 / **gitea.com 真实 Connector + 业务对象粒度能力目录**；只依赖 Ports，承载全部业务语义 |

## 依赖方向（csproj 实测 2026-11-07，禁止反向/循环）
```
Shared / Tools.Abstractions / Ports   ← 零依赖（Ports 的 ProjectReference 恒为 0，M1 门禁 4）
ActionGateway                        → Ports（底座侧，不得引用 Domains）
Erp / FakeActionSet / Gitea        → Ports（且只能这一条）
Domain                            → Shared
Runtime.Context                   → Shared
Persistence                       → Domain, Shared（+ FrameworkReference ASP.NET Core、Identity EF store）
Runtime.Model                     → Shared, Domain, Persistence
Observability                     → Domain, Persistence, Shared
Runtime.Loop / Runtime.Session    → Shared
Runtime.Tools                     → Shared, Tools.Abstractions
Tools.Builtin                     → Tools.Abstractions
Knowledge.Abstractions            → Domain, Shared
Knowledge.Support                 → Domain, Runtime.Model, Runtime.Tools, Shared
Knowledge.Local                   → Domain, Knowledge.Support, Knowledge.Abstractions,
                                    Persistence, Runtime.Model, Runtime.Tools, Shared
Documents                         → Domain, Knowledge.Abstractions, Persistence, Shared, Tools.Abstractions
Memory.Persistence                → Runtime.Session, Persistence, Shared, Tools.Abstractions
Runtime.Harness                   → Runtime.Loop, Runtime.Session, Runtime.Model,
                                    Observability, Runtime.Tools, Shared
Workflow                          → Runtime.Harness, Knowledge.Abstractions, Persistence, Domain, Shared
Webapi                            → 几乎所有模块（组合根）
```

## 扩展点
**以代码里实际存在的接口为准**：旧笔记列出的 `IModelRuntime` / `IAgentLoop` / `IContextBuilder` / `ISessionStore` / `IToolCatalog` / `IStructuredChatService` / `AddNitro*` 等名称部分已随重命名迁移或合并，使用前先 grep 确认，不要照抄。
保留原则：新增能力 = 加类 / 加模块，0 改旧代码；模块间只依赖对方契约。

## 工作规则
1. 先调查 2. 最小修改 3. 验证（build + 架构测试 + 验收）4. 停止
- 测试双轨是**约定**（原 `docs/测试标准/` 已不存在）：判定型（绿/红 + 正例绿·负控红）与度量型（指标+阈值+趋势）；LLM/检索/生成类改动必须更新度量指标。关键行为先写不变量，负控必须能变红。**能由脚本/程序判定的测试禁止使用 AI（含 LLM-as-judge），LLM 断言用 mock/回放。**
- v3 反馈源纪律：外部设计的单据模型 > mock > 绿灯，排序不能倒；**功能数、覆盖率、绿灯数、文档篇数不是成功标准**。
- 关键红线：底座五层不得出现业务概念（AC-02，禁词扫描进 CI）；换域时底座五层 diff 为空（AC-01）；写动作默认 `Ask` + fail-closed，**禁止自动重放带副作用的工具调用**。

## 约束
- 数据库变更：现有 FluentMigrator M001–M026；10-31 起仓储层试点 EF Core，新增迁移前先确认当前机制。
- Domain 不得引用基础设施（架构测试强制）。
- 不得提交 `bin/` `obj/` `node_modules/` `dist/`；前端产物 `web/dist` 由 Dockerfile 拷入 wwwroot。
- `.workbuddy/` 含真名 / 简历 / 求职信息，**任何情况下不得提交**；它是本地笔记，不是项目文档。
- ADR 仅记录跨模块、且影响后续模块变迁的架构决策；准入标准见 `docs/adr/README.md`。

## 验收
- 当前判据 = v3 §4.1 六句 + AC-01 / AC-02；**v1 的 33 条 REQ、113 条验证编号随 v1 冻结，不作当前判据**。
- 当前唯一在跑的测试工程是 `ApplicationTests`（条数与红绿以 `dotnet test` 实测为准，不在此写死；红的是功能未实现的预期红，不是失败）。F5 / X 组契约存在性、M1 四道校验、六句成功判据、AC-01 换域与 §7 四条门禁都在其中；边界门禁见 `ActionGateway/DomainSwapAc01Tests.cs`。其他测试工程内容已清空，现状以 `tests/` 目录实测为准。
- 本地跑法（11-01 实录）：`SKIP_BUILD=1 bash scripts/dev.sh`；Hub 要 cookie（种子账号 `wang` / `Demo1234`，cookie `aw.auth`）；**HTTP 问答端点已删，只能 SignalR `Ask`**；M1 动作链走 `/api/actions`（提交 / 待办 / 审批 / 审计反查）；`curl` 打 localhost 加 `--noproxy '*'`。
- 全过退出码 0。

## 参考
- **文档入口**：`docs/README.md`（分线维护规矩、变更台账、当前理解）
- 正本：`docs/需求/产品目标与范围.md`
- ADR：`D:\Code\AgentWorkbench.Py\docs\adr\`（0013–0017）、本仓 `docs/adr/README.md`（0001–0007）
- 历史与决策过程：`.workbuddy/memory/`（本地笔记，不提交）

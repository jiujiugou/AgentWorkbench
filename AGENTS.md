# AGENTS.md

> 最后校准 2026-11-08。定位与范围以 `docs/需求/产品目标与范围.md`（**v3.0 · 已确认**）为**唯一正本**；本文只写工作规则与已核实的代码事实，不重新定义需求。M0 已收口，见 `docs/需求/M0-阶段报告.md`。

## 项目
AgentWorkbench：**面向企业私有化部署的受控动作执行底座**。对外交付「底座 + 一个动作域」的完整系统；第一动作域是制造业 ERP（ERPNext 自托管），不是产品本身。

一句话方向：**不做「会聊 ERP 的 AI」，做「能让 AI 安全碰 ERP 的那一层」** —— 保证不越权、不重复执行、办错了能追回。

底座五层（Runtime/Harness、编排、知识、记忆与状态、工程保障）+ 工具层「管辖」半边**完全不认识业务**；业务语义全部集中在动作域。C# 主服务是唯一对外入口、唯一状态持有者、唯一 PDP；Python 侧为无状态能力进程（ADR 0013，见 Py 仓）。

## 事实源状态（2026-11-08 核实，勿引用不存在的路径）
| 类别 | 状态 |
|---|---|
| 正本 | `docs/需求/产品目标与范围.md` v3.0 · **已确认**（11-08 由待确认转正，M0 退出条件） |
| M0 阶段报告 | `docs/需求/M0-阶段报告.md`（构建/测试/提交/遗留的实测证据） |
| M1 设计 | `docs/设计/M1-受控动作执行流程设计.md` v0.1-draft，**仍未转正、不进事实源表** |
| ADR 0013–0017 + 能力矩阵 | **不在本仓**，在 `D:\Code\AgentWorkbench.Py\docs\adr\` 与 `docs\架构\` |
| 本仓 `docs/adr/` | 只有 0001–0007（全 F2）；编号 10-29 后重排，准入标准见其 README |
| 已冻结（v1 知识工作台，不作当前判据） | `docs/REMADE.md`、`docs/需求/功能需求.md`（6 域 33 条 REQ）、`docs/需求/阶段1功能需求.md`、`docs/设计/阶段1/`、`docs/验证与现状/`（v3 §8） |
| **不存在，别引用** | 根 `README.md`、`docs/设计/README.md`、`docs/接口/接口.md`、`docs/测试标准/`、`docs/架构调整/`、`docs/底座建设清单.md`、`docs/设计步骤/`、`docs/验收标准/`。旧 21 份文档 10-29 被清空且未提交 git，不可恢复 |
| 模块 README | 内容滞后（仍用 Providers / AgentLoop / Safety 等旧名），只当轮廓，不当事实源 |
| 本仓 docs | 共 15 份 md |

## 技术栈
.NET 10 / ASP.NET Core / Microsoft Agent Framework / Microsoft.Extensions.AI / SignalR
SQLite + FTS5 + sqlite-vec（仓储层 10-31 起试点 EF Core，迁移现状见 `Persistence/Migrations` M001–M026）
Vue 3 + Vite + TypeScript + Element Plus

## 构建与测试
```
dotnet build AgentWorkbench.slnx
dotnet test tests/AgentWorkbench.ArchitectureTests
dotnet test tests/AgentWorkbench.ApplicationTests
dotnet test tests/AgentWorkbench.ApiContractTests
dotnet test tests/AgentWorkbench.DataIntegrationTests
dotnet test tests/AgentWorkbench.EndToEndTests
cd web; npm run build
```
- `AgentWorkbench.slnx` 按 Core / Domains / Host 分组收 **19 个** src 项目（含 `Tools.Abstractions` / `Tools.Builtin`）；`Runtime.Harness` 下重复的 `AgentWorkbench.AgentHarness.csproj` 已删。
- **旧一级目录 `src/AgentWorkbench.*`（17 个）因文件句柄锁定暂未删除**，不参与编译与门禁统计，需在能关掉 dotnet/IDE 的终端手动清理；删除后可把架构测试的项目计数扫描根收紧回 `src`。
- `scripts/` 现有 5 个文件（dev.sh / demo.sh / probe-hub.mjs / schema_dump.py / contract_matrix.py）；历史文档引用的 `acceptance-*.ps1` / `eval-*.ps1` **均未实现**，端到端验收脚本仍未落地。
- 旧文档里的 `tests/AgentWorkbench.UnitTests`、`tests/AgentWorkbench.MockLlm` 已不存在。

## 目录布局（2026-11-07 分层重组）
```
src/
  Core/                  底座，业务无关，换域时整块不动
    Ports/               ← 占位：唯一允许 Domains import 的项目，M1 建 csproj
                           （PermissionQuery / ActionSpec / TraceEvent + 五字段错误信封）
    Foundation/          Shared · Domain · Persistence
    Runtime/             Harness · Loop · Model · Context · Session · Tools
    Orchestration/       Workflow
    Tools/               Tools.Abstractions · Tools.Builtin
    Knowledge/           Knowledge.Abstractions · Local · Support · Documents
    Memory/              Memory.Persistence
    Engineering/         Observability
  Domains/
    Erp/                 ← 占位：第一动作域，换域时整个换掉
  Host/
    AgentWorkbench.Webapi/   组合根
```
- **依赖方向**：`Domains → Core.Ports` 单向；`Core` 下任何项目不得 import `Domains`（ADR 0017 依赖倒置：底座定义端口，业务侧实现并注入）。跨边界不共享业务类型，`ActionSpec` 参数对底座是不透明 JSON。
- `Core/Foundation/AgentWorkbench.Domain` 装的仍是 v1 文档业务记录（DocumentRecord / ChunkRecord / WorkflowRecords），是已知遗留；`Runtime.Model` 引用了它但零使用，不应长期留在底座基础层。
- `Ports` / `Domains/Erp` 目前只有 `.gitkeep`，不是项目；M1 契约阶段才填代码。

## 模块（19 个项目，按层；下表用简称，实际路径见上）
| 层 | 模块 | 职责 |
|---|---|---|
| 基础 | Shared · Domain · Tools.Abstractions | 配置常量（零依赖）/ 纯领域 Records（→ Shared）/ 工具契约（零依赖） |
| 基础设施 | Persistence · Runtime.Model · Observability | 连接+M001–M026 迁移+仓储 / 模型调用统一语义（超时·重试·并发·用量）/ 执行轨迹落库与回放 |
| 执行控制 | Runtime.Context · Runtime.Loop · Runtime.Session · Runtime.Tools · Tools.Builtin | 上下文组装（消息/指令/片段预算）/ 迭代预算与 run 级超时 / 会话状态 / 函数调用环+白名单+审批 / 通用内置工具 |
| 知识 | Knowledge.Abstractions · Knowledge.Support · Knowledge.Local | 引擎·Embedding·重排契约 / 上下文组装（历史+本轮输入）/ FTS5 + sqlite-vec + RRF 本地引擎 |
| 数据 | Documents | 上传 → 队列 → 解析 → 清洗 → 切分 |
| 编排 | Runtime.Harness · Workflow · Memory.Persistence | 装配 model/tools/context/session/loop/trace（自身不实现能力）/ 步骤编排·单步重跑·提示词版本·重试·检查点 / 会话快照与长期记忆落库 |
| 宿主 | Webapi | 组合根：Program.cs / Controllers / SignalR Hubs |

## 依赖方向（csproj 实测 2026-11-07，禁止反向/循环）
```
Shared / Tools.Abstractions        ← 零依赖
Domain                            → Shared
Runtime.Context                   → Shared
Persistence                       → Domain, Shared
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
- 单元/契约：`ApplicationTests` / `ApiContractTests`；边界：`ArchitectureTests`；集成/端到端：`DataIntegrationTests` / `EndToEndTests`。
- 本地跑法（11-01 实录）：`SKIP_BUILD=1 bash scripts/dev.sh`；Hub 要 cookie（种子账号 `wang` / `demo1234`，cookie `aw.auth`）；**HTTP 问答端点已删，只能 SignalR `Ask`**；`curl` 打 localhost 加 `--noproxy '*'`。
- 全过退出码 0。

## 参考
- 正本：`docs/需求/产品目标与范围.md`
- ADR：`D:\Code\AgentWorkbench.Py\docs\adr\`（0013–0017）、本仓 `docs/adr/README.md`（0001–0007）
- 历史与决策过程：`.workbuddy/memory/`（本地笔记，不提交）

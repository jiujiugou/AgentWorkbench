# AGENTS.md

> **校准基线：`b4d5f2c`（2026-11-10）**。本文只写**工作规则、判断、约束**；不写「代码里有什么」。
> 需要事实时查代码或运行 §1 的命令。**不要相信本文里的路径、数量、版本号、入口名**——它们会漂。
> 重核方法：`git log b4d5f2c..HEAD --oneline`；非空只说明基线之后又有提交，按 §1 重核相关事实即可，
> 不必通读全文。
> 定位与范围以 `docs/需求/产品目标与范围.md`（v3.0）为**唯一正本**；本文不重新定义需求。

## 0. 文档与代码冲突时（最高优先级，先读这条）

> When a repository's documentation and its existing code disagree, follow the conventions used in the existing code, and in your final reply point out each inconsistency you found between the documentation and the code.

本文件是**给 agent 读的指令文件**，不是事实源。它会以「指令」身份进入上下文，而代码只是工具输出——模型被训练成优先听指令，所以一句过期描述足以让 agent 去调一个已删的方法、或不敢执行本来能跑的命令。**先信代码，并把发现的每一处不一致列在最终回复里**，让漂移变成待办，而不是沉默的坑。

这句只对本项目生效。要覆盖所有仓库，把同一句加到用户级 `~/.codex/AGENTS.md`（本文件不改你的主目录）。

注意方向：本规则假定**文档是过期的那一半**。若仓库正处于迁移中、文档写的是目标态而代码是待淘汰的旧态，不要套用——此时由用户明确说明。

## 1. 事实从哪来（别抄进文档）

判定标准一句话：**agent 能靠读代码或跑一条命令发现的，就不写进文档。**

| 想知道什么 | 怎么取 |
| --- | --- |
| 项目清单 / 依赖方向 | `dotnet sln list`；解析 `*.csproj` 的 `ProjectReference`；门禁见 `DomainSwapAc01Tests` |
| 目录布局 / 文件数 | `ls src`、`git ls-files` |
| 迁移号、脚本清单 | `ls src/Core/Foundation/AgentWorkbench.Persistence/Migrations/`、`ls scripts/` |
| 路由与端点 | 在 `Controllers/*.cs` 里 grep `[Route` / `[Http`；Hub 方法名看 `Hubs/AgentHub.cs` |
| 工具清单与白名单 | `ToolCatalog`、`appsettings.json` 的 `Harness:Tools:Whitelist` |
| MCP server 与开关 | `appsettings.json` 的 `Mcp:Servers` / `Mcp:Enabled` |
| 种子账号 | `Program.cs` 的 `SeedDemoIdentityAsync` |
| 当前校准点是否过期 | `git log b4d5f2c..HEAD --oneline` |

`scripts/contract_matrix.py`、`scripts/schema_dump.py` 是现成的事实生成脚本；优先扩它们，不要手写结果表格。

## 2. 定位（判断，不是能力清单）

**面向企业私有化部署的受控动作执行底座**：把「模型想做什么」变成「系统真的做了什么」，保证不越权、不重复执行、办错了能追回。
一句话方向：**不做「会聊 ERP 的 AI」，做「能让 AI 安全碰 ERP 的那一层」**。

底座业务完全无关，动作域承载全部业务语义；底座不单独交付。AI 附加到现有产品的前提：**AI 对现有产品的理解必须来自现有产品本身**，手抄的语义会漂移且漂移时不报错。

三条方向判据：换域底座 diff 为空（AC-01）；任一动作能答「谁 / 何时 / 看到什么 / 依据哪条规则 / ERP 里变成什么」；**拿掉模型，系统还能安全地拒绝**。四条边界、六句成功判据见正本 §4.1，**不在此复述**。

判断某段代码或某条处置属底座还是属域，只按 [docs/归属判据.md](docs/归属判据.md) 的四句顺序判，不另立标准。

## 3. 事实源与冻结

- 文档入口与写法：`docs/README.md`（全仓唯一一处写法定义，写文档前先读 §6）。
- 正本 `docs/需求/产品目标与范围.md` **当前工作区是删除态**，`HEAD` 有全文：`git show "HEAD:docs/需求/产品目标与范围.md"`。
- 过程记录：`docs/plans/`（每轮一份，做完归档，不删不改）。
- ADR 0013–0018 与能力矩阵**不在本仓**，在 `D:\Code\AgentWorkbench.Py\docs\adr\` 与 `docs\架构\`；本仓 `docs/adr/` 只有 0001–0007（v1）。ADR 0013「双进程」是设计决定，**尚未实施**。
- 冻结、不作判据：v1 功能需求与 33 条 REQ、阶段 1 设计、113 条验证编号（v1 导航 REMADE 与变更台账已删除，原件在 git 历史）。
- 模块 README 是 **explanation**（为什么这么设计），不是事实源；不要拿它确认接口、入口或配置。

## 4. 写文档 / 改代码的规矩

1. **同一次提交改代码与对应文档**。commit message 写明「文档已校准」或「未涉及文档」；单人项目不另设评审流程。
2. 文档只写**判断、取舍、边界、为什么**。凡是「有哪些模块 / 默认值多少 / 哪个类在哪 / 谁调用谁」，删掉或改成 §1 的命令。
3. **只有陈述现行的符号才带反引号**；已移除、待实现、外部系统的端点一律写纯文本或加 `已移除` / `当前不存在` 前缀。这样一致性检查才不会把合法的历史记录误判成失真。
4. 状态位写 **commit hash**，不写裸日期。日期可以被改而不改正文，hash 不能；hash 只作审计线索，不作保证。
5. 文档按 **tutorial / how-to / reference / explanation** 分开；reference 交给脚本生成或指向命令，explanation 手写且很少变。混在一篇里是最常见的失败形态。
6. 改完跑一次测试。`DocsHygieneTests` 只校验 `docs/` 的**格式卫生**（状态位、相对链接、不重定义写法）；**通过它不等于文档与代码一致**，别拿绿灯当事实核对。
7. 新增边界时**扩展 `ActionGateway/DomainSwapAc01Tests.cs`**，不要新建「检查文档有没有标题」这类门禁，也不要重建 `ArchitectureTests`（项目已空）。失败信息必须点名**被违反的那条边**，而不是「缺标签」。
8. 每写一条新能力，先问「有没有能变红的负控」；没有就不写。

## 5. 架构红线（约束，不随代码漂移）

- `Domains → Core.Ports` 单向；`Core` 下任何项目**不得** import `Domains`（ADR 0017）。跨边界不共享业务类型，`ActionSpec` 参数对底座是不透明 JSON。
- 底座五层不得出现业务概念（AC-02 禁词扫描）；换域时底座五层 diff 为空（AC-01）。**这两条由 `DomainSwapAc01Tests` 承担架构门禁**（含源码哈希快照与负控自证）。
- 四道闸门（Schema / Permission / BusinessRule / Audit）必经且 fail-closed；**人工审批按
  `ActionSpec.RequiresApproval` 逐个配置，不是全局铁律**（草稿可能免审批）。
  **禁止自动重放带副作用的工具调用**。`RiskLevel` 只用于展示，不得参与决策。
- 工具白名单默认拒绝。MCP 工具**直调、不走四道闸门、不入 `action_trace`**：只接只读工具，写工具必须包装成 `ActionSpec` 经 `propose_action` 提出。
- `clientMessageId` 是动作判重的起点；新增任何动作入口前先回到 Host 的输入身份契约，把身份放进作用域。

## 6. 构建与验证

```
dotnet build AgentWorkbench.slnx --nologo
dotnet test tests/AgentWorkbench.CoreTests/AgentWorkbench.CoreTests.csproj --no-build --nologo        # 内核模块级单测
dotnet test tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj --no-build --nologo  # 流程验收 Sxx + 宿主 + 域 + 文档门禁
cd web && npm run build
```

- **两个测试项目的分法不是按大小，是按被验对象**：`CoreTests` 装 `src/Core` 里模块自己的行为（只引 Core，**不得**出现 Host / Domains 引用，加了就说明归属判错），目录**镜像 `src/Core`**（`Runtime/Tools`、`Runtime/Context`、`Memory/Persistence`…），一个测试目录对上一个项目；`ApplicationTests` 装运行期流程的逐格验收（`S01`–`S10`）、宿主、域与文档门禁。**流程验收一律在 `ApplicationTests`**，即使被测代码在 Core。
- 两边共用的测试件（脚本化模型、工具环装配器）在 `tests/AgentWorkbench.TestKit`，不复制第二份。单格状态用 `python scripts/stage_gate.py Sxx`，它按 key 选项目。

- 优先用普通 `build`；若本机 restore 环境异常再退回 `--no-restore`（**不要预先假定 restore 一定坏**，用一次试一次）。
- 前端热更新走 `npm run dev`（5173），后端 `bash scripts/dev.sh`（8080），两者需同时在线；`curl` 打 localhost 加 `--noproxy '*'`。后端改动必须重启，热更新只管前端。
- 测试双轨：判定型（正例绿 + **负控能红**）与度量型（指标 + 阈值 + 趋势）。LLM / 检索 / 生成类改动必须更新度量指标。**能由脚本判定的禁止用 AI 判定**，LLM 断言用 mock / 回放。
- 验收 = 正本 §4.1 六句 + AC-01 / AC-02。**功能数、覆盖率、绿灯数、文档篇数一律不是成功标准**；红灯中有一批是「功能未实现」的预期红，不是失败。

## 7. 约束

- `.workbuddy/` 含真名、简历、求职信息，**任何情况下不得提交**；它是本地笔记，不是项目文档。
- 不提交 `bin/` `obj/` `node_modules/`；前端产物由 vite 直接生成到 `Webapi/wwwroot`（已 gitignore）。
- 数据库变更：FluentMigrator 与 EF Core 仓储混用，新增迁移前**先确认当前机制**，别照旧笔记写死迁移号。
- Python 工具侧 MCP server 放**主仓** `tools/mcp-rag/`，由 C# Webapi 的 `Mcp:Servers` 以 stdio 拉起；`.venv` 已 gitignore，clone 后按 `requirements.txt` 重建。这是落点决策，不是 Py 仓的子模块。
- ADR 仅记录跨模块、且影响后续模块变迁的架构决策；准入见 `docs/adr/README.md`。

## 8. 运行时入口（会变，以代码为准）

起 run 的产品入口是 SignalR `/hubs/agent`；**方法名、参数体、可用事件一律以 `Hubs/AgentHub.cs` 与前端 `stores/agent.ts` 为准**，不要引用本文或旧文档里的名字。硬约束只有一条：**不得再新增用 HTTP 起 run 的入口**（`InputIdentityContractTests.H11` 用反射守着）。

动作链走 `/api/actions`（提交 / 待办 / 审批 / 审计反查）；只读运行详情走 `/api/harness/runs/{id}`。端点清单按 §1 取，别抄。

## 9. 参考

- `docs/README.md`（文档入口）、正本 `docs/需求/产品目标与范围.md`
- ADR：`D:\Code\AgentWorkbench.Py\docs\adr\`、本仓 `docs/adr/README.md`
- 历史与决策过程：`.workbuddy/memory/`（本地笔记，**不提交**）

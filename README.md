# AgentWorkbench

通用 AI Agent 工作台底座（MVP）：接任意 OpenAI 兼容大模型，提供**流式对话**、**在线增删供应商/模型并免重启切换**、**PDF 数据管道（上传→解析→清洗→切分→预览）**、**知识库 RAG（FTS5 + sqlite-vec 混合检索 + 带引用问答）**，以及基于 Microsoft Agent Framework 的 **Agent Harness**（循环/预算/工具/轨迹）与 **Workflow**（步骤编排/单步重跑/提示词版本/重试/检查点）。

定位：**通用底座，零垂直业务语义**。新增能力 = 新增模块，模块间只依赖对方契约。

- 后端：ASP.NET Core (.NET 10) + Microsoft Agent Framework + Microsoft.Extensions.AI + SignalR
- 前端：Vue 3 + Vite + TypeScript + Element Plus
- 存储：SQLite（Dapper + FluentMigrator）/ FTS5 + sqlite-vec

> 架构决策见 [`docs/adr/`](docs/adr/)；测试与验收标准见 [`docs/测试标准/Agent-测试与验收标准.md`](docs/测试标准/Agent-测试与验收标准.md)。

## 目录结构

多项目模块化单体：模块间只依赖对方契约，新增能力 = 新增模块。

```
AgentWorkbench/
├─ AgentWorkbench.slnx
├─ global.json                        # 固定 .NET SDK 版本
├─ Directory.Build.props
├─ docs/                              # 设计 / 验收 / 测试标准 / ADR
├─ src/
│  ├─ AgentWorkbench.Shared/          # 配置/常量（被所有模块依赖）
│  ├─ AgentWorkbench.Domain/          # 纯领域：Records / ChatTurn
│  ├─ AgentWorkbench.Persistence/     # 连接 + FluentMigrator 迁移 + 仓储
│  ├─ AgentWorkbench.Documents/       # PDF 数据管道（上传/解析/清洗/切分）
│  ├─ AgentWorkbench.Knowledge.Abstractions/  # 知识契约：IKnowledgeEngine/IEmbeddingProvider/IReranker
│  ├─ AgentWorkbench.Knowledge.Local/ # 本地实现：FTS5 + sqlite-vec + RRF + RAG
│  ├─ AgentWorkbench.Knowledge/       # 聚合：AddNitroKnowledge()
│  ├─ AgentWorkbench.Providers/       # 模型网关：供应商/模型 + IChatClient
│  ├─ AgentWorkbench.Agent/           # 对话 Agent 运行时（Microsoft Agent Framework）+ 工具扩展点
│  ├─ AgentWorkbench.AgentHarness/    # Agent Harness：loop/context/memory/tools/trace
│  ├─ AgentWorkbench.Workflow/        # 步骤编排：重跑/提示词版本/重试/检查点
│  ├─ AgentWorkbench.MockLlm/         # 确定性 OpenAI 兼容 Mock 模型（测试替身，永不调真模型）
│  └─ AgentWorkbench.Webapi/          # 组合根：Controllers / Hubs / Program.cs
├─ tests/
│  ├─ AgentWorkbench.ArchitectureTests/  # ArchUnitNET 边界强制
│  └─ AgentWorkbench.UnitTests/          # 确定性纯逻辑测试
└─ web/                               # Vue 3 前端
```

## 本地运行

### 1. 配置模型（Key 不要提交到仓库）

方式 C · 前端「设置」页在线添加（推荐，**无需重启**）：填名称 / BaseUrl / Key → 添加模型 → 启用。
支持同一供应商下多模型，随时切换；Key 目前**明文存于本地 SQLite**（待加密）。
下方方式 A/B 仅在**首次启动时**作为种子写入，之后以页面配置为准。

方式 A · 环境变量（当前终端会话生效）：

```powershell
$env:Provider__BaseUrl = "https://api.deepseek.com/v1"
$env:Provider__ApiKey  = "sk-你的key"
$env:Provider__Model   = "deepseek-chat"
```

方式 B · .NET User Secrets：

```bash
dotnet user-secrets --project src/AgentWorkbench.Webapi init
dotnet user-secrets --project src/AgentWorkbench.Webapi set "Provider:ApiKey" "sk-你的key"
dotnet user-secrets --project src/AgentWorkbench.Webapi set "Provider:BaseUrl" "https://api.deepseek.com/v1"
dotnet user-secrets --project src/AgentWorkbench.Webapi set "Provider:Model" "deepseek-chat"
```

### 2. 启动后端

```bash
dotnet run --project src/AgentWorkbench.Webapi
# http://localhost:8080  · Swagger: http://localhost:8080/swagger
```

### 3. 启动前端（另开终端）

```bash
cd web
npm install
npm run dev
# 打开 http://localhost:5173
```

前端开发服务器会把 `/api` 与 `/hubs`（含 WebSocket）代理到 `http://localhost:8080`。

## 接口

### 基础 / 模型配置

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/health` | 健康检查 + 模型是否已配置 |
| GET | `/api/settings` | 当前模型配置预览（不含 Key） |
| GET/POST | `/api/settings/providers` | 供应商列表 / 新增 |
| PUT/DELETE | `/api/settings/providers/{id}` | 编辑 / 删除（Key 留空表示不变） |
| POST | `/api/settings/providers/{id}/activate` | 启用为当前供应商 |
| POST | `/api/settings/providers/{id}/models` | 新增模型 |
| DELETE | `/api/settings/models/{id}` | 删除模型 |
| PUT | `/api/settings/providers/{id}/models/{modelId}/default` | 设为当前模型 |
| POST | `/api/settings/providers/{id}/fetch-models` | 拉取该端点模型列表 |
| POST | `/api/settings/providers/{id}/test` | 连通性测试 |
| SignalR | `/hubs/agent` | `Send/Ask({conversationId,text,history})`，事件 `ReceiveSources/ReceiveDelta/ReceiveDone/Error`，`Stop()` 停止生成 |

### 文档管道（PDF）

上传 PDF → PdfPig 抽取 → 清洗（去页眉页脚/合并断行）→ 按条款切分 → SQLite 入库 → 前端预览。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/documents` | 上传 PDF（multipart，字段 `file`） |
| GET | `/api/documents` | 列表（含页数/块数/状态） |
| GET | `/api/documents/{id}` | 详情 |
| GET | `/api/documents/{id}/pages` | 每页 raw + cleaned |
| GET | `/api/documents/{id}/chunks` | 切分块 |
| POST | `/api/documents/{id}/reparse` | 重新解析 |
| DELETE | `/api/documents/{id}` | 删除（含文件与级联数据） |
| GET | `/api/documents/{id}/file` | 原始 PDF |

> 仅支持 PDF **文本层**；扫描件会判失败并提示（不含 OCR）。

### 知识库（RAG）

`chunks` → **FTS5(trigram) 全文 + sqlite-vec 向量** → 混合检索（向量 ∪ BM25 → RRF 融合）→ **带引用问答**。设计见 [`docs/设计步骤/P1知识库.md`](docs/设计步骤/P1知识库.md)。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/knowledge/health` | 引擎 + 能力位 + 索引统计 |
| GET | `/api/knowledge/documents` | 已入库文档（含 `indexed`/`chunkCount`） |
| POST | `/api/knowledge/documents/{id}/index` | 手动/重建索引 |
| POST | `/api/knowledge/retrieve` | 检索 `{ query, topK }` → 片段 + 来源 + 分数 |
| POST | `/api/knowledge/ask` | 带引用问答（非流式）；流式走 SignalR `Ask` |

配置（`Knowledge` 段 / 环境变量）：

| 配置 | 说明 |
| --- | --- |
| `Knowledge:Provider` | 引擎：`local`（默认）/ 其余为预留 stub |
| `Knowledge:Embedding:Provider` | `none`（默认，仅全文）/ `ollama` / `openai` |
| `Knowledge:Embedding:Model` | 默认 `bge-m3`（1024 维） |

本地启用向量（可选，不改也能用全文检索）：

```bash
ollama pull bge-m3
$env:Knowledge__Embedding__Provider = "ollama"   # 或 openai + BaseUrl/ApiKey
```

### Agent Harness（运行时横切轴）

loop/context/memory/tools/trace 的统一运行时（基于 MAF）：循环上限、超时/取消、工具白名单、轨迹落库、RAG 上下文注入。设计见 [`docs/设计步骤/AgentHarness.md`](docs/设计步骤/AgentHarness.md)。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/harness/health` | 能力位（模型是否配置 + 可用工具） |
| POST | `/api/harness/run` | 建一次 Harness 运行（消息 + 预算 + 可选 RAG） |
| GET | `/api/harness/runs/{id}` | 运行详情 + 步骤轨迹 |

### Workflow（步骤编排）

把步骤编排成可重跑、可续跑、提示词可版本化的长任务；步骤类型为 `agent`（托管 Agent Harness，可选 RAG）与 `report`（汇总为 Markdown）。设计见 [`docs/设计步骤/P4工作流.md`](docs/设计步骤/P4工作流.md)。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/workflow/health` | 能力位 `{ workflow, steps[] }` |
| GET | `/api/workflow/definitions` | 工作流定义列表 |
| POST | `/api/workflow/runs` | 创建运行（202 `{ runId }`，入队） |
| GET | `/api/workflow/runs` | 运行列表 |
| GET | `/api/workflow/runs/{id}` | 运行状态 + 错误 |
| GET | `/api/workflow/runs/{id}/steps` | 步骤级状态（检查点） |
| POST | `/api/workflow/runs/{id}/steps/{stepKey}/rerun` | 单步重跑（下游标脏） |
| POST | `/api/workflow/runs/{id}/cancel` | 取消（不挂起） |
| POST | `/api/workflow/runs/{id}/resume` | 断点续跑 |
| DELETE | `/api/workflow/runs/{id}` | 删除运行（级联步骤） |
| GET | `/api/workflow/prompts?key=` | 提示词版本列表 |
| POST | `/api/workflow/prompts` | 新增提示词版本 |
| POST | `/api/workflow/prompts/{key}/{version}/activate` | 设为默认版本 |
| SignalR | `/hubs/workflow` | 步骤级进度事件 |

### 会话

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET/POST | `/api/conversations` | 会话列表 / 新建 |
| GET | `/api/conversations/{id}/messages` | 会话消息 |
| DELETE | `/api/conversations/{id}` | 删除会话 |

## 构建与测试

```bash
dotnet build AgentWorkbench.slnx
dotnet test tests/AgentWorkbench.ArchitectureTests   # 依赖边界 + 无环
dotnet test tests/AgentWorkbench.UnitTests           # 确定性纯逻辑；LLM 走 mock
cd web; npm run build                                # 前端产物
```

> **验收脚本尚未落地**：仓库当前**没有** `scripts/` 目录，历史文档中出现的 `acceptance-*.ps1` / `eval-*.ps1` 均为未实现引用。端到端验收需按 [`docs/测试标准/`](docs/测试标准/) 重新落地；见 [`docs/底座建设清单.md`](docs/底座建设清单.md) 的缺口清单。

## 明确不做

- 不做任何垂直业务模块（底座零业务语义，见 [ADR-0002](docs/adr/0002-remove-vertical-modules.md)）
- 不做模型训练 / 微调 / 模型管理平台
- 不做可视化拖拽工作流设计器
- 不重写 MAF 已有的 Agent Loop / 工具协议 / Workflow 引擎

## 进度

- [x] 脚手架：后端 + 前端 + SignalR 流式
- [x] PDF 数据管道：上传/解析/清洗/切分/入库/预览
- [x] 知识库：`IKnowledgeEngine` 防腐层 + FTS5/sqlite-vec 混合检索 + 带引用问答
- [x] 模型管理：多供应商/多模型在线增删 + 免重启切换（Key 待加密）
- [x] Agent Harness：loop/工具环/轨迹/RAG 注入（基于 MAF）
- [x] Workflow：步骤编排/单步重跑/提示词版本/重试/检查点
- [ ] 落地自动化验收（`scripts/` 与负控）——当前缺口
- [ ] 工具调用扩展（HTTP / SQLite / 文档）+ 前端可视化

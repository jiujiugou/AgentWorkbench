# AgentWorkbench

通用 AI Agent 工作台（MVP）：接任意 OpenAI 兼容大模型，支持**流式对话**与**在线增删供应商/模型、免重启切换**，包含 **P0 PDF 数据管道**（上传→解析→清洗→切分→预览）与 **P1 知识库 RAG**，后续扩展**工具调用**。

- 后端：ASP.NET Core (.NET 10) + Microsoft Agent Framework + Microsoft.Extensions.AI + SignalR
- 前端：Vue 3 + Vite + TypeScript + Element Plus
- 存储：SQLite（Dapper + FluentMigrator）
- 部署：本地开发 / Docker Compose（稍后）

## 垂直落地 · 工业设备报警处置（当前主线）

一条真实工业报警 → 检索设备手册与历史处置记录 → 要素抽取 → 规则比对 → 结构化处置工单。
报警处置工作流（`extract → compare → agent → report`）的 `agent` 步骤**会真的去查知识库**，
并把命中的手册片段带 `[编号]` 一起喂给模型；手册没写的要求模型明确标注
「手册未覆盖，需现场确认」，禁止编造根因。

- 场景与语料：[`docs/工业文档RAG与要素抽取.md`](docs/工业文档RAG与要素抽取.md)
- 检索效果实测（三路对比）：[`docs/验收标准/工业报警处置-检索评测报告.md`](docs/验收标准/工业报警处置-检索评测报告.md)
- 一键复现：`python scripts/build-industrial-corpus.py` → 上传 `tests/corpus/industrial/*.pdf` → `python scripts/eval-industrial.py`

> 详细设计见 [`docs/MVP设计文档.md`](docs/MVP设计文档.md)（通用平台）。
> P0 数据管道见 [`docs/设计步骤/P0数据管道.md`](docs/设计步骤/P0数据管道.md)，验收标准与报告见 [`docs/验收标准/`](docs/验收标准/)。
> 法律文件场景（历史垂直场景，机制层仍沿用）见 [`docs/法律文件Agent-MVP与实施指南.md`](docs/法律文件Agent-MVP与实施指南.md)。

## 目录结构

多项目模块化单体（参考 NitroGateway）：模块间只依赖对方 `Contracts/`，新增能力 = 新增模块。

```
AgentWorkbench/
├─ AgentWorkbench.slnx
├─ Directory.Build.props
├─ Dockerfile / docker-compose.yml / .env.example
├─ docs/
├─ src/
│  ├─ AgentWorkbench.Shared/          # 配置/常量（被所有模块依赖）
│  ├─ AgentWorkbench.Domain/          # 纯领域：Records / ChatTurn
│  ├─ AgentWorkbench.Persistence/     # 连接 + FluentMigrator 迁移 + 仓储
│  ├─ AgentWorkbench.Documents/       # P0 数据管道（上传/解析/清洗/切分）
│  ├─ AgentWorkbench.Knowledge.Abstractions/  # 知识契约：IKnowledgeEngine/IEmbeddingProvider/IReranker
│  ├─ AgentWorkbench.Knowledge.Local/ # 本地实现：FTS5 + sqlite-vec + RRF + RAG
│  ├─ AgentWorkbench.Knowledge/       # 聚合：AddNitroKnowledge()
│  ├─ AgentWorkbench.Providers/       # 模型网关：供应商/模型 + IChatClient
│  ├─ AgentWorkbench.Agent/           # 对话 Agent 运行时（Microsoft Agent Framework）+ 工具扩展点
│  └─ AgentWorkbench.Webapi/          # 组合根：Controllers / Hubs / Program.cs
├─ tests/AgentWorkbench.ArchitectureTests/  # ArchUnitNET 边界强制
└─ web/                               # Vue 3 前端
```

## 本地运行

### 1. 配置模型（Key 不要提交到仓库）

方式 C · 前端「设置」页在线添加（推荐，**无需重启**）：填名称 / BaseUrl / Key → 添加模型 → 启用。
支持同一供应商下多模型，随时切换；Key 目前**明文存于本地 SQLite**（P2 再加密）。
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

## Docker（稍后再跑）

```bash
cp .env.example .env   # 填 Provider__ApiKey
docker compose up -d --build
# http://localhost:8080
```

## P0 数据管道

上传 PDF → PdfPig 抽取 → 清洗（去页眉页脚/合并断行）→ 按条款切分 → SQLite 入库 → 前端预览。

API：

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

测试集：`tests/corpus/`（公开合同示范文本，`fetch-corpus.ps1` 下载）。验收标准与报告见 [`docs/验收标准/`](docs/验收标准/)；自动化验收：`pwsh -File scripts/acceptance-p0.ps1`（A1/A2/A4/A6/A7 + Q1 + 负控 N1/N2/N3，全过退出码 0）。

> 说明：仅支持 PDF **文本层**；扫描件会判失败并提示（不含 OCR）。

## P1 知识库（RAG）

P0 切分的 `chunks` → **FTS5(trigram) 全文 + sqlite-vec 向量** → 混合检索（向量 ∪ BM25 → RRF 融合）→ **带引用问答**。设计见 [`docs/设计步骤/P1知识库.md`](docs/设计步骤/P1知识库.md)。

API：

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/knowledge/health` | 引擎 + 能力位 + 索引统计 |
| GET | `/api/knowledge/documents` | 已入库文档（含 `indexed`/`chunkCount`） |
| POST | `/api/knowledge/documents/{id}/index` | 手动/重建索引 |
| POST | `/api/knowledge/retrieve` | 检索 `{ query, topK }` → 片段 + 来源 + 分数 |
| POST | `/api/knowledge/ask` | 带引用问答（非流式）；流式走 SignalR `Ask` |
| GET/POST/DELETE | `/api/knowledge/rules` | 规则库管理入口（内容 P2 填充） |

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

测试集与验收：`docs/验收标准/`；自动化验收：`pwsh -File scripts/acceptance-p1.ps1`（H1/I1/I2/R1/R2/D1 + 负控 N1/N2，全过退出码 0）。

## 工业语料与检索评测

工业语料由仓库内的真实素材生成（P0 管道**只吃 PDF**，所以先转 PDF 再上传，不为 CSV 去改管道）：

```bash
python scripts/build-industrial-corpus.py   # -> tests/corpus/industrial/*.pdf（32 份）
# 上传到知识库后：
python scripts/eval-industrial.py           # -> tests/benchmark/industrial/report.json
```

| 脚本 | 作用 |
| --- | --- |
| `scripts/build-industrial-corpus.py` | 故障场景库 + 点位表 → PDF 语料 |
| `scripts/export-alarms.py` | 从 NitroGateway 导真实报警 → CSV + PDF（需先跑通软 PLC） |
| `scripts/eval-industrial.py` | 三路检索 Recall@5 / MRR + 负控误命中率 |

首轮基线（30 正例 + 5 负例）：纯 FTS5 `0.47` / 纯向量 `0.90` / RRF 融合 `0.90`（Recall@5）；
负控误命中率 `1.0` **未达标**——检索缺相关性下限，语料没覆盖的问题也会自信返回 Top5。
详见 [`docs/验收标准/工业报警处置-检索评测报告.md`](docs/验收标准/工业报警处置-检索评测报告.md)。

## 进度

- [x] D1 脚手架：后端 + 前端 + SignalR 流式 + 本地一键启动
- [x] P0 数据管道（D1–D5）：上传/解析/清洗/切分/入库/预览 + 验收
- [x] P1 知识库：`IKnowledgeEngine` 防腐层 + 本地 FTS5/sqlite-vec 混合检索 + 带引用问答 + 验收
- [x] 模型管理：多供应商/多模型在线增删 + 免重启切换（SQLite 持久化，Key 待加密）
- [ ] 工具调用（时间 / 计算 / HTTP / SQLite / 文档）+ 前端可视化
- [ ] AnythingLLM / RAGFlow 适配（预留 stub，改配置切换）

# AgentWorkbench.Webapi

组合根模块：宿主 `Program.cs`、Controllers 与 SignalR Hub。

## 功能

| 文件 | 说明 |
| --- | --- |
| `Program.cs` | 组合根：模块注册（`AddNitroPersistence/Providers/Knowledge/Documents`）、Serilog、64MB 上传上限、启动即迁移 + 种子默认供应商、静态文件 + SPA、CORS、映射 Controllers 与 Hub |
| `Controllers/HealthController.cs` | 健康检查 |
| `Controllers/DocumentsController.cs` | P0 文档：上传 / 列表 / 详情 / 页 / 块 / 重解析 / 删除 / 下载原始 PDF |
| `Controllers/KnowledgeController.cs` | P1 知识库：健康 / 索引 / 检索 / 带引用问答 / 规则 CRUD |
| `Controllers/SettingsController.cs` | 供应商与模型 CRUD / 激活 / 拉取模型 / 连通测试 |
| `Hubs/AgentHub.cs` | SignalR 对话：`Send`（普通对话）、`Ask`（RAG 问答）、`Stop`（停止生成）；事件 `ReceiveSources/ReceiveDelta/ReceiveDone/Error` |

## 依赖

Documents、Knowledge、Providers、Persistence、Domain、Shared（各模块）。

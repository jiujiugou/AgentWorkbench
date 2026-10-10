# AgentWorkbench.Persistence

SQLite 持久化模块：连接、FluentMigrator 迁移与仓储。

## 功能

| 文件 | 说明 |
| --- | --- |
| `DbConnectionFactory.cs` | 创建已打开的 SQLite 连接，开启 WAL 与外键约束 |
| `StoragePaths.cs` | 解析后的绝对存储路径与 SQLite 连接串 |
| `PersistenceServiceCollectionExtensions.cs` | 连接、迁移、仓储的 DI 注册 |
| `DocumentRepository.cs` | 文档 / 页 / 块的数据访问 |
| `ProviderRepository.cs` | 模型供应商与其模型的仓储 |
| `RuleRepository.cs` | 规则库仓储（P1 只做管理入口，内容 P2 填充） |

## 迁移（Migrations）

数据库变更必须使用 FluentMigrator，编号 `M0xx` 递增：

| 迁移 | 说明 |
| --- | --- |
| `M001_CreateDocuments` | 文档表 |
| `M002_CreateDocumentPages` | 文档页表（raw + cleaned） |
| `M003_CreateChunks` | 切分块表 |
| `M004_CreateChunksFts` | 全文索引表：FTS5 + trigram 分词（对中文子串友好），供 BM25 检索 |
| `M005_CreateRules` | 规则库表（标准条款 / 风险清单）：P1 只建表 + 管理入口，内容 P2 填充 |
| `M006_CreateProviders` | 供应商 / 模型表 |

启动时自动迁移，并种子默认供应商（见 `Webapi/Program.cs`）。

## 依赖

Domain、Shared。

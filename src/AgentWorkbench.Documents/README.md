# AgentWorkbench.Documents

P0 数据管道模块：上传 → 队列 → 后台解析 → 清洗 → 切分。

## 功能

| 文件 | 说明 |
| --- | --- |
| `DocumentsServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroDocuments` |
| `DocumentIngestionService.cs` | 接收上传：落盘 + SHA256 去重 + 入库(pending) + 入队解析 |
| `DocumentParseQueue.cs` | 待解析文档的内部队列 |
| `DocumentParseWorker.cs` | 后台单读者解析：出队 → 抽取页文本 → 入库，状态 `pending→parsing→parsed\|failed` |
| `Parsing/PdfTextExtractor.cs` | PDF 文本层抽取（不含 OCR，扫描件不在 P0 范围） |
| `Parsing/TextCleaner.cs` | 文本清洗：去页眉/页脚/页码、合并断行、规范化空白 |
| `Parsing/TextPatterns.cs` | 文本结构识别的共享规则 |
| `Parsing/Chunker.cs` | 按标题/条款预切分，再对超长段落做定长切分（带重叠） |

## 依赖

Knowledge.Abstractions、Persistence。

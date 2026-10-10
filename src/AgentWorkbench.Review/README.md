# AgentWorkbench.Review

P2 合同要素抽取与比对模块：**规则确定性优先 + LLM 增强**。
比对结论来自规则库（无规则即无清单），要素与清单全部可追溯到 chunk/页码；模型不可用时规则路径照常产出。

## 功能

| 文件 | 说明 |
| --- | --- |
| `ReviewService.cs` | 审查服务：创建 / 查询 / 重跑 / 删除 / 导出报告；后台执行「抽取 → 比对」两步并逐步持久化 + 进度通知 |
| `ReviewModels.cs` | `ReviewContext`、`ElementDraft`、`FindingDraft`、各 View 模型与领域异常 |
| `ReviewWorker.cs` + `ReviewRunQueue.cs` | `Channel` + `BackgroundService` 的后台单读者队列（`IReviewRunQueue`） |
| `IReviewProgressNotifier.cs` | 进度通知扩展点（默认空实现；Webapi 用 SignalR `ReviewHub` 覆盖） |
| `Extraction/RuleBasedElementExtractor.cs` | 确定性抽取：正则覆盖出卖人/买受人/总价/大写/交付/质保/验收/违约金/标的 |
| `Extraction/LlmElementExtractor.cs` | LLM 增强抽取：`IStructuredChatService` 强制 JSON；结果**回文定位**到 chunk 并带 evidence |
| `Extraction/IElementExtractor.cs` | 抽取器扩展点 + `ElementExtractorRegistry`（同 key 后注册者覆盖 → LLM 覆盖规则） |
| `Comparison/RuleCheckers.cs` | 3 个内置检查器：必备要素缺失 / 条款存在性（应有·禁止）/ 取值一致性（含金额大小写） |
| `Comparison/IRuleChecker.cs` | 检查器扩展点 + `RuleCheckerRegistry` |
| `Comparison/ChineseNumber.cs` | 自研中文大写金额解析（零壹贰…/零一二…，万/亿/仟/佰/拾/元/整） |
| `Comparison/DocumentRelevance.cs` | 非合同文本（无「合同/甲方/价款…」信号）跳过规则比对，避免产出无证据高危清单 |
| `ReviewReportWriter.cs` | 报告导出扩展点，内置 Markdown / CSV（JSON 由 `ReviewService` 直接序列化） |
| `ReviewServiceCollectionExtensions.cs` | 自注册入口 `AddNitroReview` + `SeedReviewRulesAsync`（买卖合同 7 条起始规则，幂等） |
| Webapi `ReviewController` | HTTP 面：`api/review/health` · `runs`（建/列表/详情/重跑/删除）· `elements` · `findings` · `report` · `rules`（CRUD） |

## 接口

| 接口 | 成员 | 实现 |
| --- | --- | --- |
| `IReviewService` | `GetCapabilitiesAsync`、`CreateRunAsync`、`GetRunAsync`、`ListRunsAsync`、`GetElementsAsync`、`GetFindingsAsync`、`Count*`、`GetReportAsync`、`RerunAsync`、`DeleteRunAsync`、`ExecuteAsync` | `ReviewService` |
| `IElementExtractor` | `Name`、`ExtractAsync` | `RuleBasedElementExtractor`、`LlmElementExtractor` |
| `IRuleChecker` | `Name`、`Check(context, rules, elements)` | `RequiredElementChecker`、`ClausePresenceChecker`、`ValueConsistencyChecker` |
| `IReviewReportWriter` | `WriteMarkdown`、`WriteCsv` | `ReviewReportWriter` |
| `IReviewRunQueue` | `EnqueueAsync`、`ReadAllAsync` | `ReviewRunQueue` |
| `IReviewProgressNotifier` | `NotifyAsync(runId, status, stepsJson)` | `NullReviewProgressNotifier`（Webapi 覆盖为 SignalR） |
| `IReviewRepository` / `IRuleRepository`（Persistence） | 审查/要素/清单/规则读写 | — |

## 框架提供 vs 自研

| 机制 | 来源 | 说明 |
| --- | --- | --- |
| LLM 一次性结构化输出（`IChatClient.GetResponseAsync<T>`） | **MEA**（经 Agent 模块 `IStructuredChatService`） | 仅 `LlmElementExtractor` 用到 |
| **其余全部自研** | 自研 | 正则抽取、3 类规则检查器、中文大写金额解析、文档相关性判定、报告渲染、队列/后台执行、步骤状态机、注册表覆盖语义 |

> 设计取舍：P2 是**判定型**能力，正确性由规则保证、由脚本判定，不引入 LLM-as-judge。
> LLM 只做「增强」，模型未配置时直接跳过（`capabilities.LlmExtract=false`），规则抽取照常产出。

## 依赖

Agent、Knowledge.Abstractions、Persistence、Domain、Shared。

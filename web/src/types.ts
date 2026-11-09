/** 检索来源（来自 SignalR ReceiveSources，字段与后端 RetrievalHit 对齐）。 */
export interface RetrievalSource {
  chunkId: string
  documentId: string
  fileName: string
  seq: number
  /**
   * 本次证据里的编号（1 起）。**历史快照只有被引用的那几块**，编号会跳号，
   * 所以不能靠数组下标反推——`citations.ts` 用 `no` 精确查找，查不到才退回下标。
   */
  no?: number
  heading?: string | null
  pageFrom?: number | null
  pageTo?: number | null
  vectorScore?: number | null
  textScore?: number | null
  fusedScore?: number | null
  content: string
}

/** 一次工具调用（来自 SignalR ReceiveTool，字段与后端 ToolCallTrace 对齐，参数/结果已截断）。 */
export interface ToolStepEvent {
  iteration: number
  name: string
  arguments?: string | null
  resultPreview?: string | null
  error?: string | null
}

/** 一次 run 的摘要（来自 SignalR ReceiveDone 的 run 字段，与后端 HarnessRunResult 对齐）。 */
export interface RunSummary {
  /** Python 侧是 uuid hex（C# 侧是自增整数）。这里是字符串——两者都能显示。 */
  runId: string
  /**
   * 轮次。**Python 版恒为 1**：一次作答是四段流水线，没有 agent 循环。
   * 是否显示成"轮迭代"看 `pipeline`，写死"N 轮迭代"会让界面撒谎。
   */
  iterations: number
  /** true = 四段流水线（预检索 → 判定 → 生成 → 校验），不显示"轮迭代"。 */
  pipeline?: boolean
  model?: string | null
  /** null = 没记到耗时（崩溃、或供应商没回）。不补 0——0 是「没花时间」。 */
  latencyMs: number | null
  /** 正常收尾 / 跑满迭代 / 循环 / 超预算 / 超时 / 取消 / 失败，取值见后端 AgentRunStatus。 */
  status: string
  /** 结束原因码，取值见后端 AgentRunCode；正常收尾时为 null。 */
  code?: string | null
  steps: number
  tools: number
}

/** 一条引用标注的校验结论（来自 SignalR ReceiveCitations，与后端 Citation 对齐）。 */
export interface CitationItem {
  /** 所属句子序号（从 0 起，切句规则见后端 CitationParser）。 */
  sentenceIndex: number
  /** 模型写的编号（从 1 起，对应检索结果的 no）。 */
  evidenceNo: number
  fileName: string
  seq: number
  chunkId: number
  /** ok = 对得上；outOfRange = 指向不存在的编号；unsupported = 号码存在但内容对不上。 */
  check: 'ok' | 'outOfRange' | 'unsupported'
  /** 该句与所指块的字符覆盖率；越界时为 null。 */
  coverage?: number | null
}

/** 一次拒答（来自 SignalR ReceiveRefusal）：文案由服务端写死，不是模型的输出。 */
export interface RefusalInfo {
  text: string
  code?: string | null
}

/** 一次作答的引用体检报告（来自 SignalR ReceiveCitations，与后端 CitationReport 对齐）。 */
export interface CitationReport {
  sentenceCount: number
  citedSentenceCount: number
  uncitedSentenceCount: number
  outOfRangeCount: number
  unsupportedCount: number
  /** 无出处句占全部句子的比例（0 到 1），由服务端算好，前端不再自己除。 */
  uncitedRatio: number
  /**
   * 整段答案有没有资料支撑：supported / unsupported / undetermined（判定线未配置）。
   * 与 unsupportedCount 不是一回事——后者数的是"几条引用指向错块"，这个是"整段有没有撑住"。
   */
  supportVerdict: string
  /** 剥掉越界标注后的正文。 */
  cleanedAnswer: string
  citations: CitationItem[]
}

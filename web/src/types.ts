/** 检索来源（来自 SignalR ReceiveSources，字段与后端 RetrievalHit 对齐）。 */
export interface RetrievalSource {
  chunkId: string
  documentId: string
  fileName: string
  seq: number
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
  runId: number
  iterations: number
  model?: string | null
  latencyMs: number
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

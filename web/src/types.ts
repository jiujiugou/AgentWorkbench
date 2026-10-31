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

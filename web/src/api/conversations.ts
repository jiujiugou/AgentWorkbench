/**
 * 会话 · 列表 / 历史消息 / 运行轨迹。
 *
 * 三个都是 GET，都不写库。**历史消息里的引用是快照**：文件名、片段号、原文 120 字、
 * 当时的校验结论，全是当时复制下来的值——所以这里读出来的东西不依赖索引当前长什么样
 * （C-V3）。也因此历史里刻意没有 `chunkId` / `documentId` / 页码：那几样是指针，
 * 快照不存它们，存了就会出现"卡片还在、原文已经不是那一份"。
 */

import type { RunSummary, ToolStepEvent } from '@/types'
import { handle } from './http'

export type ConversationKind = 'knowledge' | 'general'

export interface Conversation {
  id: string
  title: string
  /** Python 侧创建时固定；C# 后端不返回，故可选。 */
  kind?: ConversationKind
  createdAt: string
  updatedAt: string
}

/** 一张出处卡片。字段与 `message_citations` 一一对应，**全部来自快照**。 */
export interface HistoryCitation {
  no: number
  fileName: string
  seq: number
  excerpt: string
  /** kept = 号在当次证据范围内。stripped 不会出现在历史里（越界的当时就不入库） */
  checkFirst: 'kept' | 'stripped'
  /** null = 没有二级结论；unverified = 判了但判不了。两者都不许当成 supported */
  checkSecond: 'supported' | 'unsupported' | 'unverified' | null
  coverage: number | null
}

export interface ConversationMessage {
  id: number
  role: 'user' | 'assistant'
  content: string
  createdAt: string
  /** assistant 必有（失败那一轮也有），user 恒为 null。 */
  runId?: string | null
  /**
   * 那次运行的终态。**不是消息自己的一列**：会话域不存状态，这里读的是
   * `agent_runs` 那一行（C-V13）。
   *
   * 界面靠它区分三件长得一样的事：`running` = 还没跑完（可能已中断）；
   * 空正文 + `failed/cancelled/limited` = 跑完了但没答上来；
   * `refused` = 它明确说了"资料里没有"。都画成"一条空回答"等于把三个
   * 排查方向压成一个。
   */
  runStatus?: 'running' | 'succeeded' | 'failed' | 'cancelled' | 'limited' | 'refused' | null
  /** 终态的原因码。refused 时是 knowledge_insufficient 之类。 */
  runReason?: string | null
  /** Python 侧必有；C# 运行时为 undefined，store 侧用可选链兜底。 */
  citations: HistoryCitation[]
}

/** `/messages` 的外层。**不是裸数组**：引用按消息挂，外层还带会话类型，
 *  界面据此决定要不要显示“未使用企业资料”。 */
export interface MessagesResponse {
  conversationId: string
  kind: ConversationKind
  messages: ConversationMessage[]
}

/** 一步的完整记录。`inFlight` = 发出去了没回来（与"返回了空"是两件事）。 */
export interface TraceStep {
  seq: number
  kind: 'retrieval' | 'assistant' | 'tool'
  toolName: string | null
  input: string | null
  output: string | null
  error: string | null
  model: string | null
  durationMs: number | null
  promptTokens: number | null
  completionTokens: number | null
  createdAt: string
  inFlight: boolean
}

export interface Trace {
  runId: string
  question: string
  status: string
  reason: string | null
  model: string | null
  promptVersion: string | null
  startedAt: string
  finishedAt: string | null
  durationMs: number | null
  promptTokens: number | null
  completionTokens: number | null
  /** null = 没有正文可校验（拒答 / 失败）；0 = 校验过、零处问题。两者不许合并。 */
  citationOutOfRange: number | null
  citationUnsupported: number | null
  error: string | null
  steps: TraceStep[]
}

export const conversationsApi = {
  list: () => fetch('/api/conversations').then((r) => handle<Conversation[]>(r)),
  // C# ConversationsController.Messages 返回裸数组，不是 {conversationId, kind, messages}
  messages: (id: string) =>
    fetch(`/api/conversations/${id}/messages`).then((r) => handle<ConversationMessage[]>(r)),
  /** 一次运行的完整回放。路径挂在 `/api/runs` 下：轨迹的主键就是 run，不是会话。 */
  trace: (runId: string) => fetch(`/api/runs/${runId}/trace`).then((r) => handle<Trace>(r)),
  remove: (id: string) => fetch(`/api/conversations/${id}`, { method: 'DELETE' }),
}

// ── 轨迹 → 组件形状 ──
//
// `RunTrace` 组件是照 C# 的 agent 循环写的（"第 N 轮迭代"、工具调用列表）。
// Python 版是四段流水线，没有迭代、只有一个生成步，所以两个字段要显式改写：
// `pipeline` 让组件别再说"轮迭代"，`iteration` 恒 0 且不显示。

const STEP_NAME: Record<TraceStep['kind'], string> = {
  retrieval: '检索知识库',
  assistant: '生成回答',
  tool: '工具调用',
}

/** 轨迹的步骤 → 时间线。`inFlight` 单独成一句文案：它是"不知道结果"，不是"返回了空"。 */
export function toToolSteps(trace: Trace): ToolStepEvent[] {
  return trace.steps.map((step) => ({
    // 四段流水线没有迭代，恒 0；组件在 `pipeline` 为真时不显示轮次
    iteration: 0,
    name: step.toolName ?? STEP_NAME[step.kind] ?? step.kind,
    arguments: step.input,
    resultPreview: step.output,
    // "发出去了没回来"必须与"工具返回了空"分开说：前者要查那次调用
    // （进程被杀 / 中途停止），后者要查工具本身。合成一句会把排查方向带偏。
    error: step.error ?? (step.inFlight ? '发出后没有返回（进程中断或已停止）' : null),
  }))
}

/** 轨迹头 → run 摘要。`latencyMs` 为 null 时界面不显示耗时，不补 0——0 是"没花时间"。 */
export function toRunSummary(trace: Trace): RunSummary {
  return {
    runId: trace.runId,
    iterations: 1,
    pipeline: true,
    model: trace.model,
    latencyMs: trace.durationMs,
    status: trace.status,
    code: trace.reason,
    steps: trace.steps.length,
    // "工具调用"数排除生成步：生成不是工具，把它算进去会让"2 次工具调用"
    // 与用户看到的"只查了一次知识库"对不上
    tools: trace.steps.filter((s) => s.kind !== 'assistant').length,
  }
}

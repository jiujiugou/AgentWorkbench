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
  /** assistant 必有（失败那一轮也可能为空）；user 恒为 null。数字是后端自增 id。 */
  runId?: number | null
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

// ── 轨迹 ──
//
// 轨迹只有一份契约：`GET /api/harness/runs/{id}`，它是 `agent_runs` 那一行的只读投影。
//
// 这里原本另有一份照 Python 四段流水线写的 `Trace`（`question` / `promptVersion` /
// `citationOutOfRange` / `steps[].inFlight`…），打在 `/api/runs/{id}/trace` 上。
// **那些字段在 C# 侧从来没存在过**，端点也不存在——于是请求恒 404，
// 而调用方 `catch {}` 静默吞掉，表现为「打开历史对话，执行过程永远是空的」，
// 且不报错。同一条链路上不留第二种轨迹形状。

/** 一步轨迹，字段与后端 `HarnessStep` 一一对应。 */
export interface RunStep {
  iteration: number
  /** `tool` = 模型调了一次工具；`assistant` = 本次 run 的最终答复那一步。 */
  kind: 'tool' | 'assistant'
  tool: string | null
  arguments: string | null
  result: string | null
  /** 工具报错原文；正常为 null。 */
  error: string | null
}

/** 一次 run 的结果，字段与后端 `HarnessRunResult` 一一对应。 */
export interface RunDetail {
  /** 自增整数（不是 Python 侧的 uuid hex）。展示时转字符串。 */
  runId: number
  sessionId: string
  text: string
  iterations: number
  model: string | null
  latencyMs: number
  steps: RunStep[]
  /** 取值见后端 `AgentRunStatus`。 */
  status: string
  /** 结束原因码，正常收尾为 null。 */
  code: string | null
}

export const conversationsApi = {
  list: () => fetch('/api/conversations').then((r) => handle<Conversation[]>(r)),
  // C# ConversationsController.Messages 返回裸数组，不是 {conversationId, kind, messages}
  messages: (id: string) =>
    fetch(`/api/conversations/${id}/messages`).then((r) => handle<ConversationMessage[]>(r)),
  /**
   * 一次 run 的回放。**主键是 run，不是会话**，所以挂在 harness 下而不是 conversations 下。
   * 用 `encodeURIComponent`：runId 来自 `ReceiveDone`，转义不做假设。
   */
  runDetail: (runId: string) =>
    fetch(`/api/harness/runs/${encodeURIComponent(runId)}`).then((r) => handle<RunDetail>(r)),
  remove: (id: string) => fetch(`/api/conversations/${id}`, { method: 'DELETE' }),
}

// ── 轨迹 → 组件形状 ──

/**
 * 轨迹步骤 → 时间线。
 *
 * **只取 `tool` 步**：`assistant` 那一步是最终答复本身，正文已经显示在上方，
 * 再列进"执行过程"会重复；更实际的原因是面板头部的"N 次工具调用"数的是时间线长度，
 * 把答复算进去会变成"3 次工具调用"而实际只调了 2 次。
 * 这样实时（只推 `ReceiveTool`）与回看（读轨迹表）看到的时间线长度一致。
 */
export function toToolSteps(run: RunDetail): ToolStepEvent[] {
  return run.steps
    .filter((step) => step.kind === 'tool')
    .map((step) => ({
      iteration: step.iteration,
      name: step.tool ?? step.kind,
      arguments: step.arguments,
      resultPreview: step.result,
      error: step.error,
    }))
}

/**
 * run 头 → 摘要。
 *
 * **不设 `pipeline`**：那是 Python 四段流水线的标记，会让组件改口说"四段流水线"。
 * C# 侧是真的 agent 循环（`iterations` 由后端给），照实说"N 轮迭代"。
 * `latencyMs` 后端必有；`tools` 只数 `tool` 步，生成不算工具调用。
 */
export function toRunSummary(run: RunDetail): RunSummary {
  return {
    runId: String(run.runId),
    iterations: run.iterations,
    model: run.model,
    latencyMs: run.latencyMs,
    status: run.status,
    code: run.code,
    steps: run.steps.length,
    tools: run.steps.filter((s) => s.kind === 'tool').length,
  }
}

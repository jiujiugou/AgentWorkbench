/**
 * 知识问答 · `POST /api/knowledge/ask`
 *
 * 一次请求-响应，不是流式：Python 侧的形态是**系统预检索**（F3 §1.2）——
 * 四段流水线跑完才有结果，中间没有"模型正在想什么"可推。C# 那边走 SignalR
 * 逐字推流，两者的数据形状不同，所以这里不做"尽量兼容"，而是**在这一层做一次转换**。
 *
 * 转换的边界就放在本文件：上面的组件（ChatMessage / citations.ts）继续吃
 * 它们熟悉的 `RetrievalSource` / `CitationReport`，不必知道后端换了一套字段名。
 * 将来后端补齐字段、或前端整体改型，只需改这里的两个函数。
 */
import type { CitationItem, CitationReport, RetrievalSource } from '@/types'
import type { ConversationMessage } from '@/api/conversations'
import { handle } from './http'
import { sentenceRanges } from '@/utils/citations'

export interface AskSource {
  no: number
  chunkId: number
  documentId: string
  fileName: string
  seq: number
  pageFrom: number
  pageTo: number
  heading: string | null
  content: string
}

export interface AskCitation {
  sentenceIndex: number
  evidenceNo: number
  documentId: string
  fileName: string
  seq: number
  chunkId: number
  /** kept = 编号在本次证据范围内；stripped = 越界，已从正文里摘掉 */
  first: 'kept' | 'stripped'
  /** 二级校验：内容真的支持这句话吗。越界时无从谈起，为 null */
  second: 'supported' | 'unsupported' | 'unverified' | null
  coverage: number | null
}

export interface AskReport {
  sentenceCount: number
  citedSentenceCount: number
  uncitedSentenceCount: number
  outOfRangeCount: number
  unsupportedCount: number
  /** 整段结论：supported / unsupported / undetermined（服务端算好，界面不算） */
  supportVerdict: string
  /** 无出处句占比。除在服务端：0/0 在 JS 是 NaN、在 Python 会抛，两边表现不同 */
  uncitedRatio: number
  citations: AskCitation[]
}

export interface AskResponse {
  /** 落在哪条会话。新建时前端必须记下来，否则下一次追问会再开一条。 */
  conversationId: string
  runId: string
  status: string
  reason: string | null
  verdict: string
  answer: string
  /** 拒答时为 null：没有正文可校验，与"校验过、零处问题"是两件事 */
  report: AskReport | null
  sources: AskSource[]
}

/**
 * 一次问答。`audit_unavailable` 时抛错——记录写不进去，这次回答不算成功。
 *
 * `conversationId` 带上就是追问，不带就新开一条。**他人的 id 服务端会当成
 * "不存在"并新开一条**（不报错、不复用），所以这里不需要先校验它。
 */
export async function askKnowledge(
  question: string,
  conversationId?: string | null,
  signal?: AbortSignal,
): Promise<AskResponse> {
  const response = await fetch('/api/knowledge/ask', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ question, conversationId: conversationId || null }),
    signal,
  })
  // 必须过 handle：fetch 不会因 4xx/5xx 抛错，直接返回 response 的话
  // 一个 500 会被当成"成功响应"往下走，界面显示一条空白答案而不是错误。
  return handle<AskResponse>(response)
}

/**
 * 后端 `AskSource` → 组件用的 `RetrievalSource`。
 *
 * `chunkId` 要转成字符串：Python 侧是自增整数，C# 侧是 Guid，
 * 前端类型从一开始就按字符串写的。不转的话 `sources?.[no - 1]` 之外的
 * 任何按 chunkId 关联的地方都会静默失配（两边都说"有这个字段"，值却对不上）。
 */
export function toSources(response: AskResponse): RetrievalSource[] {
  return response.sources.map((s) => ({
    no: s.no,
    chunkId: String(s.chunkId),
    documentId: s.documentId,
    fileName: s.fileName,
    seq: s.seq,
    heading: s.heading,
    pageFrom: s.pageFrom,
    pageTo: s.pageTo,
    content: s.content,
  }))
}

function toCheck(c: AskCitation): CitationItem['check'] {
  // 前端三值是为显示服务的：越界 / 挂错块 / 对得上。
  // 后端拆成两级（first + second）是校验本身的形状，映射在这里收口。
  if (c.first === 'stripped') return 'outOfRange'
  if (c.second === 'unsupported') return 'unsupported'
  return 'ok'
}

/** 后端报告 → 组件用的 `CitationReport`。拒答时返回 null（没有正文可校验）。 */
export function toReport(response: AskResponse): CitationReport | null {
  const report = response.report
  if (!report) return null
  return {
    sentenceCount: report.sentenceCount,
    citedSentenceCount: report.citedSentenceCount,
    uncitedSentenceCount: report.uncitedSentenceCount,
    outOfRangeCount: report.outOfRangeCount,
    unsupportedCount: report.unsupportedCount,
    uncitedRatio: report.uncitedRatio,
    supportVerdict: report.supportVerdict,
    // 正文已经由服务端剥掉越界标注，`answer` 就是 cleanedAnswer。
    // 再造一个同义字段没意义——两处存同一句话，迟早一处新一处旧。
    cleanedAnswer: response.answer,
    citations: report.citations.map((c) => ({
      sentenceIndex: c.sentenceIndex,
      evidenceNo: c.evidenceNo,
      fileName: c.fileName,
      seq: c.seq,
      chunkId: c.chunkId,
      check: toCheck(c),
    })),
  }
}

// ── 历史快照 → 组件形状 ──
//
// 当次作答走 `toSources` / `toReport`（后端给的是完整报告）。历史走下面两个函数：
// 库里存的是**每块一张卡片**的快照（`message_citations`），没有按句的体检报告。
// 所以这两件事都得从快照重建，而重建里有一项是**推断**，必须说清楚：
//
// - 每张卡片的 `check`（对得上 / 挂错块）是**存下来的**，不是推的 —— 这是判据本身。
// - 它属于哪一句（`sentenceIndex`）是**从正文的角标位置反推的**。正文里 `[n]`
//   还在（越界的当时就被剥掉，留下的都是有效角标），所以能定位；切句规则与后端
//   同一套（`sentenceRanges`，见 `utils/citations.ts` 里的注释）。
// - `supportVerdict`（整段结论）**没有落库**，这里按与后端相同的优先级重新聚合。
//   它是展示用的汇总结论，不是判据；判据是每张卡片的 `check`。
//
// 不做的事：历史里不重新校验内容（不拿 chunks 比对）。那正是"快照"要避免的——
// 索引一重建，重算的结果就和当时不一样了（C-V6 的同一条道理）。

/**
 * 历史消息里的卡片 → 来源面板的形状。
 *
 * `chunkId` 写成 `snapshot-<no>` 而不是数字：快照里没有真 chunkId（刻意不存指针），
 * 编一个数字出来会让"按 chunkId 关联"的代码拿到一个假 id 却不报错。
 */
export function historySources(message: ConversationMessage): RetrievalSource[] {
  return message.citations.map((c) => ({
    no: c.no,
    chunkId: `snapshot-${c.no}`,
    documentId: '',
    fileName: c.fileName,
    seq: c.seq,
    pageFrom: null,
    pageTo: null,
    heading: null,
    content: c.excerpt,
  }))
}

/** 正文里的角标，含 `[1,3]` 这种合并写法（与渲染时的正则同一套）。 */
const CITE_GROUP = /\[\s*(\d+(?:\s*[,，]\s*\d+)*)\s*\]/g

/**
 * 历史消息 → 组件用的 `CitationReport`。
 *
 * 没有卡片时返回 `null`：拒答与失败轮次没有正文可校验，与"校验过、零处问题"
 * 是两件事——返回一份全零的报告会让界面显示"引用了 0 处，全部无出处"，
 * 而实际上那次根本没答。
 */
export function historyReport(message: ConversationMessage): CitationReport | null {
  if (!message.citations.length) return null

  const byNo = new Map(message.citations.map((c) => [c.no, c]))
  const ranges = sentenceRanges(message.content)
  const items: CitationItem[] = []
  const seen = new Set<string>()

  for (const match of message.content.matchAll(CITE_GROUP)) {
    const pos = match.index ?? 0
    // 角标落在哪一句：句末标点之后、下一句之前的位置归前一句（与后端一致）
    let index = ranges.findIndex(([a, b]) => pos >= a && pos < b)
    if (index < 0) index = Math.max(0, ranges.length - 1)

    for (const part of match[1].split(/[,，]/)) {
      const no = Number(part.trim())
      const snapshot = byNo.get(no)
      // 找不到说明这个角标当时被剥掉了（越界），正文里不该还留着——跳过而不是编一条
      if (!snapshot) continue
      const key = `${index}:${no}`
      if (seen.has(key)) continue
      seen.add(key)
      items.push({
        sentenceIndex: index,
        evidenceNo: no,
        fileName: snapshot.fileName,
        seq: snapshot.seq,
        chunkId: 0,
        // 二级没判过（null）或判不了（unverified）都算"对得上"以外的第三种，
        // 但界面的三值只区分"挂错块"。这里只在明确 unsupported 时标坏。
        check: snapshot.checkSecond === 'unsupported' ? 'unsupported' : 'ok',
        coverage: snapshot.coverage,
      })
    }
  }

  const sentenceCount = ranges.length || 1
  const cited = new Set(items.map((i) => i.sentenceIndex))
  const uncited = Math.max(0, sentenceCount - cited.size)
  const unsupported = items.filter((i) => i.check === 'unsupported').length
  const undetermined =
    unsupported > 0 ||
    message.citations.some((c) => c.checkSecond !== 'supported')

  return {
    sentenceCount,
    citedSentenceCount: cited.size,
    uncitedSentenceCount: uncited,
    // 越界的不入库，所以历史里的越界数恒为 0——这个数是"当时剥掉了几条"，
    // 已经落在 `agent_runs.citation_out_of_range` 上，不在这份快照里
    outOfRangeCount: 0,
    unsupportedCount: unsupported,
    uncitedRatio: uncited / sentenceCount,
    supportVerdict: unsupported ? 'unsupported' : undetermined ? 'undetermined' : 'supported',
    cleanedAnswer: message.content,
    citations: items,
  }
}

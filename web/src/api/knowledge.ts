/**
 * 引用快照 → 组件形状（历史回看专用）。
 *
 * 实时作答的来源与引用**不走这里**：它们由 SignalR 的 `ReceiveSources` /
 * `ReceiveCitations` 推下来，`stores/agent.ts` 直接写进对应消息的 map。
 *
 * 本文件只服务回看。库里存的是**每块一张卡片**的快照（`message_citations`），
 * 没有按句的体检报告，所以这两件事都得从快照重建，其中有一项是**推断**：
 *
 * - 每张卡片的 `check`（对得上 / 挂错块）是**存下来的**，不是推的 —— 这是判据本身。
 * - 它属于哪一句（`sentenceIndex`）是**从正文的角标位置反推的**。正文里 `[n]`
 *   还在（越界的当时就被剥掉，留下的都是有效角标），所以能定位；切句规则与后端
 *   同一套（`sentenceRanges`，见 `utils/citations.ts` 里的注释）。
 * - `supportVerdict`（整段结论）**没有落库**，这里按与后端相同的优先级重新聚合。
 *   它是展示用的汇总结论，不是判据；判据是每张卡片的 `check`。
 *
 * 不做的事：历史里不重新校验内容（不拿 chunks 比对）。那正是"快照"要避免的——
 * 索引一重建，重算的结果就和当时不一样了（C-V6 的同一条道理）。
 *
 * 2026-11-10 删掉了 `askKnowledge` 与 `toSources` / `toReport` / 四个 `Ask*` 类型：
 * 它们打的是 `POST /api/knowledge/ask`，而 C# 后端从来没有这个端点（知识问答
 * 早已由 `/hubs/agent` 的 `Ask` 承担）。留着就是五个函数指向一个 404。
 */
import type { CitationItem, CitationReport, RetrievalSource } from '@/types'
import type { ConversationMessage } from '@/api/conversations'
import { sentenceRanges } from '@/utils/citations'

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

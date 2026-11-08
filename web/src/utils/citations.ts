import { renderMarkdown } from '@/utils/markdown'
import type { CitationReport, RetrievalSource } from '@/types'

/**
 * 知识问答答案的引用标注：把正文里的 `[n]` 变成可悬停的角标，给没有出处的句子加虚线底纹。
 *
 * 为什么不走 Vue 组件：正文是 Markdown 渲染出来的 HTML（v-html），句子藏在任意层级的节点里，
 * 拿 Vue 去逐句包组件等于自己写一个 Markdown 解析器。所以在渲染前埋哨兵字符、渲染后替换成 HTML，
 * 复杂度收敛在一个纯函数里。
 */

/** 哨兵字符取 Unicode 私有区：不是任何 Markdown 语法字符，引擎会原样保留。 */
const CITE_OPEN = '\uE100'
const CITE_CLOSE = '\uE101'
const UNCITED_OPEN = '\uE102'
const UNCITED_CLOSE = '\uE103'

/** 与后端 CitationParser 同一套句末标点。少认一个，句子边界就错一位，底纹会挂错句。 */
const SENTENCE_ENDS = '。！？；!?;\n'

/**
 * 切句区间：与后端 CitationParser.SentenceRanges 同规则——连续句末标点并入同一句，
 * 纯空白不算句。只切区间，不做"标号归前一句"的归属（那是后端的事，这里只用它的结论）。
 */
export function sentenceRanges(text: string): Array<[number, number]> {
  const ranges: Array<[number, number]> = []
  let start = 0

  for (let i = 0; i < text.length; i++) {
    if (!SENTENCE_ENDS.includes(text[i])) continue

    let end = i + 1
    while (end < text.length && SENTENCE_ENDS.includes(text[end])) end++

    if (!isBlank(text, start, end)) ranges.push([start, end])
    start = end
    i = end - 1
  }

  if (start < text.length && !isBlank(text, start, text.length)) ranges.push([start, text.length])
  return ranges
}

function isBlank(text: string, start: number, end: number): boolean {
  for (let i = start; i < end; i++) {
    if (!/\s/.test(text[i])) return false
  }
  return true
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

function pageLabel(s?: RetrievalSource): string {
  if (!s || s.pageFrom == null) return ''
  if (s.pageTo != null && s.pageTo !== s.pageFrom) return `P${s.pageFrom}–${s.pageTo}`
  return `P${s.pageFrom}`
}

/** 角标 + 悬浮卡片。卡片直接进 HTML：v-html 里挂不上 Vue 组件，纯 CSS 控制显隐。 */
function citeHtml(no: number, report: CitationReport, sources?: RetrievalSource[]): string {
  const item = report.citations.find((c) => c.evidenceNo === no)
  if (!item) return `[${no}]` // 越界编号后端已剥，走到这里说明数据不一致，退回纯文本不渲染假角标

  // 来源面板里同一编号的命中；缺席时退回校验记录里的文件名，卡片至少能说明出处是谁。
  const source = sources?.[no - 1]
  const fileName = source?.fileName ?? item.fileName
  const page = pageLabel(source)
  const body = (source?.content ?? '').slice(0, 120)
  const ellipsis = (source?.content ?? '').length > 120 ? '…' : ''
  const bad = item.check === 'unsupported'

  const warn =
    bad ? '<span class="cite-card-warn">⚠ 所引内容在该片段里找不到对应，请核对原文</span>' : ''

  return (
    `<sup class="cite${bad ? ' cite-bad' : ''}" data-no="${no}" tabindex="0">` +
    `<span class="cite-mark">[${no}]</span>` +
    '<span class="cite-card">' +
    `<span class="cite-card-head">《${escapeHtml(fileName)}》 · 片段 #${item.seq}` +
    (page ? ` · ${page}` : '') +
    '</span>' +
    (body ? `<span class="cite-card-body">${escapeHtml(body)}${ellipsis}</span>` : '') +
    warn +
    '</span></sup>'
  )
}

/**
 * Markdown 围栏代码块区间（```…```）。代码里的 `[2]` 不是引用、`。` 不是句子边界——
 * 哨兵埋进去会把代码改得面目全非（行首的 ``` 前多一个字符，围栏直接失效）。
 */
function fencedRanges(text: string): Array<[number, number]> {
  const ranges: Array<[number, number]> = []
  const fence = /^ {0,3}(```|~~~)/gm
  let open: number | null = null
  let m: RegExpExecArray | null
  while ((m = fence.exec(text)) !== null) {
    if (open === null) {
      open = m.index
    } else {
      ranges.push([open, fence.lastIndex])
      open = null
    }
  }
  if (open !== null) ranges.push([open, text.length]) // 未闭合围栏，其余部分都当代码
  return ranges
}

/** 行首是块级语法的句子不包底纹：哨兵占住行首会让标题/列表/引用整行失效。 */
const BLOCK_START = /^ {0,3}(?:#{1,6}\s|[-*+]\s|\d+[.)]\s|>|~~~|`{3})/

/**
 * 渲染带标注的答案。
 *
 * @param text 正文（有报告时用 cleanedAnswer——越界编号已剥，不再显示）
 * @param report 引用体检报告；null 表示没有做过校验（普通对话 / 历史消息），按普通 Markdown 渲染
 * @param sources 来源面板那批命中，给悬浮卡片供页码与原文
 */
export function renderAnnotated(
  text: string,
  report: CitationReport | null | undefined,
  sources?: RetrievalSource[],
): string {
  // 注意：citations 为空（模型一个标号都没写）也要走标注路径——
  // 此时全部句子都无出处，恰恰是最需要底纹可见的情况。
  if (!report) return renderMarkdown(text)

  const fences = fencedRanges(text)
  const inFence = (pos: number) => fences.some(([a, b]) => pos >= a && pos < b)
  const overlapsFence = (a: number, b: number) => fences.some(([x, y]) => a < y && b > x)

  // 1. 给没有有效出处的句子包哨兵。从后往前包，前面的区间下标不受影响。
  const cited = new Set(
    report.citations.filter((c) => c.check !== 'outOfRange').map((c) => c.sentenceIndex),
  )
  let marked = text
  const ranges = sentenceRanges(text)
  for (let i = ranges.length - 1; i >= 0; i--) {
    if (cited.has(i)) continue
    const [a, b] = ranges[i]
    // 代码块里没有"句子"；行首块级语法（标题/列表/引用）前埋哨兵会让 Markdown 失效。
    if (overlapsFence(a, b)) continue
    if (BLOCK_START.test(text.slice(a, b).split('\n', 1)[0])) continue
    marked = marked.slice(0, a) + UNCITED_OPEN + marked.slice(a, b) + UNCITED_CLOSE + marked.slice(b)
  }

  // 2. `[2]` / `[1,3]` 逐个编号换成角标哨兵；代码块内的方括号是数组下标，不是引用。
  marked = marked.replace(
    /\[\s*(\d+(?:\s*[,，]\s*\d+)*)\s*\]/g,
    (match: string, inner: string, offset: number) => {
      if (inFence(offset)) return match
      return inner
        .split(/[,，]/)
        .map((p) => p.trim())
        .filter((p) => /^\d+$/.test(p) && Number(p) > 0)
        .map((p) => CITE_OPEN + Number(p) + CITE_CLOSE)
        .join('')
    },
  )

  // 3. Markdown 渲染（哨兵不是语法字符，会原样保留在原地）。
  let html = renderMarkdown(marked)

  // 4. 哨兵换回 HTML。
  html = html.replace(new RegExp(`${CITE_OPEN}(\\d+)${CITE_CLOSE}`, 'g'), (_m, d: string) =>
    citeHtml(Number(d), report, sources),
  )
  html = html.split(UNCITED_OPEN).join('<span class="sent-uncited">')
  html = html.split(UNCITED_CLOSE).join('</span>')

  return html
}

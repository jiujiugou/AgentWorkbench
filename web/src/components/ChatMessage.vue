<script setup lang="ts">
import { computed, ref } from 'vue'
import { ArrowRight, CopyDocument, MagicStick, Connection } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { renderAnnotated } from '@/utils/citations'
import type { CitationReport, RefusalInfo, RetrievalSource, RunSummary, ToolStepEvent } from '@/types'
import type { ChatMessage } from '@/stores/agent'
import RunTrace from '@/components/RunTrace.vue'
import ActionCards from '@/components/ActionCards.vue'

const props = defineProps<{
  message: ChatMessage
  /** 这条会话的答案合同。general 时必须显示“未使用企业资料”。 */
  mode?: 'knowledge' | 'general'
  sources?: RetrievalSource[]
  citations?: CitationReport
  refusal?: RefusalInfo
  tools?: ToolStepEvent[]
  run?: RunSummary
  stopStatus?: string
}>()

const sourcesOpen = ref(false)
const sourceList = computed(() => props.sources ?? [])

// 有体检报告时用 cleanedAnswer：越界编号（指向不存在之物）已由服务端剥掉，正文不再保留假担保。
const bodyText = computed(
  () => props.refusal?.text || props.citations?.cleanedAnswer || props.message.content,
)

const html = computed(() =>
  props.message.role === 'assistant'
    ? renderAnnotated(bodyText.value, props.citations, props.sources)
    : '',
)

/** 引用体检的一行摘要：句数 / 有出处 / 无出处 / 错块。全零指标不占位。 */
const citeSummary = computed(() => {
  // 拒答时不显示句级统计：那份报告针对的是被丢弃的草稿，挂在拒答卡片下面只会让人以为"它答了但没出处"。
  if (props.refusal) return null
  const r = props.citations
  if (!r) return null
  const parts: string[] = [`${r.sentenceCount} 句`]
  if (r.citedSentenceCount) parts.push(`${r.citedSentenceCount} 句有出处`)
  if (r.uncitedSentenceCount) parts.push(`${r.uncitedSentenceCount} 句无出处`)
  if (r.unsupportedCount) parts.push(`${r.unsupportedCount} 条指向错块`)
  return parts.join(' · ')
})

/**
 * 第二层「承认」：整段没有资料支撑时，在正文上方先给一句结论。
 *
 * 这一档不拒答——同主题但资料里没写，拒了会误伤真实问题。但读者必须在开始读之前就知道：
 * 下面这些话没有出处可核。判定由服务端算好（supportVerdict），这里只负责显示，不自己数。
 */
const supportNote = computed(() => {
  if (props.refusal) return null
  const r = props.citations
  if (!r || r.supportVerdict !== 'unsupported') return null
  return `${r.sentenceCount} 句里有 ${r.uncitedSentenceCount} 句没有标出处，建议对照原文核对`
})

async function copyText(text: string, tip = '已复制'): Promise<void> {
  if (!text) return
  try {
    await navigator.clipboard.writeText(text)
    ElMessage.success(tip)
  } catch {
    // 剪贴板不可用（非安全上下文等）：退回复制区方案
    const ta = document.createElement('textarea')
    ta.value = text
    ta.style.position = 'fixed'
    ta.style.opacity = '0'
    document.body.appendChild(ta)
    ta.select()
    try {
      document.execCommand('copy')
      ElMessage.success(tip)
    } catch {
      ElMessage.warning('复制失败，请手动选择文本')
    } finally {
      document.body.removeChild(ta)
    }
  }
}

/** 代码块内的复制按钮用事件委托（v-html 内容不在模板作用域内）。 */
function onMarkdownClick(e: MouseEvent): void {
  const btn = (e.target as HTMLElement).closest('.code-copy') as HTMLElement | null
  if (!btn) return
  const code = btn.closest('.code-block')?.querySelector('pre code')
  if (code?.textContent) void copyText(code.textContent, '代码已复制')
}

/**
 * 空正文有三种意思，必须分开说（C-V11 的界面一侧）。
 *
 * 三种都画成"一条空回答"，等于把三个排查方向压成一个：还在跑、跑完了但失败、
 * 以及"这一轮什么也没留下"。判据来自那条 run 的终态，不在这里猜。
 * `pending` 是当次推送的进行中（有打字动画），不走这里。
 */
const emptyNote = computed(() => {
  if (props.message.role !== 'assistant' || props.message.pending) return ''
  if (props.refusal || bodyText.value) return ''
  if (props.stopStatus === 'cancelled') return '已停止生成。'
  const status = props.message.runStatus
  if (status === 'running') return '这一轮还没有结束。进程若已中断，它会一直停在这里。'
  if (status === 'cancelled') return '这一轮已停止。'
  if (status === 'failed' || status === 'limited') {
    return '这一轮没有答上来，展开下面的执行过程看原因。'
  }
  return '这一轮没有留下正文。'
})

function pageLabel(s: RetrievalSource): string {
  if (s.pageFrom == null) return ''
  if (s.pageTo != null && s.pageTo !== s.pageFrom) return `P${s.pageFrom}–${s.pageTo}`
  return `P${s.pageFrom}`
}
</script>

<template>
  <div class="msg" :class="message.role">
    <div class="msg-avatar" :class="message.role">
      <el-icon v-if="message.role === 'assistant'"><MagicStick /></el-icon>
      <span v-else>我</span>
    </div>

    <div class="msg-main">
      <div class="msg-name">{{ message.role === 'assistant' ? 'Agent' : '我' }}</div>

      <div class="msg-bubble" :class="message.role">
        <div v-if="refusal" class="refusal-card">
          <div class="refusal-head">
            <span class="refusal-tag">依据不足</span>
            <span class="refusal-code">{{ refusal.code }}</span>
          </div>
          <p class="refusal-text">{{ refusal.text }}</p>
        </div>
        <template v-else-if="message.role === 'assistant'">
          <div v-if="mode === 'general'" class="general-note">
            <span class="general-tag">直答</span>
            <span class="general-text">未使用企业资料</span>
          </div>
          <div v-if="supportNote" class="support-note">
            <span class="support-tag">无资料支撑</span>
            <span class="support-text">{{ supportNote }}</span>
          </div>
          <div class="markdown" v-html="html" @click="onMarkdownClick" />
          <p v-if="emptyNote" class="empty-answer">{{ emptyNote }}</p>
        </template>
        <template v-else>{{ message.content }}</template>

        <span v-if="message.pending && !message.content" class="typing">
          <i /><i /><i />
        </span>

        <button
          v-if="bodyText && !message.pending"
          class="msg-copy"
          :class="message.role"
          title="复制全文"
          @click="copyText(bodyText)"
        >
          <el-icon><CopyDocument /></el-icon>
        </button>
      </div>

      <div v-if="citeSummary" class="cite-summary" title="引用体检：句级统计来自服务端的两级校验">
        <span class="cite-summary-dot" />{{ citeSummary }}
      </div>

      <!-- 动作卡片放在「执行过程」之上：单子是产品的主语，工具调用只是它的来路。
           藏在折叠面板里的话，模型挂起一单等人批准，用户在对话里看不见。 -->
      <ActionCards v-if="message.role === 'assistant'" :tools="tools" />

      <RunTrace v-if="message.role === 'assistant'" :tools="tools" :run="run" :stop-status="stopStatus" />

      <div v-if="message.role === 'assistant' && !refusal && sourceList.length" class="sources">
        <button class="sources-toggle" @click="sourcesOpen = !sourcesOpen">
          <el-icon><Connection /></el-icon>
          <span>引用来源 · {{ sourceList.length }}</span>
          <el-icon class="chevron" :class="{ open: sourcesOpen }"><ArrowRight /></el-icon>
        </button>
        <div v-if="sourcesOpen" class="sources-panel">
          <div v-for="(s, i) in sourceList" :key="s.chunkId" class="source-item">
            <div class="source-head">
              <span class="source-idx">[{{ i + 1 }}]</span>
              <span class="source-file">《{{ s.fileName }}》</span>
              <span class="source-meta">
                片段 #{{ s.seq }}<template v-if="pageLabel(s)"> · {{ pageLabel(s) }}</template>
              </span>
              <span v-if="s.fusedScore != null" class="source-score" title="RRF 融合分">
                RRF {{ s.fusedScore.toFixed(3) }}
              </span>
            </div>
            <div class="source-content">{{ s.content }}</div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.msg {
  display: flex;
  gap: 12px;
  margin-bottom: 22px;
  animation: aw-msg-in 260ms var(--aw-ease);
}

.msg.user {
  flex-direction: row-reverse;
}

.msg-avatar {
  flex: none;
  width: 34px;
  height: 34px;
  border-radius: 11px;
  display: grid;
  place-items: center;
  font-size: 13px;
  font-weight: 600;
}

.msg-avatar.assistant {
  background: linear-gradient(135deg, var(--aw-primary), #7c6cf0);
  color: #fff;
  box-shadow: 0 2px 8px rgb(79 70 229 / 0.32);
}

.msg-avatar.assistant .el-icon {
  font-size: 17px;
}

.msg-avatar.user {
  background: var(--aw-surface-3);
  color: var(--aw-text-2);
  border: 1px solid var(--aw-border);
}

.msg-main {
  max-width: min(76%, 780px);
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.msg.user .msg-main {
  align-items: flex-end;
}

.msg-name {
  font-size: 12px;
  color: var(--aw-text-3);
  margin: 0 4px 5px;
}

.msg-bubble {
  position: relative;
  padding: 11px 15px;
  border-radius: var(--aw-radius-l);
  font-size: 14.5px;
  line-height: 1.75;
  word-break: break-word;
}

.msg-bubble.assistant {
  background: var(--aw-surface);
  border: 1px solid var(--aw-border);
  border-top-left-radius: 5px;
  box-shadow: var(--aw-shadow-1);
  color: var(--aw-text);
}

.msg-bubble.user {
  background: linear-gradient(135deg, var(--aw-primary), var(--aw-primary-hover));
  color: #fff;
  border-top-right-radius: 5px;
  box-shadow: 0 2px 10px rgb(79 70 229 / 0.28);
  white-space: pre-wrap;
}

.msg-copy {
  position: absolute;
  top: 6px;
  right: 6px;
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--aw-text-3);
  cursor: pointer;
  opacity: 0;
  transition: opacity var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease),
    background-color var(--aw-dur) var(--aw-ease);
}

.msg-bubble:hover .msg-copy {
  opacity: 1;
}

.msg-copy:hover {
  color: var(--aw-primary);
  background: var(--aw-surface-3);
}

.msg-copy.user {
  color: rgb(255 255 255 / 0.8);
}

.msg-copy.user:hover {
  color: #fff;
  background: rgb(255 255 255 / 0.18);
}

/* ---------- Markdown ---------- */
/* 空正文的说明。三种来源（还没跑完 / 跑完失败 / 什么也没留）共用这一处，
   文案由 emptyNote 决定——它是"这一轮怎么了"，不是模型说的话，所以用弱语气。 */
.empty-answer {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.6;
  color: var(--aw-text-3);
}

.markdown :deep(p) {
  margin: 0 0 10px;
}

.markdown :deep(p:last-child) {
  margin-bottom: 0;
}

.markdown :deep(ul),
.markdown :deep(ol) {
  margin: 6px 0 10px;
  padding-left: 22px;
}

.markdown :deep(li) {
  margin: 3px 0;
}

.markdown :deep(h1),
.markdown :deep(h2),
.markdown :deep(h3),
.markdown :deep(h4) {
  margin: 14px 0 8px;
  line-height: 1.4;
  font-weight: 600;
}

.markdown :deep(h1) {
  font-size: 19px;
}

.markdown :deep(h2) {
  font-size: 17px;
}

.markdown :deep(h3) {
  font-size: 15.5px;
}

.markdown :deep(a) {
  color: var(--aw-primary);
  text-decoration: none;
  border-bottom: 1px solid var(--el-color-primary-light-7);
}

.markdown :deep(a:hover) {
  border-bottom-color: var(--aw-primary);
}

.markdown :deep(blockquote) {
  margin: 8px 0;
  padding: 4px 14px;
  border-left: 3px solid var(--el-color-primary-light-5);
  background: var(--aw-surface-2);
  border-radius: 0 var(--aw-radius-s) var(--aw-radius-s) 0;
  color: var(--aw-text-2);
}

.markdown :deep(table) {
  border-collapse: collapse;
  margin: 10px 0;
  font-size: 13.5px;
  display: block;
  overflow-x: auto;
}

.markdown :deep(th),
.markdown :deep(td) {
  border: 1px solid var(--aw-border);
  padding: 6px 12px;
}

.markdown :deep(th) {
  background: var(--aw-surface-2);
  font-weight: 600;
}

.markdown :deep(code) {
  font-family: "Cascadia Code", "JetBrains Mono", Consolas, monospace;
  font-size: 0.88em;
}

.markdown :deep(:not(pre) > code) {
  background: var(--aw-surface-3);
  border: 1px solid var(--aw-border);
  border-radius: 6px;
  padding: 1px 6px;
}

.markdown :deep(.code-block) {
  margin: 10px 0;
  border-radius: var(--aw-radius-m);
  border: 1px solid var(--aw-border);
  background: var(--aw-surface-2);
  overflow: hidden;
}

.markdown :deep(.code-head) {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 5px 8px 5px 13px;
  background: var(--aw-surface-3);
  border-bottom: 1px solid var(--aw-border);
  font-size: 12px;
  color: var(--aw-text-2);
}

.markdown :deep(.code-lang) {
  font-weight: 500;
  letter-spacing: 0.02em;
}

.markdown :deep(.code-copy) {
  border: none;
  background: transparent;
  cursor: pointer;
  font-size: 12px;
  color: var(--aw-text-2);
  padding: 3px 9px;
  border-radius: 6px;
  transition: color var(--aw-dur) var(--aw-ease), background-color var(--aw-dur) var(--aw-ease);
}

.markdown :deep(.code-copy:hover) {
  color: var(--aw-primary);
  background: var(--aw-surface);
}

.markdown :deep(pre.hljs) {
  margin: 0;
  padding: 13px 15px;
  background: transparent;
  font-size: 13px;
  line-height: 1.65;
  overflow-x: auto;
}

/* ---------- 打字动画 ---------- */
.typing {
  display: inline-flex;
  gap: 5px;
  padding: 5px 2px;
}

.typing i {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--aw-text-3);
  animation: aw-blink 1.2s infinite both;
}

.typing i:nth-child(2) {
  animation-delay: 0.2s;
}

.typing i:nth-child(3) {
  animation-delay: 0.4s;
}

@keyframes aw-blink {
  0%,
  80%,
  100% {
    opacity: 0.25;
    transform: scale(0.85);
  }
  40% {
    opacity: 1;
    transform: scale(1);
  }
}

/* ---------- 引用角标与无出处底纹 ---------- */
/* 角标本体：内联在正文里，不点开就看得出"这句有出处"。 */
.markdown :deep(.cite) {
  position: relative;
  cursor: default;
  margin: 0 1px;
}

.markdown :deep(.cite-mark) {
  color: var(--aw-primary);
  font-size: 11px;
  font-weight: 600;
  line-height: 1;
  vertical-align: super;
}

/* 指向错块：号码存在、内容对不上。黄色表示"存疑"，不是"删除"。 */
.markdown :deep(.cite-bad .cite-mark) {
  color: #b45309;
  background: #fef3c7;
  border-radius: 4px;
  padding: 1px 3px;
}

.markdown :deep(.cite-card) {
  display: none;
  position: absolute;
  bottom: calc(100% + 8px);
  left: 50%;
  transform: translateX(-50%);
  z-index: 30;
  width: 320px;
  padding: 10px 12px;
  border-radius: var(--aw-radius-m);
  border: 1px solid var(--aw-border);
  background: var(--aw-surface);
  box-shadow: var(--aw-shadow-2, 0 8px 24px rgb(15 23 42 / 0.14));
  text-align: left;
  font-size: 12px;
  line-height: 1.6;
  font-weight: 400;
  color: var(--aw-text-2);
  white-space: normal;
}

/* 贴近视口边缘的角标卡片会被裁剪，光标移入正文气泡时仍要能看全——允许气泡溢出显示。 */
.markdown :deep(.cite:hover .cite-card),
.markdown :deep(.cite:focus .cite-card) {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.markdown :deep(.cite-card-head) {
  font-weight: 600;
  color: var(--aw-text);
}

.markdown :deep(.cite-card-body) {
  color: var(--aw-text-2);
  max-height: 120px;
  overflow-y: auto;
}

.markdown :deep(.cite-card-warn) {
  color: #b45309;
  font-weight: 500;
}

/* 没有出处的句子：不点开就看得出"这句没依据"。虚线底纹比高亮轻，提示而不警告。 */
.markdown :deep(.sent-uncited) {
  border-bottom: 1px dashed color-mix(in srgb, var(--aw-text-3) 55%, transparent);
  padding-bottom: 1px;
}

/* ---------- 引用体检摘要 ---------- */
.cite-summary {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-top: 6px;
  font-size: 12px;
  color: var(--aw-text-3);
}

.cite-summary-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--aw-primary);
  opacity: 0.55;
}

/* ---------- 引用来源 ---------- */
.sources {
  margin-top: 8px;
  align-self: stretch;
  max-width: 100%;
}

.sources-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--aw-border);
  background: var(--aw-surface-2);
  border-radius: var(--aw-radius-full);
  padding: 4px 12px;
  font-size: 12.5px;
  color: var(--aw-text-2);
  cursor: pointer;
  transition: border-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease),
    background-color var(--aw-dur) var(--aw-ease);
}

.sources-toggle:hover {
  border-color: var(--el-color-primary-light-5);
  color: var(--aw-primary);
  background: var(--el-color-primary-light-9);
}

.sources-toggle .chevron {
  transition: transform var(--aw-dur) var(--aw-ease);
}

.sources-toggle .chevron.open {
  transform: rotate(90deg);
}

.sources-panel {
  margin-top: 8px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  background: var(--aw-surface-2);
  overflow: hidden;
  animation: aw-fade-in var(--aw-dur) var(--aw-ease);
}

.source-item {
  padding: 10px 13px;
  border-bottom: 1px solid var(--aw-border);
}

.source-item:last-child {
  border-bottom: none;
}

.source-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  font-size: 12.5px;
  color: var(--aw-text-2);
}

.source-idx {
  font-weight: 600;
  color: var(--aw-primary);
}

.source-file {
  font-weight: 500;
  color: var(--aw-text);
}

.source-meta {
  color: var(--aw-text-3);
}

.source-score {
  margin-left: auto;
  font-family: Consolas, monospace;
  font-size: 11.5px;
  color: var(--aw-text-3);
  background: var(--aw-surface);
  border: 1px solid var(--aw-border);
  border-radius: 6px;
  padding: 0 7px;
}

.source-content {
  margin-top: 5px;
  font-size: 12.5px;
  line-height: 1.65;
  color: var(--aw-text-2);
  white-space: pre-wrap;
  max-height: 132px;
  overflow-y: auto;
}

/* ---------- 拒答 ---------- */
/* 拒答是系统的结论，不是模型说的话：用边框与标签把它和普通答复区分开，
   否则用户会把固定文案当成模型自己承认"不知道"。 */
.refusal-card {
  border: 1px solid var(--aw-border);
  border-left: 3px solid #d97706;
  background: var(--aw-surface-3);
  border-radius: var(--aw-radius-l);
  padding: 10px 12px;
}

.refusal-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
}

.refusal-tag {
  font-size: 12px;
  font-weight: 600;
  color: #b45309;
}

.refusal-code {
  font-size: 11px;
  color: var(--aw-text-3);
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
}

.refusal-text {
  margin: 0;
  color: var(--aw-text);
  line-height: 1.7;
}

/* ---------- 无资料支撑 ---------- */
/* 直答的合同标识：它不是“资料不足”，也不是拒答；它只是明确告诉读者这条回答
   没有引用企业资料——跑完后一条来源都没有，才打这个标（见 stores/agent.ts 的 ReceiveDone）。 */
.general-note {
  display: flex;
  align-items: baseline;
  gap: 8px;
  margin-bottom: 8px;
  padding: 6px 10px;
  border-radius: var(--aw-radius-l);
  border-left: 3px solid var(--aw-primary);
  background: var(--el-color-primary-light-9);
}

.general-tag {
  flex: none;
  font-size: 12px;
  font-weight: 600;
  color: var(--aw-primary);
}

.general-text {
  font-size: 12px;
  color: var(--aw-text-3);
  line-height: 1.5;
}

/* 与拒答卡片刻意区别：拒答是系统不给答案（橙色粗边框、独立卡片）；
   这一层答案照给，只是先告诉读者"下面没有出处可核"，所以是正文上方的一条窄提示，不遮内容。 */
.support-note {
  display: flex;
  align-items: baseline;
  gap: 8px;
  margin-bottom: 8px;
  padding: 6px 10px;
  border-radius: var(--aw-radius-l);
  border-left: 3px solid #d97706;
  background: var(--aw-surface-3);
}

.support-tag {
  flex: none;
  font-size: 12px;
  font-weight: 600;
  color: #b45309;
}

.support-text {
  font-size: 12px;
  color: var(--aw-text-3);
  line-height: 1.5;
}
</style>

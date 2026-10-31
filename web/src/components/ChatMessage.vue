<script setup lang="ts">
import { computed, ref } from 'vue'
import { ArrowRight, CopyDocument, MagicStick, Connection } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { renderMarkdown } from '@/utils/markdown'
import type { RetrievalSource, RunSummary, ToolStepEvent } from '@/types'
import type { ChatMessage } from '@/stores/agent'
import RunTrace from '@/components/RunTrace.vue'

const props = defineProps<{
  message: ChatMessage
  sources?: RetrievalSource[]
  tools?: ToolStepEvent[]
  run?: RunSummary
  stopStatus?: string
}>()

const sourcesOpen = ref(false)
const sourceList = computed(() => props.sources ?? [])
const html = computed(() =>
  props.message.role === 'assistant' ? renderMarkdown(props.message.content) : '',
)

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
        <div v-if="message.role === 'assistant'" class="markdown" v-html="html" @click="onMarkdownClick" />
        <template v-else>{{ message.content }}</template>

        <span v-if="message.pending && !message.content" class="typing">
          <i /><i /><i />
        </span>

        <button
          v-if="message.content && !message.pending"
          class="msg-copy"
          :class="message.role"
          title="复制全文"
          @click="copyText(message.content)"
        >
          <el-icon><CopyDocument /></el-icon>
        </button>
      </div>

      <RunTrace v-if="message.role === 'assistant'" :tools="tools" :run="run" :stop-status="stopStatus" />

      <div v-if="message.role === 'assistant' && sourceList.length" class="sources">
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
</style>

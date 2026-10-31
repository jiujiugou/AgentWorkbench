<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ArrowDown, Delete, MagicStick, Promotion, VideoPause } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import ChatMessage from '@/components/ChatMessage.vue'
import { useAgentStore } from '@/stores/agent'

const agent = useAgentStore()
const router = useRouter()

const input = ref('')
const textareaRef = ref<HTMLTextAreaElement | null>(null)
const listRef = ref<HTMLElement | null>(null)
const knowledgeMode = ref(false)
const atBottom = ref(true)

const hasModels = computed(() => agent.modelOptions.length > 0)
const providersWithModels = computed(() => agent.providers.filter((p) => p.models.length > 0))

const currentTitle = computed(() => {
  const c = agent.conversations.find((x) => x.id === agent.conversationId)
  return c ? agent.titleFor(c) : '新对话'
})

const showJump = computed(() => !atBottom.value && agent.messages.length > 0)

const suggestions = [
  '你好，请介绍一下你自己',
  '帮我解释一下 RAG 的工作原理',
  '用 Python 写一个快速排序，并说明时间复杂度',
  '把这段话改写得更专业：今天开会讨论了项目进度，感觉有点慢。',
]

function scrollToBottom(smooth = false): void {
  const el = listRef.value
  if (!el) return
  el.scrollTo({ top: el.scrollHeight, behavior: smooth ? 'smooth' : 'auto' })
  atBottom.value = true
}

function onScroll(): void {
  const el = listRef.value
  if (!el) return
  atBottom.value = el.scrollHeight - el.scrollTop - el.clientHeight < 80
}

/** 流式过程中始终贴底（用户主动上滚时则不打扰）。 */
watch(
  () => {
    const last = agent.messages[agent.messages.length - 1]
    return agent.messages.length + ':' + (last?.content.length ?? 0)
  },
  () => {
    if (atBottom.value) void nextTick(() => scrollToBottom())
  },
)

function autoResize(): void {
  const el = textareaRef.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = Math.min(el.scrollHeight, 180) + 'px'
}

async function handleSend(text?: string): Promise<void> {
  const content = (text ?? input.value).trim()
  if (!content || agent.generating) return
  input.value = ''
  void nextTick(autoResize)
  atBottom.value = true
  await (knowledgeMode.value ? agent.ask(content) : agent.send(content))
}

function onKeydown(e: KeyboardEvent): void {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault()
    void handleSend()
  }
}

async function handleStop(): Promise<void> {
  await agent.stop()
}

async function handleClear(): Promise<void> {
  await agent.clearCurrent()
  ElMessage.success('已清空当前对话')
}

onMounted(() => {
  void agent.connect()
  void nextTick(() => scrollToBottom())
})
</script>

<template>
  <div class="chat-page">
    <header class="chat-header">
      <div class="chat-heading">
        <h1 class="chat-title" :title="currentTitle">{{ currentTitle }}</h1>
        <span v-if="knowledgeMode" class="mode-badge">知识库问答</span>
      </div>

      <div class="chat-actions">
        <div class="mode-toggle">
          <button :class="{ on: !knowledgeMode }" @click="knowledgeMode = false">对话</button>
          <button :class="{ on: knowledgeMode }" @click="knowledgeMode = true">知识库问答</button>
        </div>

        <el-select
          v-if="hasModels"
          v-model="agent.selectedModelId"
          class="model-select"
          size="default"
          filterable
          placeholder="选择模型"
          @change="agent.switchModel"
        >
          <el-option-group v-for="p in providersWithModels" :key="p.id" :label="p.name">
            <el-option
              v-for="m in p.models"
              :key="m.id"
              :label="m.displayName?.trim() || m.model"
              :value="m.id"
            >
              <span>{{ m.displayName?.trim() || m.model }}</span>
              <span v-if="!p.isActive" class="opt-tag">其他供应商</span>
              <span v-else-if="m.isDefault" class="opt-tag current">当前</span>
            </el-option>
          </el-option-group>
        </el-select>
        <el-button v-else round type="warning" plain size="small" @click="router.push('/settings')">
          未配置模型 · 去设置
        </el-button>

        <el-button :icon="Delete" text round @click="handleClear">清空</el-button>
      </div>
    </header>

    <div ref="listRef" class="chat-scroll" @scroll="onScroll">
      <div v-if="agent.messages.length === 0" class="welcome">
        <div class="welcome-icon">
          <el-icon><MagicStick /></el-icon>
        </div>
        <h2>有什么可以帮忙的？</h2>
        <p>「对话」直接聊天；「知识库问答」会在需要时检索知识库并给出引用来源。</p>
        <div class="suggestions">
          <button v-for="s in suggestions" :key="s" class="suggestion" @click="handleSend(s)">
            {{ s }}
          </button>
        </div>
      </div>

      <template v-else>
        <ChatMessage
          v-for="m in agent.messages"
          :key="m.id"
          :message="m"
          :sources="agent.sources[m.id]"
          :tools="agent.toolSteps[m.id]"
          :run="agent.runs[m.id]"
          :stop-status="agent.stops[m.id]"
        />
      </template>
    </div>

    <transition name="aw-pop">
      <button v-if="showJump" class="jump-btn" title="回到底部" @click="scrollToBottom(true)">
        <el-icon><ArrowDown /></el-icon>
      </button>
    </transition>

    <div class="composer">
      <div class="composer-inner">
        <textarea
          ref="textareaRef"
          v-model="input"
          rows="1"
          placeholder="输入消息，Enter 发送，Shift+Enter 换行"
          @keydown="onKeydown"
          @input="autoResize"
        />
        <div class="composer-actions">
          <span class="hint">Enter 发送 · Shift+Enter 换行</span>
          <div class="composer-buttons">
            <el-button v-if="agent.generating" round type="danger" plain :icon="VideoPause" @click="handleStop">
              停止
            </el-button>
            <el-button
              v-else
              round
              type="primary"
              :icon="Promotion"
              :disabled="!input.trim()"
              @click="handleSend()"
            >
              发送
            </el-button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.chat-page {
  position: relative;
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--aw-surface);
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-l);
  box-shadow: var(--aw-shadow-1);
  overflow: hidden;
}

/* ---------- 顶栏 ---------- */
.chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px 16px;
  flex-wrap: wrap;
  padding: 11px 18px;
  border-bottom: 1px solid var(--aw-border);
}

.chat-heading {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.chat-title {
  margin: 0;
  font-size: 15px;
  font-weight: 650;
  max-width: 340px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mode-badge {
  flex: none;
  font-size: 11.5px;
  color: var(--aw-primary);
  background: var(--el-color-primary-light-9);
  border-radius: var(--aw-radius-full);
  padding: 2px 10px;
}

.chat-actions {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.mode-toggle {
  display: inline-flex;
  gap: 2px;
  padding: 3px;
  background: var(--aw-surface-3);
  border-radius: var(--aw-radius-full);
}

.mode-toggle button {
  border: none;
  background: transparent;
  padding: 5px 14px;
  border-radius: var(--aw-radius-full);
  font-size: 12.5px;
  color: var(--aw-text-2);
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease),
    box-shadow var(--aw-dur) var(--aw-ease);
}

.mode-toggle button:hover {
  color: var(--aw-text);
}

.mode-toggle button.on {
  background: var(--aw-surface);
  color: var(--aw-primary);
  font-weight: 550;
  box-shadow: var(--aw-shadow-1);
}

.model-select {
  width: 230px;
}

.opt-tag {
  float: right;
  margin-left: 10px;
  font-size: 11px;
  color: var(--aw-text-3);
}

.opt-tag.current {
  color: var(--aw-success);
}

/* ---------- 消息区 ---------- */
.chat-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 22px 26px;
  overscroll-behavior: contain;
}

.welcome {
  height: 100%;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  text-align: center;
  animation: aw-fade-in 320ms var(--aw-ease);
}

.welcome-icon {
  width: 58px;
  height: 58px;
  border-radius: 18px;
  display: grid;
  place-items: center;
  color: #fff;
  font-size: 26px;
  background: linear-gradient(135deg, var(--aw-primary), #8b7cf6);
  box-shadow: 0 6px 20px rgb(79 70 229 / 0.3);
  margin-bottom: 12px;
}

.welcome h2 {
  margin: 0;
  font-size: 21px;
  font-weight: 650;
}

.welcome p {
  margin: 8px 0 20px;
  max-width: 430px;
  font-size: 13.5px;
  line-height: 1.7;
  color: var(--aw-text-2);
}

.suggestions {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  justify-content: center;
  max-width: 580px;
}

.suggestion {
  border: 1px solid var(--aw-border);
  background: var(--aw-surface-2);
  border-radius: var(--aw-radius-full);
  padding: 8px 16px;
  font-size: 13px;
  color: var(--aw-text-2);
  cursor: pointer;
  transition: border-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease),
    background-color var(--aw-dur) var(--aw-ease), transform var(--aw-dur) var(--aw-ease);
}

.suggestion:hover {
  border-color: var(--el-color-primary-light-5);
  color: var(--aw-primary);
  background: var(--el-color-primary-light-9);
  transform: translateY(-1px);
}

/* ---------- 回到底部 ---------- */
.jump-btn {
  position: absolute;
  right: 30px;
  bottom: 132px;
  width: 36px;
  height: 36px;
  display: grid;
  place-items: center;
  border: 1px solid var(--aw-border);
  border-radius: 50%;
  background: var(--aw-surface);
  color: var(--aw-text-2);
  cursor: pointer;
  box-shadow: var(--aw-shadow-2);
  transition: color var(--aw-dur) var(--aw-ease), border-color var(--aw-dur) var(--aw-ease),
    transform var(--aw-dur) var(--aw-ease);
}

.jump-btn:hover {
  color: var(--aw-primary);
  border-color: var(--el-color-primary-light-5);
  transform: translateY(1px);
}

.aw-pop-enter-active,
.aw-pop-leave-active {
  transition: opacity var(--aw-dur) var(--aw-ease), transform var(--aw-dur) var(--aw-ease);
}

.aw-pop-enter-from,
.aw-pop-leave-to {
  opacity: 0;
  transform: translateY(6px);
}

/* ---------- 输入区 ---------- */
.composer {
  flex: none;
  border-top: 1px solid var(--aw-border);
  padding: 13px 18px 15px;
  background: var(--aw-surface);
}

.composer-inner {
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-l);
  background: var(--aw-surface-2);
  padding: 10px 12px 8px;
  transition: border-color var(--aw-dur) var(--aw-ease), box-shadow var(--aw-dur) var(--aw-ease),
    background-color var(--aw-dur) var(--aw-ease);
}

.composer-inner:focus-within {
  border-color: var(--aw-primary);
  background: var(--aw-surface);
  box-shadow: 0 0 0 3px rgb(79 70 229 / 0.1);
}

.composer-inner textarea {
  display: block;
  width: 100%;
  border: none;
  outline: none;
  resize: none;
  background: transparent;
  font-family: inherit;
  font-size: 14px;
  line-height: 1.65;
  color: var(--aw-text);
  max-height: 180px;
  padding: 2px 2px 8px;
}

.composer-inner textarea::placeholder {
  color: var(--aw-text-3);
}

.composer-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.hint {
  font-size: 11.5px;
  color: var(--aw-text-3);
}

.composer-buttons {
  display: flex;
  gap: 8px;
}

@media (max-width: 640px) {
  .chat-scroll {
    padding: 16px 14px;
  }

  .hint {
    display: none;
  }
}
</style>

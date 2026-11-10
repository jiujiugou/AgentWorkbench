<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ArrowDown, Delete, MagicStick, Promotion, VideoPause } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import ChatMessage from '@/components/ChatMessage.vue'
import { QUICK_STARTS } from '@/suggestions'
import { useAgentStore } from '@/stores/agent'

const agent = useAgentStore()
const router = useRouter()

const input = ref('')
const textareaRef = ref<HTMLTextAreaElement | null>(null)
const listRef = ref<HTMLElement | null>(null)
const atBottom = ref(true)

const hasModels = computed(() => agent.modelOptions.length > 0)
const providersWithModels = computed(() => agent.providers.filter((p) => p.models.length > 0))

// 只有一个入口：说什么由模型判断该查资料、该查系统还是该直答。
// 曾经这里有一个「通用对话 / 知识库问答」开关，它不改变工具集、只改一句措辞，
// 给的是一个不存在的选择权——已删。

const currentTitle = computed(() => {
  const c = agent.conversations.find((x) => x.id === agent.conversationId)
  return c ? agent.titleFor(c) : '新对话'
})

const showJump = computed(() => !atBottom.value && agent.messages.length > 0)

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
  await agent.send(content)
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

/**
 * 消费新建对话弹窗带过来的第一句。
 *
 * 两处都要调：已经在对话页时靠 `watch`（弹窗不改路由），从别的页进来时
 * 值早就写好了、`watch` 不会触发，只能靠 `onMounted`。**读走就清空**，
 * 否则来回切页会重复发送。
 */
function consumePending(): void {
  const question = agent.pendingQuestion
  if (!question) return
  agent.pendingQuestion = ''
  void handleSend(question)
}

watch(() => agent.pendingQuestion, consumePending)

onMounted(() => {
  // C# 后端：问答走 /hubs/agent 的 Send / Ask，进页面先建好 Hub 连接。
  void agent.connect()
  void nextTick(() => scrollToBottom())
  consumePending()
})
</script>

<template>
  <div class="chat-page">
    <header class="chat-header">
      <div class="chat-heading">
        <h1 class="chat-title" :title="currentTitle">{{ currentTitle }}</h1>
      </div>

      <div class="chat-actions">
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
      <div class="chat-inner">
        <div v-if="agent.messages.length === 0" class="welcome">
          <div class="welcome-icon">
            <el-icon><MagicStick /></el-icon>
          </div>
          <h2>有什么可以帮忙的？</h2>
          <p>涉及企业资料会自动检索并标注出处；需要查询或改动企业系统时会先向你确认。</p>
          <div class="suggestions">
            <button v-for="s in QUICK_STARTS" :key="s" class="suggestion" @click="handleSend(s)">
              {{ s }}
            </button>
          </div>
        </div>

        <template v-else>
          <ChatMessage
            v-for="m in agent.messages"
            :key="m.id"
            :message="m"
            :mode="m.mode"
            :sources="agent.sources[m.id]"
            :citations="agent.citations[m.id]"
            :refusal="agent.refusals[m.id]"
            :tools="agent.toolSteps[m.id]"
            :run="agent.runs[m.id]"
            :stop-status="agent.stops[m.id]"
          />
        </template>
      </div>
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
  min-width: 0;
  display: flex;
  flex-direction: column;
  background: var(--aw-surface);
  overflow: hidden;
}

/* ---------- 顶栏 ---------- */
.chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px 16px;
  flex-wrap: wrap;
  padding: 12px 26px;
  border-bottom: 1px solid var(--aw-border);
  background: var(--aw-surface);
}

.chat-heading {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.chat-title {
  margin: 0;
  font-size: 15.5px;
  font-weight: 650;
  letter-spacing: -0.01em;
  max-width: 420px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.chat-actions {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
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
  padding: 18px 26px 8px;
  overscroll-behavior: contain;
}

/* 内容限宽居中：整行铺满时，一行 1600px 的正文没人读得下去。
   注意限的是**内容**，不是背景——画布仍然铺满整个窗口。 */
.chat-inner {
  display: flex;
  flex-direction: column;
  width: 100%;
  max-width: 860px;
  min-height: 100%;
  margin: 0 auto;
}

.welcome {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  text-align: center;
  padding: 24px 0 40px;
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
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
  width: 100%;
  max-width: 640px;
}

.suggestion {
  border: 1px solid var(--aw-border);
  background: var(--aw-surface);
  border-radius: 16px;
  padding: 13px 16px;
  font-family: inherit;
  font-size: 13px;
  line-height: 1.55;
  text-align: left;
  color: var(--aw-text-2);
  cursor: pointer;
  transition: border-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease),
    background-color var(--aw-dur) var(--aw-ease), transform var(--aw-dur) var(--aw-ease),
    box-shadow var(--aw-dur) var(--aw-ease);
}

.suggestion:hover {
  border-color: var(--el-color-primary-light-5);
  color: var(--aw-primary);
  background: var(--el-color-primary-light-9);
  transform: translateY(-1px);
  box-shadow: 0 8px 20px rgb(79 70 229 / 0.08);
}

/* ---------- 回到底部 ---------- */
.jump-btn {
  position: absolute;
  right: 34px;
  bottom: 122px;
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
  padding: 8px 26px 18px;
  /* 顶部渐隐：消息滚到输入框后面时淡出，而不是被一条硬边框切断。 */
  background: linear-gradient(to top, var(--aw-surface) 72%, rgb(255 255 255 / 0));
}

.composer-inner {
  max-width: 860px;
  margin: 0 auto;
  border: 1px solid var(--aw-border);
  border-radius: 20px;
  background: var(--aw-surface);
  padding: 11px 13px 9px;
  box-shadow: 0 6px 24px rgb(23 26 43 / 0.07);
  transition: border-color var(--aw-dur) var(--aw-ease), box-shadow var(--aw-dur) var(--aw-ease),
    background-color var(--aw-dur) var(--aw-ease);
}

.composer-inner:focus-within {
  border-color: var(--aw-primary);
  box-shadow: 0 8px 28px rgb(79 70 229 / 0.14);
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
  justify-content: flex-end;
  gap: 10px;
}

.composer-buttons {
  display: flex;
  gap: 8px;
}

@media (max-width: 640px) {
  .chat-scroll {
    padding: 14px 16px 6px;
  }

  .composer {
    padding: 6px 16px 14px;
  }

  .suggestions {
    grid-template-columns: 1fr;
  }
}
</style>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { Delete, Promotion, VideoPause } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useAgent } from '@/composables/useAgent'
import { renderMarkdown } from '@/utils/markdown'
import { settingsApi, type Provider } from '@/api/settings'

const router = useRouter()
const { status, messages, generating, sources, connect, send, ask, stop, clear, restore } = useAgent()
const input = ref('')
const listRef = ref<HTMLElement | null>(null)
const knowledgeMode = ref(false)

interface ModelOption {
  modelId: number
  providerId: number
  providerName: string
  label: string
}

const providers = ref<Provider[]>([])
const selectedModelId = ref<number | null>(null)

/** 所有供应商下的模型拍平，供下拉分组切换（模型 id 全局唯一）。 */
const modelOptions = computed<ModelOption[]>(() =>
  providers.value.flatMap((p) =>
    p.models.map((m) => ({
      modelId: m.id,
      providerId: p.id,
      providerName: p.name,
      label: m.displayName?.trim() || m.model,
    })),
  ),
)

const hasModels = computed(() => modelOptions.value.length > 0)
const currentModel = computed(
  () => modelOptions.value.find((o) => o.modelId === selectedModelId.value) ?? null,
)

const providersWithModels = computed(() => providers.value.filter((p) => p.models.length > 0))

/** 默认选中：当前启用供应商的默认模型（无则取首个），否则清空无效选择。 */
function resolveSelection(list: Provider[]) {
  const active = list.find((p) => p.isActive) ?? null
  const activeModels = active?.models ?? []
  const def = activeModels.find((m) => m.isDefault) ?? activeModels[0]
  if (def) {
    selectedModelId.value = def.id
  } else if (!modelOptions.value.some((o) => o.modelId === selectedModelId.value)) {
    selectedModelId.value = null
  }
}

async function loadModels() {
  try {
    const list = await settingsApi.listProviders()
    providers.value = list
    resolveSelection(list)
  } catch {
    providers.value = []
    selectedModelId.value = null
  }
}

async function onModelChange(id: number) {
  const option = modelOptions.value.find((o) => o.modelId === id)
  if (!option) return
  const provider = providers.value.find((p) => p.id === option.providerId)
  if (!provider) return
  try {
    if (!provider.isActive) await settingsApi.activateProvider(provider.id)
    await settingsApi.setDefaultModel(provider.id, id)
    ElMessage.success(`已切换模型：${option.label}`)
  } catch (e) {
    ElMessage.error('切换失败：' + (e as Error).message)
  } finally {
    await loadModels()
  }
}

function gotoSettings() {
  void router.push('/settings')
}

const statusText: Record<string, string> = {
  disconnected: '未连接',
  connecting: '连接中…',
  connected: '已连接',
}
const statusType: Record<string, 'info' | 'warning' | 'success'> = {
  disconnected: 'info',
  connecting: 'warning',
  connected: 'success',
}

onMounted(() => {
  void connect()
  void loadModels()
  void restore()
})

watch(
  messages,
  () => {
    void nextTick(() => {
      const el = listRef.value
      if (el) el.scrollTop = el.scrollHeight
    })
  },
  { deep: true },
)

async function handleSend() {
  const text = input.value
  if (!text.trim() || generating.value) return
  input.value = ''
  await (knowledgeMode.value ? ask(text) : send(text))
}

function openSource(documentId: string, seq: number) {
  void router.push(`/documents/${documentId}?chunk=${seq}`)
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault()
    void handleSend()
  }
}

async function handleStop() {
  await stop()
}

async function handleClear() {
  await clear()
  ElMessage.success('已清空对话')
}
</script>

<template>
  <div class="chat">
    <div class="chat-toolbar">
      <el-tag :type="statusType[status]" effect="light" round>{{ statusText[status] }}</el-tag>
      <el-switch v-model="knowledgeMode" active-text="知识库问答" class="kb-switch" />
      <el-select
        v-if="hasModels"
        v-model="selectedModelId"
        class="model-select"
        size="small"
        filterable
        placeholder="选择模型"
        @change="onModelChange"
      >
        <el-option-group v-for="p in providersWithModels" :key="p.id" :label="p.name">
          <el-option v-for="m in p.models" :key="m.id" :label="m.displayName?.trim() || m.model" :value="m.id">
            <span>{{ m.displayName?.trim() || m.model }}</span>
            <el-tag v-if="!p.isActive" size="small" type="info" effect="plain" class="opt-tag">其他供应商</el-tag>
            <el-tag v-else-if="m.isDefault" size="small" type="success" effect="plain" class="opt-tag">当前</el-tag>
          </el-option>
        </el-option-group>
      </el-select>
      <el-button v-else size="small" type="warning" plain @click="gotoSettings">未配置模型 · 去设置</el-button>
      <span v-if="currentModel" class="model-hint">{{ currentModel.providerName }}</span>
      <div class="spacer" />
      <el-button :icon="Delete" text @click="handleClear">清空</el-button>
    </div>

    <div ref="listRef" class="chat-list">
      <el-empty v-if="messages.length === 0" description="开始和你的 Agent 聊聊吧" />
      <div v-for="m in messages" :key="m.id" class="row" :class="m.role">
        <div class="bubble" :class="m.role">
          <div v-if="m.role === 'assistant'" class="markdown" v-html="renderMarkdown(m.content)" />
          <template v-else>{{ m.content }}</template>
          <span v-if="m.pending && !m.content" class="typing">
            <i /><i /><i />
          </span>
          <div v-if="m.role === 'assistant' && sources[m.id]?.length" class="sources">
            <div class="sources-title">引用来源</div>
            <el-link
              v-for="(s, i) in sources[m.id]"
              :key="s.chunkId"
              type="primary"
              class="source-link"
              @click="openSource(s.documentId, s.seq)"
            >
              [{{ i + 1 }}] 《{{ s.fileName }}》· 片段#{{ s.seq }}
            </el-link>
          </div>
        </div>
      </div>
    </div>

    <div class="composer">
      <el-input
        v-model="input"
        type="textarea"
        :rows="2"
        resize="none"
        placeholder="输入消息，Enter 发送，Shift+Enter 换行"
        @keydown="onKeydown"
      />
      <el-button v-if="generating" type="danger" :icon="VideoPause" @click="handleStop">停止</el-button>
      <el-button v-else type="primary" :icon="Promotion" :disabled="!input.trim()" @click="handleSend">
        发送
      </el-button>
    </div>
  </div>
</template>

<style scoped>
.chat {
  height: 100%;
  display: flex;
  flex-direction: column;
  background: #fff;
  border-radius: 10px;
  border: 1px solid #e5e7eb;
  overflow: hidden;
}

.chat-toolbar {
  display: flex;
  align-items: center;
  padding: 8px 12px;
  border-bottom: 1px solid #eef0f3;
}

.spacer {
  flex: 1;
}

.kb-switch {
  margin-left: 12px;
}

.model-select {
  margin-left: 12px;
  width: 240px;
}

.model-hint {
  margin-left: 8px;
  font-size: 12px;
  color: #909399;
}

.opt-tag {
  margin-left: 8px;
}

.sources {
  margin-top: 10px;
  padding-top: 8px;
  border-top: 1px dashed #dcdfe6;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.sources-title {
  font-size: 12px;
  color: #909399;
}

.source-link {
  font-size: 13px;
  justify-content: flex-start;
}

.chat-list {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
}

.row {
  display: flex;
  margin-bottom: 12px;
}

.row.user {
  justify-content: flex-end;
}

.bubble {
  max-width: 76%;
  padding: 10px 14px;
  border-radius: 10px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 14px;
}

.bubble.user {
  background: #409eff;
  color: #fff;
  border-bottom-right-radius: 2px;
}

.bubble.assistant {
  background: #f4f5f7;
  color: #1f2328;
  border-bottom-left-radius: 2px;
}

.markdown :deep(pre.hljs) {
  background: #0d1117;
  color: #e6edf3;
  padding: 12px;
  border-radius: 8px;
  overflow-x: auto;
}

.markdown :deep(code) {
  font-family: "Cascadia Code", Consolas, monospace;
  font-size: 13px;
}

.markdown :deep(p) {
  margin: 0 0 8px;
}

.markdown :deep(p:last-child) {
  margin-bottom: 0;
}

.typing {
  display: inline-flex;
  gap: 4px;
  padding: 4px 0;
}

.typing i {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #909399;
  animation: blink 1.2s infinite both;
}

.typing i:nth-child(2) {
  animation-delay: 0.2s;
}

.typing i:nth-child(3) {
  animation-delay: 0.4s;
}

@keyframes blink {
  0%,
  80%,
  100% {
    opacity: 0.2;
  }
  40% {
    opacity: 1;
  }
}

.composer {
  display: flex;
  gap: 10px;
  padding: 12px;
  border-top: 1px solid #eef0f3;
  align-items: flex-end;
}
</style>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { documentsApi, type ChunkDto, type DocumentDto, type PageDto } from '@/api/documents'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const route = useRoute()
const router = useRouter()
const id = route.params.id as string

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const document = ref<DocumentDto | null>(null)
const pages = ref<PageDto[]>([])
const chunks = ref<ChunkDto[]>([])
const loading = ref(false)
const showCleaned = ref(true)
const activePage = ref<number | null>(null)

const highlightSeq = computed(() => {
  const raw = route.query.chunk
  if (raw === undefined || raw === null || raw === '') return null
  const seq = Number(raw)
  return Number.isNaN(seq) ? null : seq
})

async function load() {
  loading.value = true
  try {
    const [doc, pageList, chunkList] = await Promise.all([
      documentsApi.get(id),
      documentsApi.pages(id),
      documentsApi.chunks(id),
    ])
    document.value = doc
    pages.value = pageList
    chunks.value = chunkList
    if (activePage.value === null && pageList.length > 0) activePage.value = pageList[0].pageNo
    await nextTick()
    if (highlightSeq.value !== null) {
      window.document
        .getElementById(`chunk-${highlightSeq.value}`)
        ?.scrollIntoView({ block: 'center' })
    }
  } catch (e) {
    ElMessage.error('加载失败：' + (e as Error).message)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
  startHealthCheck()
})

const currentPage = computed(() => pages.value.find((p) => p.pageNo === activePage.value) ?? null)
const pageText = computed(() => {
  const page = currentPage.value
  if (!page) return ''
  return showCleaned.value ? page.cleanedText : page.rawText
})

function back() {
  void router.push('/documents')
}
</script>

<template>
  <div class="detail" v-loading="loading">
    <div class="toolbar">
      <el-button :icon="ArrowLeft" text @click="back">返回</el-button>
      <span class="file">{{ document?.fileName }}</span>
      <el-tag v-if="document" effect="light">{{ document.status }}</el-tag>
      <span class="meta" v-if="document">
        {{ document.pageCount }} 页 · {{ chunks.length }} 块
      </span>
      <div class="spacer" />
      <BackendStatus
        :status="backendHealth.status"
        :last-check="backendHealth.lastCheck"
        :error="backendHealth.error"
        :model-configured="backendHealth.modelConfigured"
        :show-last-check="true"
        size="small"
      />
      <el-button :icon="Refresh" text @click="load">刷新</el-button>
    </div>

    <div class="panes">
      <section class="pane">
        <header class="pane-header">
          <span>页文本</span>
          <div class="pane-actions">
            <el-radio-group v-model="showCleaned" size="small">
              <el-radio-button :value="true">清洗后</el-radio-button>
              <el-radio-button :value="false">原始</el-radio-button>
            </el-radio-group>
          </div>
        </header>
        <div class="page-tabs">
          <el-tag
            v-for="page in pages"
            :key="page.pageNo"
            class="page-tag"
            :effect="page.pageNo === activePage ? 'dark' : 'plain'"
            @click="activePage = page.pageNo"
          >
            第 {{ page.pageNo }} 页
          </el-tag>
        </div>
        <pre class="text">{{ pageText }}</pre>
      </section>

      <section class="pane">
        <header class="pane-header">
          <span>切分块（chunks）</span>
        </header>
        <div class="chunks">
          <el-empty v-if="chunks.length === 0" description="暂无切分块" />
          <article
            v-for="chunk in chunks"
            :id="'chunk-' + chunk.seq"
            :key="chunk.seq"
            class="chunk"
            :class="{ highlight: chunk.seq === highlightSeq }"
          >
            <div class="chunk-head">
              <span class="seq">#{{ chunk.seq + 1 }}</span>
              <span v-if="chunk.heading" class="heading">{{ chunk.heading }}</span>
              <span class="range">p{{ chunk.pageFrom }}–{{ chunk.pageTo }} · {{ chunk.charCount }} 字</span>
            </div>
            <pre class="text chunk-text">{{ chunk.content }}</pre>
          </article>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.detail {
  height: 100%;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  background: #fff;
  border: 1px solid #e5e7eb;
  border-radius: 8px;
  padding: 8px 12px;
}

.file {
  font-weight: 600;
}

.meta {
  color: #909399;
  font-size: 13px;
}

.spacer {
  flex: 1;
}

.panes {
  flex: 1;
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
  min-height: 0;
}

.pane {
  display: flex;
  flex-direction: column;
  background: #fff;
  border: 1px solid #e5e7eb;
  border-radius: 8px;
  min-height: 0;
  overflow: hidden;
}

.pane-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  border-bottom: 1px solid #eef0f3;
  font-weight: 600;
}

.page-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 8px 12px;
  border-bottom: 1px solid #eef0f3;
}

.page-tag {
  cursor: pointer;
}

.chunks {
  flex: 1;
  overflow-y: auto;
  padding: 8px 12px;
}

.chunk {
  border: 1px solid #eef0f3;
  border-radius: 6px;
  padding: 8px 10px;
  margin-bottom: 10px;
}

.chunk.highlight {
  border-color: #409eff;
  background: #ecf5ff;
  box-shadow: 0 0 0 2px rgba(64, 158, 255, 0.15);
}

.chunk-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
  font-size: 13px;
}

.seq {
  color: #409eff;
  font-weight: 600;
}

.heading {
  font-weight: 600;
}

.range {
  color: #909399;
  margin-left: auto;
}

.text {
  margin: 0;
  padding: 12px;
  flex: 1;
  overflow-y: auto;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: "Microsoft YaHei", monospace;
  font-size: 13px;
  line-height: 1.8;
  color: #1f2328;
}

.chunk-text {
  padding: 0;
  flex: none;
  max-height: 260px;
  overflow-y: auto;
  color: #303133;
}
</style>

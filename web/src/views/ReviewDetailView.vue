<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Download, Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { reviewApi, type ReviewElement, type ReviewFinding, type ReviewRun, type ReviewStep } from '@/api/review'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const route = useRoute()
const router = useRouter()
const id = Number(route.params.id)

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const run = ref<ReviewRun | null>(null)
const elements = ref<ReviewElement[]>([])
const findings = ref<ReviewFinding[]>([])
const loading = ref(false)
let timer: number | undefined

const severityType: Record<string, string> = {
  high: 'danger',
  medium: 'warning',
  low: 'info',
  info: 'info',
}

const severityText: Record<string, string> = {
  high: '高',
  medium: '中',
  low: '低',
  info: '提示',
}

const steps = computed<ReviewStep[]>(() => {
  const raw = run.value?.steps
  if (!raw) return []
  try {
    return JSON.parse(raw) as ReviewStep[]
  } catch {
    return []
  }
})

const active = computed(() => run.value?.status === 'pending' || run.value?.status === 'running')

async function load() {
  loading.value = true
  try {
    run.value = await reviewApi.run(id)
    const [els, finds] = await Promise.all([reviewApi.elements(id), reviewApi.findings(id)])
    elements.value = els
    findings.value = finds
  } catch (e) {
    ElMessage.error('加载失败：' + (e as Error).message)
  } finally {
    loading.value = false
  }
}

function schedule() {
  window.clearTimeout(timer)
  if (!active.value) return
  timer = window.setTimeout(async () => {
    await load()
    schedule()
  }, 1500)
}

onMounted(async () => {
  await load()
  startHealthCheck()
  schedule()
})

onUnmounted(() => window.clearTimeout(timer))

function openSource(seq?: number | null) {
  if (!run.value || seq === null || seq === undefined) return
  void router.push(`/documents/${run.value.documentId}?chunk=${seq}`)
}

async function exportReport(format: string) {
  try {
    const { content } = await reviewApi.report(id, format)
    const blob = new Blob([content], { type: 'text/plain;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `review-${id}.${format}`
    a.click()
    URL.revokeObjectURL(url)
  } catch (e) {
    ElMessage.error('导出失败：' + (e as Error).message)
  }
}

function score(value: number) {
  return value.toFixed(2)
}
</script>

<template>
  <div class="detail" v-loading="loading">
    <div class="toolbar">
      <el-button :icon="ArrowLeft" text @click="router.push('/review')">返回</el-button>
      <el-tag effect="light">审查 #{{ id }}</el-tag>
      <el-tag v-if="run" effect="light">{{ run.fileName }}</el-tag>
      <el-tag v-if="run" :type="active ? 'warning' : run.status === 'succeeded' ? 'success' : 'danger'" effect="light">
        {{ run.status }}
      </el-tag>
      <span class="spacer" />
      <BackendStatus
        :status="backendHealth.status"
        :last-check="backendHealth.lastCheck"
        :error="backendHealth.error"
        :model-configured="backendHealth.modelConfigured"
        :show-last-check="true"
        size="small"
      />
      <el-button :icon="Refresh" text @click="load">刷新</el-button>
      <el-button :icon="Download" text @click="exportReport('md')">导出 MD</el-button>
      <el-button :icon="Download" text @click="exportReport('csv')">导出 CSV</el-button>
      <el-button :icon="Download" text @click="exportReport('json')">导出 JSON</el-button>
    </div>

    <el-card v-if="steps.length" shadow="never">
      <template #header>工作流进度</template>
      <el-steps :active="steps.length" align-center>
        <el-step
          v-for="s in steps"
          :key="s.name"
          :title="s.name === 'extract' ? '要素抽取' : '规则比对'"
          :description="s.detail || s.status"
          :status="s.status === 'succeeded' ? 'success' : s.status === 'running' ? 'process' : 'wait'"
        />
      </el-steps>
    </el-card>

    <el-card v-if="run?.error" shadow="never">
      <el-alert :title="run.error" type="error" :closable="false" />
    </el-card>

    <div class="pane">
      <el-card shadow="never" class="block">
        <template #header>要素表（{{ elements.length }}）</template>
        <el-table :data="elements" empty-text="暂无要素">
          <el-table-column prop="label" label="要素" width="120" />
          <el-table-column prop="value" label="值" min-width="180" show-overflow-tooltip />
          <el-table-column prop="valueType" label="类型" width="90" />
          <el-table-column prop="source" label="来源" width="80" />
          <el-table-column label="置信度" width="90">
            <template #default="{ row }">{{ score(row.confidence) }}</template>
          </el-table-column>
          <el-table-column label="出处" width="120">
            <template #default="{ row }">
              <el-link v-if="row.seq !== null && row.seq !== undefined" type="primary" @click="openSource(row.seq)">
                片段#{{ row.seq }}
              </el-link>
              <span v-else>-</span>
            </template>
          </el-table-column>
        </el-table>
      </el-card>

      <el-card shadow="never" class="block">
        <template #header>不一致 / 风险清单（{{ findings.length }}）</template>
        <el-table :data="findings" empty-text="未发现不一致项">
          <el-table-column label="严重级" width="90">
            <template #default="{ row }">
              <el-tag :type="severityType[row.severity] ?? 'info'" effect="light">
                {{ severityText[row.severity] ?? row.severity }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="category" label="分类" width="110" />
          <el-table-column prop="title" label="结论" min-width="200" show-overflow-tooltip />
          <el-table-column prop="expected" label="期望" width="120" show-overflow-tooltip />
          <el-table-column prop="actual" label="实际" width="120" show-overflow-tooltip />
          <el-table-column label="出处" width="120">
            <template #default="{ row }">
              <el-link v-if="row.seq !== null && row.seq !== undefined" type="primary" @click="openSource(row.seq)">
                片段#{{ row.seq }}
              </el-link>
              <span v-else>-</span>
            </template>
          </el-table-column>
        </el-table>
      </el-card>
    </div>
  </div>
</template>

<style scoped>
.detail {
  max-width: 1100px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
  overflow-y: auto;
  height: 100%;
  padding-bottom: 16px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
}

.spacer {
  flex: 1;
}

.pane {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.block {
  flex: none;
}
</style>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Delete, Refresh, VideoPlay } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { knowledgeApi } from '@/api/knowledge'
import { reviewApi, type ReviewCapabilities, type ReviewRun } from '@/api/review'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const router = useRouter()

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const capabilities = ref<ReviewCapabilities | null>(null)
const documents = ref<{ id: string; fileName: string; status: string; chunkCount: number; indexed: boolean }[]>([])
const runs = ref<ReviewRun[]>([])
const loading = ref(false)
const submitting = ref(false)

const documentId = ref('')
const mode = ref('both')
const template = ref('买卖合同')

const availableDocuments = computed(() => documents.value.filter((d) => d.status === 'parsed' && d.chunkCount > 0))

const statusType: Record<string, string> = {
  pending: 'info',
  running: 'warning',
  succeeded: 'success',
  failed: 'danger',
}

const statusText: Record<string, string> = {
  pending: '排队中',
  running: '执行中',
  succeeded: '已完成',
  failed: '失败',
}

async function load() {
  loading.value = true
  try {
    const [caps, docs, list] = await Promise.all([reviewApi.health(), knowledgeApi.documents(), reviewApi.runs()])
    capabilities.value = caps
    documents.value = docs
    runs.value = list
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

async function startReview() {
  if (!documentId.value) {
    ElMessage.warning('请选择要审查的文档')
    return
  }
  submitting.value = true
  try {
    const { runId } = await reviewApi.create(documentId.value, mode.value, template.value || undefined)
    ElMessage.success('已创建审查任务')
    void router.push(`/review/${runId}`)
  } catch (e) {
    ElMessage.error('创建失败：' + (e as Error).message)
  } finally {
    submitting.value = false
  }
}

function openRun(row: ReviewRun) {
  void router.push(`/review/${row.id}`)
}

async function rerun(row: ReviewRun) {
  await reviewApi.rerun(row.id)
  ElMessage.success('已重新排队')
  await load()
}

async function remove(row: ReviewRun) {
  await ElMessageBox.confirm(`删除审查记录 #${row.id}？`, '确认', { type: 'warning' })
  await reviewApi.remove(row.id)
  await load()
}
</script>

<template>
  <div class="review" v-loading="loading">
    <el-card shadow="never">
      <template #header>
        <div class="header">
          <span>合同审查 · 要素抽取与比对</span>
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
      </template>
      <div class="tags" v-if="capabilities">
        <el-tag v-if="capabilities.extract" type="success" effect="plain">要素抽取</el-tag>
        <el-tag v-if="capabilities.compare" type="success" effect="plain">规则比对</el-tag>
        <el-tag :type="capabilities.llmExtract ? 'success' : 'info'" effect="plain">
          LLM 增强：{{ capabilities.llmExtract ? '已启用' : '未配置（仅规则抽取）' }}
        </el-tag>
      </div>

      <div class="form">
        <el-select v-model="documentId" placeholder="选择已解析文档" filterable style="width: 320px">
          <el-option v-for="d in availableDocuments" :key="d.id" :label="d.fileName" :value="d.id" />
        </el-select>
        <el-select v-model="mode" style="width: 160px">
          <el-option label="审合同（要素+比对）" value="both" />
          <el-option label="仅抽要素" value="extract" />
          <el-option label="仅比对" value="compare" />
        </el-select>
        <el-input v-model="template" placeholder="模板/规则分类（可空）" style="width: 200px" />
        <el-button type="primary" :icon="VideoPlay" :loading="submitting" @click="startReview">开始审查</el-button>
      </div>
    </el-card>

    <el-card shadow="never" class="block">
      <template #header>审查记录</template>
      <el-table :data="runs" empty-text="暂无审查记录">
        <el-table-column prop="id" label="#" width="70" />
        <el-table-column prop="fileName" label="文档" min-width="220" show-overflow-tooltip />
        <el-table-column prop="mode" label="模式" width="100" />
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag :type="statusType[row.status] ?? 'info'" effect="light">{{ statusText[row.status] ?? row.status }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="elementCount" label="要素" width="80" />
        <el-table-column prop="findingCount" label="清单" width="80" />
        <el-table-column prop="latencyMs" label="耗时(ms)" width="100" />
        <el-table-column prop="createdAt" label="时间" width="200" show-overflow-tooltip />
        <el-table-column label="操作" width="200">
          <template #default="{ row }">
            <el-button text type="primary" @click="openRun(row)">查看</el-button>
            <el-button text @click="rerun(row)">重跑</el-button>
            <el-button :icon="Delete" text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<style scoped>
.review {
  max-width: 1100px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
  overflow-y: auto;
  height: 100%;
  padding-bottom: 16px;
}

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;
}

.form {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
}

.block {
  flex: none;
}
</style>

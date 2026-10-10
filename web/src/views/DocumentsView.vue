<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Delete, Refresh, UploadFilled, View } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { documentsApi, type DocumentDto, type DocumentStatus } from '@/api/documents'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const router = useRouter()

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const documents = ref<DocumentDto[]>([])
const loading = ref(false)
let timer: number | undefined

const statusType: Record<DocumentStatus, 'success' | 'warning' | 'danger' | 'info'> = {
  pending: 'info',
  parsing: 'warning',
  parsed: 'success',
  failed: 'danger',
}
const statusText: Record<DocumentStatus, string> = {
  pending: '待解析',
  parsing: '解析中',
  parsed: '已解析',
  failed: '失败',
}

const hasInProgress = computed(() =>
  documents.value.some((d) => d.status === 'pending' || d.status === 'parsing'),
)

async function load() {
  loading.value = true
  try {
    documents.value = await documentsApi.list()
  } catch (e) {
    ElMessage.error('读取失败：' + (e as Error).message)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
  startHealthCheck()
  timer = window.setInterval(() => {
    if (hasInProgress.value) void load()
  }, 2000)
})

onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
})

async function beforeUpload(file: File): Promise<boolean> {
  if (!file.name.toLowerCase().endsWith('.pdf')) {
    ElMessage.error('仅支持 PDF（P0 文本层）')
    return false
  }
  try {
    await documentsApi.upload(file)
    ElMessage.success('上传成功，解析中…')
    await load()
  } catch (e) {
    ElMessage.error('上传失败：' + (e as Error).message)
  }
  return false
}

function openDetail(row: DocumentDto) {
  void router.push(`/documents/${row.id}`)
}

async function reparse(row: DocumentDto) {
  await documentsApi.reparse(row.id)
  ElMessage.success('已重新入队')
  await load()
}

async function remove(row: DocumentDto) {
  await ElMessageBox.confirm(`删除「${row.fileName}」？`, '确认', { type: 'warning' })
  await documentsApi.remove(row.id)
  ElMessage.success('已删除')
  await load()
}

function formatSize(bytes: number) {
  if (bytes < 1024) return bytes + ' B'
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
  return (bytes / 1024 / 1024).toFixed(1) + ' MB'
}
</script>

<template>
  <div class="documents">
    <el-card shadow="never">
      <template #header>
        <div class="header">
          <span>数据管道 · 文档</span>
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

      <el-upload
        drag
        :show-file-list="false"
        :before-upload="beforeUpload"
        accept="application/pdf"
        class="uploader"
      >
        <el-icon class="el-icon--upload"><UploadFilled /></el-icon>
        <div class="el-upload__text">拖拽 PDF 到此处，或<em>点击上传</em></div>
        <template #tip>
          <div class="el-upload__tip">仅 PDF 文本层；扫描件不在 P0 范围。</div>
        </template>
      </el-upload>

      <el-table v-loading="loading" :data="documents" class="table" empty-text="暂无文档">
        <el-table-column prop="fileName" label="文件名" min-width="220" show-overflow-tooltip />
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tooltip :disabled="!row.error" :content="row.error ?? ''" placement="top">
              <el-tag :type="statusType[row.status as DocumentStatus]" effect="light">
                {{ statusText[row.status as DocumentStatus] }}
              </el-tag>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column prop="pageCount" label="页数" width="80" />
        <el-table-column prop="chunkCount" label="块数" width="80" />
        <el-table-column label="大小" width="100">
          <template #default="{ row }">{{ formatSize(row.sizeBytes) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="220">
          <template #default="{ row }">
            <el-button :icon="View" text type="primary" @click="openDetail(row)">预览</el-button>
            <el-button :icon="Refresh" text @click="reparse(row)">重解析</el-button>
            <el-button :icon="Delete" text type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<style scoped>
.documents {
  max-width: 1100px;
  margin: 0 auto;
}

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.uploader {
  margin-bottom: 16px;
}

.table {
  width: 100%;
}
</style>

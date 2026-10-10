<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { Delete, Edit, Plus, Refresh, Setting, Connection } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  settingsApi,
  type Provider,
  type ProviderModel,
  type SettingsPreview,
} from '@/api/settings'
import { useBackendHealth } from '@/composables/useBackendHealth'
import BackendStatus from '@/components/BackendStatus.vue'

const { health: backendHealth, start: startHealthCheck } = useBackendHealth({
  intervalMs: 10000,
  immediate: true,
})

const loading = ref(false)
const preview = ref<SettingsPreview | null>(null)
const providers = ref<Provider[]>([])

const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const saving = ref(false)
const form = reactive({ name: '', baseUrl: '', apiKey: '' })

const modelsDialogVisible = ref(false)
const currentProvider = ref<Provider | null>(null)
const newModel = ref('')
const fetchedModels = ref<string[]>([])
const fetching = ref(false)

async function load() {
  loading.value = true
  try {
    const [p, list] = await Promise.all([settingsApi.preview(), settingsApi.listProviders()])
    preview.value = p
    providers.value = list
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

function openCreate() {
  editingId.value = null
  form.name = ''
  form.baseUrl = 'https://api.siliconflow.cn/v1'
  form.apiKey = ''
  dialogVisible.value = true
}

function openEdit(p: Provider) {
  editingId.value = p.id
  form.name = p.name
  form.baseUrl = p.baseUrl
  form.apiKey = ''
  dialogVisible.value = true
}

async function saveProvider() {
  if (!form.baseUrl.trim()) {
    ElMessage.warning('请填写 BaseUrl')
    return
  }
  saving.value = true
  try {
    if (editingId.value === null) {
      await settingsApi.createProvider({ name: form.name, baseUrl: form.baseUrl, apiKey: form.apiKey })
      ElMessage.success('已新增供应商')
    } else {
      await settingsApi.updateProvider(editingId.value, {
        name: form.name,
        baseUrl: form.baseUrl,
        apiKey: form.apiKey,
      })
      ElMessage.success('已保存')
    }
    dialogVisible.value = false
    await load()
  } catch (e) {
    ElMessage.error('保存失败：' + (e as Error).message)
  } finally {
    saving.value = false
  }
}

async function removeProvider(p: Provider) {
  await ElMessageBox.confirm(`删除供应商「${p.name}」及其全部模型？`, '确认', { type: 'warning' })
  await settingsApi.deleteProvider(p.id)
  ElMessage.success('已删除')
  await load()
}

async function activate(p: Provider) {
  await settingsApi.activateProvider(p.id)
  ElMessage.success(`已切换到「${p.name}」`)
  await load()
}

async function test(p: Provider) {
  try {
    const r = await settingsApi.testProvider(p.id)
    if (r.ok) ElMessage.success(`连接成功：${r.count ?? 0} 个模型，${r.latencyMs}ms`)
    else ElMessage.error(`连接失败：${r.error ?? '未知错误'}`)
  } catch (e) {
    ElMessage.error('测试失败：' + (e as Error).message)
  }
}

function openModels(p: Provider) {
  currentProvider.value = p
  newModel.value = ''
  fetchedModels.value = []
  modelsDialogVisible.value = true
}

async function refreshCurrentProvider() {
  const id = currentProvider.value?.id
  if (id === undefined) return
  currentProvider.value = providers.value.find((x) => x.id === id) ?? null
}

async function addModel(build = false) {
  const p = currentProvider.value
  const name = newModel.value.trim()
  if (!p || !name) return
  try {
    await settingsApi.addModel(p.id, name)
    newModel.value = ''
    await load()
    await refreshCurrentProvider()
    if (!build) ElMessage.success('已添加模型')
  } catch (e) {
    ElMessage.error('添加失败：' + (e as Error).message)
  }
}

async function addFetched(model: string) {
  const p = currentProvider.value
  if (!p) return
  if (p.models.some((x) => x.model === model)) {
    ElMessage.info('该模型已在列表中')
    return
  }
  newModel.value = model
  await addModel(true)
}

async function removeModel(m: ProviderModel) {
  await settingsApi.deleteModel(m.id)
  await load()
  await refreshCurrentProvider()
}

async function makeDefault(m: ProviderModel) {
  const p = currentProvider.value
  if (!p) return
  await settingsApi.setDefaultModel(p.id, m.id)
  ElMessage.success(`当前模型：${m.model}`)
  await load()
  await refreshCurrentProvider()
}

async function fetchForCurrent() {
  const p = currentProvider.value
  if (!p) return
  fetching.value = true
  try {
    const r = await settingsApi.fetchModelsForProvider(p.id)
    fetchedModels.value = r.models
    if (r.models.length === 0) ElMessage.info('该端点未返回模型列表')
  } catch (e) {
    ElMessage.error('拉取失败：' + (e as Error).message)
  } finally {
    fetching.value = false
  }
}
</script>

<template>
  <div class="settings" v-loading="loading">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>当前模型</span>
          <el-tag v-if="preview" :type="preview.configured ? 'success' : 'warning'" effect="light">
            {{ preview.configured ? '已配置' : '未配置' }}
          </el-tag>
        </div>
      </template>

      <el-alert
        v-if="preview && !preview.configured"
        type="warning"
        :closable="false"
        show-icon
        title="尚未配置可用模型"
        description="在下方新增一个供应商并添加模型后即可对话；无需重启服务。"
        class="mb"
      />

      <el-descriptions v-if="preview" :column="2" border>
        <el-descriptions-item label="对话 BaseUrl">{{ preview.baseUrl }}</el-descriptions-item>
        <el-descriptions-item label="对话模型">{{ preview.model }}</el-descriptions-item>
        <el-descriptions-item label="Embedding 模型">{{ preview.embeddingModel || '（未配置）' }}</el-descriptions-item>
        <el-descriptions-item label="温度">{{ preview.temperature }}</el-descriptions-item>
      </el-descriptions>
    </el-card>

    <el-card shadow="never" class="block">
      <template #header>
        <div class="card-header">
          <span>模型供应商</span>
          <BackendStatus
            :status="backendHealth.status"
            :last-check="backendHealth.lastCheck"
            :error="backendHealth.error"
            :model-configured="backendHealth.modelConfigured"
            :show-last-check="true"
            size="small"
          />
          <div>
            <el-button :icon="Refresh" text @click="load">刷新</el-button>
            <el-button type="primary" :icon="Plus" @click="openCreate">新增供应商</el-button>
          </div>
        </div>
      </template>

      <el-table :data="providers" empty-text="暂无供应商">
        <el-table-column prop="name" label="名称" width="160" show-overflow-tooltip />
        <el-table-column prop="baseUrl" label="BaseUrl" min-width="220" show-overflow-tooltip />
        <el-table-column label="API Key" width="120">
          <template #default="{ row }">
            <span class="mono">{{ row.hasApiKey ? row.apiKeyMasked : '未设置' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="模型" min-width="220">
          <template #default="{ row }">
            <div class="model-tags">
              <el-tag
                v-for="m in row.models"
                :key="m.id"
                size="small"
                :type="m.isDefault ? 'success' : 'info'"
                :effect="m.isDefault ? 'dark' : 'plain'"
              >
                {{ m.model }}
              </el-tag>
              <span v-if="!row.models.length" class="muted">（无模型）</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag v-if="row.isActive" type="success" effect="light">当前</el-tag>
            <el-button v-else text type="primary" @click="activate(row)">启用</el-button>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="300">
          <template #default="{ row }">
            <el-button text :icon="Setting" type="primary" @click="openModels(row)">模型</el-button>
            <el-button text :icon="Connection" @click="test(row)">测试</el-button>
            <el-button text :icon="Edit" @click="openEdit(row)">编辑</el-button>
            <el-button text :icon="Delete" type="danger" @click="removeProvider(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-dialog
      v-model="dialogVisible"
      :title="editingId === null ? '新增供应商' : '编辑供应商'"
      width="520"
    >
      <el-form label-width="90px" @submit.prevent>
        <el-form-item label="名称">
          <el-input v-model="form.name" placeholder="如：SiliconFlow" />
        </el-form-item>
        <el-form-item label="BaseUrl" required>
          <el-input v-model="form.baseUrl" placeholder="https://api.siliconflow.cn/v1" />
        </el-form-item>
        <el-form-item label="API Key">
          <el-input
            v-model="form.apiKey"
            type="password"
            show-password
            :placeholder="editingId === null ? 'sk-...' : '留空则保持不变'"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="saveProvider">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="modelsDialogVisible"
      :title="`管理模型 · ${currentProvider?.name ?? ''}`"
      width="620"
    >
      <div class="model-add">
        <el-input
          v-model="newModel"
          placeholder="模型名，如 deepseek-ai/DeepSeek-V4-Flash"
          @keyup.enter="addModel(false)"
        />
        <el-button type="primary" @click="addModel(false)">添加</el-button>
        <el-button :loading="fetching" @click="fetchForCurrent">拉取</el-button>
      </div>

      <div v-if="fetchedModels.length" class="fetched">
        <span class="muted">点击添加到列表：</span>
        <el-tag
          v-for="m in fetchedModels"
          :key="m"
          class="clickable"
          size="small"
          @click="addFetched(m)"
        >
          {{ m }}
        </el-tag>
      </div>

      <el-table :data="currentProvider?.models ?? []" size="small" empty-text="暂无模型">
        <el-table-column prop="model" label="模型" min-width="240" show-overflow-tooltip />
        <el-table-column label="当前" width="80">
          <template #default="{ row }">
            <el-tag v-if="row.isDefault" type="success" effect="light">当前</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="160">
          <template #default="{ row }">
            <el-button text type="primary" :disabled="row.isDefault" @click="makeDefault(row)">
              设为当前
            </el-button>
            <el-button text type="danger" @click="removeModel(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-dialog>
  </div>
</template>

<style scoped>
.settings {
  max-width: 1000px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.block {
  flex: none;
}

.mb {
  margin-bottom: 16px;
}

.model-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.mono {
  font-family: Consolas, monospace;
  font-size: 12px;
}

.muted {
  color: #909399;
  font-size: 12px;
}

.model-add {
  display: flex;
  gap: 10px;
  margin-bottom: 12px;
}

.fetched {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
  margin-bottom: 12px;
  max-height: 120px;
  overflow-y: auto;
}

.clickable {
  cursor: pointer;
}
</style>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { Connection, Delete, Edit, Plus, Refresh, Setting } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import BackendStatus from '@/components/BackendStatus.vue'
import { useAgentStore } from '@/stores/agent'
import { useHealthStore } from '@/stores/health'
import {
  settingsApi,
  type Provider,
  type ProviderModel,
  type SettingsPreview,
} from '@/api/settings'

const agent = useAgentStore()
const health = useHealthStore()

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

/** 供应商列表以 store 为单一数据源（对话页共用），改完即同步。 */
async function reloadProviders(): Promise<void> {
  providers.value = await agent.loadProviders()
}

async function load(): Promise<void> {
  loading.value = true
  try {
    const [p] = await Promise.all([settingsApi.preview(), reloadProviders()])
    preview.value = p
  } catch (e) {
    ElMessage.error('加载失败：' + (e as Error).message)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
})

function openCreate(): void {
  editingId.value = null
  form.name = ''
  form.baseUrl = 'https://api.siliconflow.cn/v1'
  form.apiKey = ''
  dialogVisible.value = true
}

function openEdit(p: Provider): void {
  editingId.value = p.id
  form.name = p.name
  form.baseUrl = p.baseUrl
  form.apiKey = ''
  dialogVisible.value = true
}

async function saveProvider(): Promise<void> {
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
    await Promise.all([load()])
  } catch (e) {
    ElMessage.error('保存失败：' + (e as Error).message)
  } finally {
    saving.value = false
  }
}

async function removeProvider(p: Provider): Promise<void> {
  await ElMessageBox.confirm(`删除供应商「${p.name}」及其全部模型？`, '确认', { type: 'warning' })
  await settingsApi.deleteProvider(p.id)
  ElMessage.success('已删除')
  await load()
}

async function activate(p: Provider): Promise<void> {
  await settingsApi.activateProvider(p.id)
  ElMessage.success(`已切换到「${p.name}」`)
  await load()
}

async function test(p: Provider): Promise<void> {
  try {
    const r = await settingsApi.testProvider(p.id)
    if (r.ok) ElMessage.success(`连接成功：${r.count ?? 0} 个模型，${r.latencyMs}ms`)
    else ElMessage.error(`连接失败：${r.error ?? '未知错误'}`)
  } catch (e) {
    ElMessage.error('测试失败：' + (e as Error).message)
  }
}

function openModels(p: Provider): void {
  currentProvider.value = p
  newModel.value = ''
  fetchedModels.value = []
  modelsDialogVisible.value = true
}

async function refreshCurrentProvider(): Promise<void> {
  const id = currentProvider.value?.id
  if (id === undefined) return
  currentProvider.value = providers.value.find((x) => x.id === id) ?? null
}

async function addModel(build = false): Promise<void> {
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

async function addFetched(model: string): Promise<void> {
  const p = currentProvider.value
  if (!p) return
  if (p.models.some((x) => x.model === model)) {
    ElMessage.info('该模型已在列表中')
    return
  }
  newModel.value = model
  await addModel(true)
}

async function removeModel(m: ProviderModel): Promise<void> {
  await settingsApi.deleteModel(m.id)
  await load()
  await refreshCurrentProvider()
}

async function makeDefault(m: ProviderModel): Promise<void> {
  const p = currentProvider.value
  if (!p) return
  await settingsApi.setDefaultModel(p.id, m.id)
  ElMessage.success(`当前模型：${m.model}`)
  await load()
  await refreshCurrentProvider()
}

async function fetchForCurrent(): Promise<void> {
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
  <div class="settings-page" v-loading="loading">
    <header class="page-header">
      <div class="page-title">
        <h1>设置</h1>
        <p>管理模型供应商与当前模型。修改即时生效，无需重启服务。</p>
      </div>
      <BackendStatus
        :status="health.health.status"
        :last-check="health.health.lastCheck"
        :error="health.health.error"
        :model-configured="health.health.modelConfigured"
        show-last-check
      />
    </header>

    <!-- 资料库：部署期准备动作，不是日常功能，所以入口在这里而不是一级导航。
         检索本身是工具，由模型在需要依据时自己调用；用户不需要「进入问答模式」。 -->
    <section class="panel">
      <div class="panel-head docs-head">
        <div>
          <h2>资料库</h2>
          <p>上传 PDF / 文本，供模型在需要「依据哪条规则」时检索。这是部署时的准备工作，平时不用打开。</p>
        </div>
        <RouterLink to="/documents">
          <el-button round>管理资料</el-button>
        </RouterLink>
      </div>
    </section>

    <!-- 当前模型 -->
    <section class="panel">
      <div class="panel-head">
        <h2>当前模型</h2>
        <el-tag v-if="preview" :type="preview.configured ? 'success' : 'warning'" effect="light" round>
          {{ preview.configured ? '已配置' : '未配置' }}
        </el-tag>
      </div>

      <el-alert
        v-if="preview && !preview.configured"
        type="warning"
        :closable="false"
        show-icon
        title="尚未配置可用模型"
        description="在下方新增一个供应商并添加模型后即可对话；无需重启服务。"
      />

      <div v-if="preview" class="preview-grid">
        <div class="preview-item">
          <span class="k">对话 BaseUrl</span>
          <span class="v" :title="preview.baseUrl">{{ preview.baseUrl }}</span>
        </div>
        <div class="preview-item">
          <span class="k">对话模型</span>
          <span class="v" :title="preview.model">{{ preview.model }}</span>
        </div>
        <div class="preview-item">
          <span class="k">Embedding 模型</span>
          <span class="v">{{ preview.embeddingModel || '（未配置）' }}</span>
        </div>
        <div class="preview-item">
          <span class="k">温度</span>
          <span class="v">{{ preview.temperature ?? '不适用' }}</span>
        </div>
      </div>
    </section>

    <!-- 供应商 -->
    <section class="panel">
      <div class="panel-head">
        <h2>模型供应商</h2>
        <div class="panel-actions">
          <el-button :icon="Refresh" text round @click="load">刷新</el-button>
          <el-button type="primary" round :icon="Plus" @click="openCreate">新增供应商</el-button>
        </div>
      </div>

      <div class="table-wrap">
        <el-table :data="providers" empty-text="暂无供应商">
          <el-table-column prop="name" label="名称" width="160" show-overflow-tooltip />
          <el-table-column prop="baseUrl" label="BaseUrl" min-width="220" show-overflow-tooltip />
          <el-table-column label="API Key" width="130">
            <template #default="{ row }">
              <span class="mono">{{ row.hasApiKey ? '已保存' : '未设置' }}</span>
            </template>
          </el-table-column>
          <el-table-column label="模型" min-width="230">
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
          <el-table-column label="状态" width="100">
            <template #default="{ row }">
              <el-tag v-if="row.isActive" type="success" effect="light" round>当前</el-tag>
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
      </div>
    </section>

    <!-- 新增 / 编辑供应商 -->
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
        <el-button round @click="dialogVisible = false">取消</el-button>
        <el-button round type="primary" :loading="saving" @click="saveProvider">保存</el-button>
      </template>
    </el-dialog>

    <!-- 模型管理 -->
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
        <el-button round type="primary" @click="addModel(false)">添加</el-button>
        <el-button round :loading="fetching" @click="fetchForCurrent">拉取</el-button>
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
        <el-table-column label="当前" width="90">
          <template #default="{ row }">
            <el-tag v-if="row.isDefault" type="success" effect="light" round>当前</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="170">
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
.settings-page {
  height: 100%;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 16px;
  max-width: 1060px;
  margin: 0 auto;
  padding: 4px 2px 24px;
}

/* ---------- 页头 ---------- */
.page-header {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}

.page-title h1 {
  margin: 0;
  font-size: 22px;
  font-weight: 700;
  letter-spacing: 0.01em;
}

.page-title p {
  margin: 5px 0 0;
  font-size: 13px;
  color: var(--aw-text-2);
}

/* ---------- 面板 ---------- */
.panel {
  background: var(--aw-surface);
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-l);
  box-shadow: var(--aw-shadow-1);
  padding: 18px 20px;
}

.panel-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 14px;
}

.panel-head h2 {
  margin: 0;
  font-size: 15.5px;
  font-weight: 650;
}

/* 资料库那一格：副标题只在这一处出现，别为它引入通用类 */
.docs-head {
  align-items: flex-start;
  margin-bottom: 0;
}

.docs-head p {
  margin: 5px 0 0;
  max-width: 46em;
  color: var(--aw-text-3);
  font-size: 12.5px;
  line-height: 1.55;
}

.panel-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* ---------- 当前模型预览 ---------- */
.preview-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
  margin-top: 14px;
}

.preview-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 12px 14px;
  background: var(--aw-surface-2);
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  min-width: 0;
}

.preview-item .k {
  font-size: 12px;
  color: var(--aw-text-3);
}

.preview-item .v {
  font-size: 13.5px;
  font-weight: 550;
  color: var(--aw-text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ---------- 表格 ---------- */
.table-wrap {
  border-radius: var(--aw-radius-m);
  overflow: hidden;
  border: 1px solid var(--aw-border);
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
  color: var(--aw-text-3);
  font-size: 12px;
}

/* ---------- 模型管理弹窗 ---------- */
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

@media (max-width: 640px) {
  .page-header {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>

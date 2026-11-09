<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  CircleCloseFilled,
  Delete,
  Document as FileIcon,
  Download,
  Grid,
  Menu as ListIcon,
  Plus,
  RefreshRight,
  Search,
  UploadFilled,
} from '@element-plus/icons-vue'
import {
  ACCEPT_EXTS,
  MAX_FILE_BYTES,
  type DocumentChunkDto,
  type DocumentDto,
  type DocumentPageDto,
  fetchChunks,
  fetchPages,
  listDocuments,
  removeDocument,
  reparseDocument,
  uploadDocument,
} from '@/api/documents'
import {
  createKnowledgeBase,
  listKnowledgeBases,
  type KnowledgeBaseDto,
} from '@/api/knowledgeBases'
import { cachedUser } from '@/api/auth'

/* ---------------- 状态 ---------------- */

const docs = ref<DocumentDto[]>([])
const loading = ref(false)
const keyword = ref('')
const viewMode = ref<'grid' | 'list'>('list')
const statusFilter = ref<string>('')
const typeFilter = ref<string>('')

// 删除是管理员的动作（ADR 0008）。这里只决定「看不看得见按钮」——
// 后端仍然会判一次，因为前端拿到的 role 可能是几分钟前的缓存。
const isAdmin = ref(false)
const myLevelRank = ref(1)

const knowledgeBases = ref<KnowledgeBaseDto[]>([])
const targetKbId = ref('')
const kbFilter = ref('')
const kbDialogOpen = ref(false)
const kbSaving = ref(false)
const kbForm = reactive<{
  name: string
  visibility: 'public' | 'private'
  minLevelRank: number
}>({ name: '', visibility: 'private', minLevelRank: 1 })

const uploads = ref<{ key: number; name: string; percent: number; abort: () => void; error: string }[]>([])
let uploadKey = 0
const fileInput = ref<HTMLInputElement | null>(null)

const drawer = ref(false)
const current = ref<DocumentDto | null>(null)
const pages = ref<DocumentPageDto[] | null>(null)
const chunks = ref<DocumentChunkDto[] | null>(null)
const detailTab = ref<'pages' | 'chunks'>('pages')
const detailLoading = ref(false)

let pollTimer: number | null = null

/* ---------------- 派生 ---------------- */

function extOf(name: string): string {
  const i = name.lastIndexOf('.')
  return i >= 0 ? name.slice(i).toLowerCase() : ''
}

const accessibleKbs = computed(() =>
  knowledgeBases.value.filter(
    (kb) => kb.visibility === 'public' || (kb.minLevelRank ?? 1) <= myLevelRank.value,
  ),
)

function kbName(id: string | null | undefined): string {
  if (!id) return '未归属'
  return knowledgeBases.value.find((kb) => kb.id === id)?.name ?? '知识库'
}

const filtered = computed(() =>
  docs.value.filter((d) => {
    if (statusFilter.value && d.status !== statusFilter.value) return false
    if (typeFilter.value && extOf(d.fileName) !== typeFilter.value) return false
    if (kbFilter.value && d.kbId !== kbFilter.value) return false
    if (keyword.value && !d.fileName.toLowerCase().includes(keyword.value.toLowerCase())) return false
    return true
  }),
)

const STATUS_LABEL: Record<string, string> = {
  pending: '排队中',
  parsing: '解析中',
  parsed: '可检索',
  failed: '失败',
}

/**
 * 「已解析」不等于「语义可检索」：向量没建成的文档仍能按字面命中，
 * 但按意思找不到。这类状态必须与完整的「可检索」在视觉上分开，
 * 否则残缺数据和健康数据在界面上长得一模一样。
 */
function isVectorSparse(d: DocumentDto): boolean {
  return d.status === 'parsed' && (d.indexState === 'text_only' || d.indexState === 'partial')
}

function statusClass(d: DocumentDto): string {
  return isVectorSparse(d) ? 'text-only' : d.status
}

function statusText(d: DocumentDto): string {
  if (d.status === 'parsed') {
    if (d.indexState === 'partial') return '部分向量'
    if (d.indexState === 'text_only') return '仅全文'
  }
  return STATUS_LABEL[d.status] ?? d.status
}

function statusHint(d: DocumentDto): string {
  if (d.status === 'failed') return d.error ?? ''
  if (d.indexState === 'text_only')
    return '这篇只建了全文索引：按关键词能搜到，按意思搜不到。向量服务恢复后点「重新解析」可补齐。'
  if (d.indexState === 'partial') return '部分分块缺少向量，重新解析可补齐。'
  return ''
}

/* ---------------- 数据与轮询 ---------------- */

async function refresh(silent = false): Promise<void> {
  if (!silent) loading.value = true
  try {
    docs.value = await listDocuments()
  } catch (e) {
    if (!silent) ElMessage.error((e as Error).message)
  } finally {
    loading.value = false
  }
  schedulePoll()
}

function schedulePoll(): void {
  if (pollTimer !== null) {
    window.clearTimeout(pollTimer)
    pollTimer = null
  }
  const busy = docs.value.some((d) => d.status === 'pending' || d.status === 'parsing')
  if (busy) pollTimer = window.setTimeout(() => void refresh(true), 3000)
}

async function loadKnowledgeBases(): Promise<void> {
  try {
    knowledgeBases.value = await listKnowledgeBases()
  } catch (e) {
    ElMessage.error(`加载知识库失败：${(e as Error).message}`)
    return
  }
  if (!accessibleKbs.value.some((kb) => kb.id === targetKbId.value)) {
    targetKbId.value =
      accessibleKbs.value.find((kb) => kb.isDefault)?.id ?? accessibleKbs.value[0]?.id ?? ''
  }
}

onMounted(() => {
  void refresh()
  void cachedUser().then(async (user) => {
    isAdmin.value = user?.role === 'owner' || user?.role === 'admin'
    myLevelRank.value = user?.levelRank ?? 1
    await loadKnowledgeBases()
  })
})
onBeforeUnmount(() => {
  if (pollTimer !== null) window.clearTimeout(pollTimer)
})

/* ---------------- 上传 ---------------- */

function pickFiles(): void {
  fileInput.value?.click()
}

function onFilesChosen(ev: Event): void {
  const input = ev.target as HTMLInputElement
  if (input.files) void startUploads(Array.from(input.files))
  input.value = ''
}

function onDrop(ev: DragEvent): void {
  const files = ev.dataTransfer?.files
  if (files) void startUploads(Array.from(files))
}

async function startUploads(files: File[]): Promise<void> {
  if (!isAdmin.value) {
    ElMessage.warning('只有团队管理员可以新增文档')
    return
  }
  if (!targetKbId.value) {
    ElMessage.warning('请先选择目标知识库')
    return
  }
  for (const file of files) {
    const ext = extOf(file.name)
    if (!ACCEPT_EXTS.includes(ext)) {
      ElMessage.warning(`「${file.name}」不支持，仅支持 PDF / TXT / MD / JSON`)
      continue
    }
    if (file.size > MAX_FILE_BYTES) {
      ElMessage.warning(`「${file.name}」超过 30 MB`)
      continue
    }
    const item = { key: ++uploadKey, name: file.name, percent: 0, abort: (): void => {}, error: '' }
    uploads.value.push(item)
    const task = uploadDocument(file, (p) => (item.percent = p), targetKbId.value)
    item.abort = task.abort
    try {
      await task.promise
      item.percent = 100
    } catch (e) {
      item.error = (e as Error).message
    }
  }
  await refresh(true)
  // 完成的条目保留 2 秒再清掉，让用户看见"传完了"
  window.setTimeout(() => {
    uploads.value = uploads.value.filter((u) => u.error !== '' || u.percent < 100)
  }, 2000)
}

function cancelUpload(key: number): void {
  const item = uploads.value.find((u) => u.key === key)
  item?.abort()
  uploads.value = uploads.value.filter((u) => u.key !== key)
}

/* ---------------- 明细与操作 ---------------- */

async function openDetail(doc: DocumentDto): Promise<void> {
  current.value = doc
  detailTab.value = 'pages'
  pages.value = null
  chunks.value = null
  drawer.value = true
  detailLoading.value = true
  try {
    const [p, c] = await Promise.all([fetchPages(doc.id), fetchChunks(doc.id)])
    pages.value = p
    chunks.value = c
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    detailLoading.value = false
  }
}

async function onReparse(doc: DocumentDto): Promise<void> {
  if (!isAdmin.value) {
    ElMessage.warning('只有团队管理员可以重新解析文档')
    return
  }
  try {
    await reparseDocument(doc.id)
    ElMessage.success('已重新排队解析')
    drawer.value = false
    await refresh(true)
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

async function onDelete(doc: DocumentDto): Promise<void> {
  try {
    await ElMessageBox.confirm(`删除「${doc.fileName}」？删除后将无法再检索到它的内容。`, '确认删除', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }
  try {
    await removeDocument(doc.id)
    ElMessage.success('已删除')
    drawer.value = false
    await refresh(true)
  } catch (e) {
    ElMessage.error((e as Error).message)
  }
}

/* ---------------- 知识库 ---------------- */

function openKbDialog(): void {
  kbForm.name = ''
  kbForm.visibility = 'private'
  kbForm.minLevelRank = 1
  kbDialogOpen.value = true
}

async function saveKnowledgeBase(): Promise<void> {
  const name = kbForm.name.trim()
  if (!name) {
    ElMessage.warning('请填写知识库名称')
    return
  }
  if (
    kbForm.visibility === 'private' &&
    (kbForm.minLevelRank < 1 || kbForm.minLevelRank > myLevelRank.value)
  ) {
    ElMessage.warning(`私人库门槛必须在 L1~L${myLevelRank.value} 之间`)
    return
  }
  kbSaving.value = true
  try {
    const created = await createKnowledgeBase({
      name,
      visibility: kbForm.visibility,
      minLevelRank: kbForm.visibility === 'private' ? kbForm.minLevelRank : null,
    })
    kbDialogOpen.value = false
    await loadKnowledgeBases()
    targetKbId.value = created.id
    ElMessage.success(`已创建知识库「${created.name}」`)
  } catch (e) {
    ElMessage.error(`创建知识库失败：${(e as Error).message}`)
  } finally {
    kbSaving.value = false
  }
}

/* ---------------- 展示辅助 ---------------- */

function fmtSize(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}

function fmtTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return `${d.getMonth() + 1}/${d.getDate()} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const EXT_COLOR: Record<string, string> = {
  '.pdf': '#e0574b',
  '.txt': '#e8b64c',
  '.md': '#4f7ce8',
  '.json': '#9b6ce8',
}

function extColor(name: string): string {
  return EXT_COLOR[extOf(name)] ?? '#8b9391'
}

const typeOptions = computed(() => {
  const set = new Set(docs.value.map((d) => extOf(d.fileName)).filter(Boolean))
  return Array.from(set).sort()
})
</script>

<template>
  <div class="doc-page">
    <!-- ============ 左：文档列表 ============ -->
    <section v-loading="loading" class="doc-card">
      <header class="doc-topbar">
        <div class="search-box">
          <el-icon class="search-icon"><Search /></el-icon>
          <input v-model="keyword" placeholder="搜索文件名" />
        </div>
        <span class="view-label">视图</span>
        <div class="view-toggle">
          <button :class="{ on: viewMode === 'grid' }" title="网格" @click="viewMode = 'grid'">
            <el-icon><Grid /></el-icon>
          </button>
          <button :class="{ on: viewMode === 'list' }" title="列表" @click="viewMode = 'list'">
            <el-icon><ListIcon /></el-icon>
          </button>
        </div>
        <button class="ghost-icon" title="刷新" @click="refresh()">
          <el-icon><RefreshRight /></el-icon>
        </button>
      </header>

      <div class="title-row">
        <h1 class="doc-title">资料</h1>
        <el-select v-model="statusFilter" placeholder="全部状态" clearable class="chip-select">
          <el-option label="全部状态" value="" />
          <el-option label="排队中" value="pending" />
          <el-option label="解析中" value="parsing" />
          <el-option label="可检索" value="parsed" />
          <el-option label="失败" value="failed" />
        </el-select>
        <el-select v-model="typeFilter" placeholder="类型" clearable class="chip-select">
          <el-option label="全部类型" value="" />
          <el-option v-for="t in typeOptions" :key="t" :label="t.slice(1).toUpperCase()" :value="t" />
        </el-select>
        <el-select
          v-if="accessibleKbs.length > 1"
          v-model="kbFilter"
          placeholder="知识库"
          clearable
          class="chip-select"
        >
          <el-option label="全部知识库" value="" />
          <el-option v-for="kb in accessibleKbs" :key="kb.id" :label="kb.name" :value="kb.id" />
        </el-select>
      </div>

      <!-- 列表视图 -->
      <div v-if="viewMode === 'list'" class="table">
        <div class="thead">
          <span>文件名</span>
          <span>大小</span>
          <span>状态</span>
          <span>片段</span>
          <span>时间</span>
          <span />
        </div>
        <div
          v-for="d in filtered"
          :key="d.id"
          class="trow"
          role="button"
          tabindex="0"
          @click="openDetail(d)"
          @keydown.enter="openDetail(d)"
        >
          <span class="cell-name">
            <span class="file-badge" :style="{ background: extColor(d.fileName) }">
              {{ extOf(d.fileName).slice(1).toUpperCase() || 'FILE' }}
            </span>
            <span class="name-copy">
              <span class="name-text" :title="d.fileName">{{ d.fileName }}</span>
              <span class="kb-name">{{ kbName(d.kbId) }}</span>
            </span>
          </span>
          <span class="cell-dim">{{ fmtSize(d.sizeBytes) }}</span>
          <span>
            <el-tooltip :disabled="!statusHint(d)" :content="statusHint(d)" placement="top">
              <span class="status" :class="statusClass(d)"
                >{{ statusText(d) }}<i v-if="d.status === 'parsing'" class="dot"
              /></span>
            </el-tooltip>
          </span>
          <span class="cell-dim">{{ d.chunkCount ?? '—' }}</span>
          <span class="cell-dim">{{ fmtTime(d.createdAt) }}</span>
          <span class="row-actions" @click.stop>
            <button v-if="isAdmin" class="row-icon" title="重新解析" @click="onReparse(d)">
              <el-icon><RefreshRight /></el-icon>
            </button>
            <button v-if="isAdmin" class="row-icon danger" title="删除" @click="onDelete(d)">
              <el-icon><Delete /></el-icon>
            </button>
          </span>
        </div>
        <div v-if="filtered.length === 0" class="empty">
          <el-icon><FileIcon /></el-icon>
          <p>{{ docs.length === 0 ? '还没有资料，从右侧传一份文件开始' : '没有符合筛选条件的资料' }}</p>
        </div>
      </div>

      <!-- 网格视图 -->
      <div v-else class="grid">
        <div
          v-for="d in filtered"
          :key="d.id"
          class="gcard"
          role="button"
          tabindex="0"
          @click="openDetail(d)"
          @keydown.enter="openDetail(d)"
        >
          <span class="file-badge big" :style="{ background: extColor(d.fileName) }">
            {{ extOf(d.fileName).slice(1).toUpperCase() || 'FILE' }}
          </span>
          <span class="gname" :title="d.fileName">{{ d.fileName }}</span>
          <span class="kb-name">{{ kbName(d.kbId) }}</span>
          <span class="status" :class="statusClass(d)"
            >{{ statusText(d) }}<i v-if="d.status === 'parsing'" class="dot"
          /></span>
          <span class="gmeta">{{ fmtSize(d.sizeBytes) }} · {{ fmtTime(d.createdAt) }}</span>
          <button
            v-if="isAdmin"
            class="row-icon danger gdel"
            title="删除"
            @click.stop="onDelete(d)"
          >
            <el-icon><Delete /></el-icon>
          </button>
        </div>
        <div v-if="filtered.length === 0" class="empty">
          <el-icon><FileIcon /></el-icon>
          <p>{{ docs.length === 0 ? '还没有资料，从右侧传一份文件开始' : '没有符合筛选条件的资料' }}</p>
        </div>
      </div>
    </section>

    <!-- ============ 右：新增文档面板 ============ -->
    <aside class="upload-panel">
      <div class="panel-head">
        <div>
          <h2 class="panel-title">新增文档</h2>
          <p class="panel-sub">文档继承目标知识库的可见性</p>
        </div>
        <button v-if="isAdmin" class="link-btn" @click="openKbDialog">
          <el-icon><Plus /></el-icon>新建知识库
        </button>
      </div>

      <template v-if="isAdmin">
        <label class="field-label" for="target-kb">上传到知识库</label>
        <el-select id="target-kb" v-model="targetKbId" placeholder="选择知识库" class="kb-select">
          <el-option
            v-for="kb in accessibleKbs"
            :key="kb.id"
            :label="`${kb.name}${kb.visibility === 'private' ? ` · L${kb.minLevelRank ?? 1}+` : ''}`"
            :value="kb.id"
          />
        </el-select>
        <p v-if="accessibleKbs.length === 0" class="empty-hint">当前没有可上传的知识库。</p>

        <div
          class="dropzone"
          role="button"
          tabindex="0"
          @click="pickFiles"
          @keydown.enter="pickFiles"
          @dragover.prevent
          @drop.prevent="onDrop"
        >
          <el-icon class="dz-icon"><UploadFilled /></el-icon>
          <p class="dz-text">拖拽文件到这里，或点击选择</p>
          <button class="btn-dark" @click.stop="pickFiles">选择文件</button>
          <p class="dz-hint">支持 PDF / TXT / MD / JSON，单个 ≤ 30 MB</p>
        </div>
      </template>

      <div v-else class="readonly-note">
        <el-icon><FileIcon /></el-icon>
        <p>仅团队管理员可以新增文档。</p>
      </div>

      <input ref="fileInput" type="file" multiple :accept="ACCEPT_EXTS.join(',')" hidden @change="onFilesChosen" />

      <div class="upload-list">
        <div v-for="u in uploads" :key="u.key" class="upload-item">
          <span class="file-badge sm" :style="{ background: extColor(u.name) }">
            {{ extOf(u.name).slice(1).toUpperCase() || 'FILE' }}
          </span>
          <div class="upload-body">
            <span class="upload-name" :title="u.name">{{ u.name }}</span>
            <div v-if="!u.error" class="bar"><i :style="{ width: u.percent + '%' }" /></div>
            <span v-else class="upload-err" :title="u.error">{{ u.error }}</span>
          </div>
          <button class="row-icon" :title="u.error ? '移除' : '取消'" @click="cancelUpload(u.key)">
            <el-icon><CircleCloseFilled /></el-icon>
          </button>
        </div>
      </div>

      <div class="panel-footer">
        <button class="btn-dark wide" @click="refresh()">
          <el-icon><RefreshRight /></el-icon>
          <span>刷新列表</span>
        </button>
      </div>
    </aside>

    <!-- ============ 明细抽屉 ============ -->
    <el-drawer v-model="drawer" size="600px" :with-header="false">
      <div v-if="current" class="detail">
        <header class="detail-head">
          <span class="file-badge" :style="{ background: extColor(current.fileName) }">
            {{ extOf(current.fileName).slice(1).toUpperCase() || 'FILE' }}
          </span>
          <div class="detail-title">
            <h2 :title="current.fileName">{{ current.fileName }}</h2>
            <div class="detail-sub">
              <span class="status" :class="statusClass(current)">{{ statusText(current) }}</span>
              <span class="kb-name">{{ kbName(current.kbId) }}</span>
              <span>{{ fmtSize(current.sizeBytes) }}</span>
              <span v-if="current.pageCount">· {{ current.pageCount }} 页</span>
              <span v-if="current.chunkCount != null">· {{ current.chunkCount }} 片段</span>
              <span v-if="current.vectorCount != null" :class="{ 'vec-short': isVectorSparse(current) }"
                >· 向量 {{ current.vectorCount }}</span
              >
            </div>
            <p v-if="current.status === 'failed' && current.error" class="detail-error">{{ current.error }}</p>
            <p v-else-if="statusHint(current)" class="detail-warn">{{ statusHint(current) }}</p>
          </div>
        </header>

        <div class="detail-actions">
          <a class="btn-ghost" :href="`/api/documents/${current.id}/file`" target="_blank" rel="noopener">
            <el-icon><Download /></el-icon> 原件
          </a>
          <button v-if="isAdmin" class="btn-ghost" @click="onReparse(current)">
            <el-icon><RefreshRight /></el-icon> 重新解析
          </button>
          <button v-if="isAdmin" class="btn-ghost danger" @click="onDelete(current)">
            <el-icon><Delete /></el-icon> 删除
          </button>
        </div>

        <div class="detail-tabs">
          <button :class="{ on: detailTab === 'pages' }" @click="detailTab = 'pages'">页原文</button>
          <button :class="{ on: detailTab === 'chunks' }" @click="detailTab = 'chunks'">片段</button>
        </div>

        <div v-loading="detailLoading" class="detail-body">
          <template v-if="detailTab === 'pages'">
            <div v-if="pages && pages.length === 0" class="empty small">还没有解析出内容</div>
            <article v-for="p in pages ?? []" :key="p.pageNo" class="page-item">
              <h3>第 {{ p.pageNo }} 页</h3>
              <pre>{{ p.cleanedText || p.rawText || '（无文本）' }}</pre>
            </article>
          </template>
          <template v-else>
            <div v-if="chunks && chunks.length === 0" class="empty small">还没有切分出片段</div>
            <article v-for="c in chunks ?? []" :key="c.seq" class="page-item">
              <h3>#{{ c.seq }} · P{{ c.pageFrom }}–{{ c.pageTo }} · {{ c.charCount }} 字</h3>
              <p v-if="c.heading" class="chunk-heading">{{ c.heading }}</p>
              <pre>{{ c.content }}</pre>
            </article>
          </template>
        </div>
      </div>
    </el-drawer>

    <el-dialog v-model="kbDialogOpen" title="新建知识库" width="420px" append-to-body>
      <el-form label-position="top">
        <el-form-item label="名称">
          <el-input v-model="kbForm.name" maxlength="40" placeholder="例如：报价资料" />
        </el-form-item>
        <el-form-item label="可见性">
          <el-radio-group v-model="kbForm.visibility">
            <el-radio-button value="private">私人库</el-radio-button>
            <el-radio-button value="public">公共库</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item v-if="kbForm.visibility === 'private'" label="最低可见等级">
          <el-input-number v-model="kbForm.minLevelRank" :min="1" :max="myLevelRank" />
          <span class="form-hint">L{{ kbForm.minLevelRank }} 及以上账号可读</span>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button text @click="kbDialogOpen = false">取消</el-button>
        <el-button type="primary" :loading="kbSaving" @click="saveKnowledgeBase">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
/* ============ 布局 ============ */
.doc-page {
  height: 100%;
  min-height: 0;
  display: grid;
  grid-template-columns: minmax(0, 1fr) 330px;
  gap: 18px;
}

.doc-card {
  min-width: 0;
  min-height: 0;
  overflow: auto;
  display: flex;
  flex-direction: column;
  padding: 22px 26px;
  background: var(--aw-surface);
  border-radius: var(--aw-radius-l);
  box-shadow: var(--aw-shadow-1);
}

/* ============ 顶栏 ============ */
.doc-topbar {
  display: flex;
  align-items: center;
  gap: 14px;
}

.search-box {
  flex: 1;
  max-width: 340px;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 14px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-full);
  background: var(--aw-surface-2);
  transition: border-color var(--aw-dur) var(--aw-ease);
}

.search-box:focus-within {
  border-color: var(--aw-doc-ink-2);
}

.search-icon {
  color: var(--aw-text-3);
  font-size: 15px;
}

.search-box input {
  flex: 1;
  min-width: 0;
  border: none;
  outline: none;
  background: transparent;
  font-size: 13px;
  color: var(--aw-text);
}

.view-label {
  font-size: 12.5px;
  color: var(--aw-text-3);
}

.view-toggle {
  display: flex;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-full);
  overflow: hidden;
}

.view-toggle button {
  display: grid;
  place-items: center;
  width: 34px;
  height: 30px;
  border: none;
  background: transparent;
  color: var(--aw-text-3);
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease);
}

.view-toggle button.on {
  background: var(--aw-doc-ink);
  color: #fff;
}

.ghost-icon {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  margin-left: auto;
  border: none;
  border-radius: 50%;
  background: transparent;
  color: var(--aw-text-2);
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease);
}

.ghost-icon:hover {
  background: var(--aw-surface-3);
}

/* ============ 标题与筛选 ============ */
.title-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
  margin: 20px 0 14px;
}

.doc-title {
  margin-right: 6px;
  font-size: 24px;
  font-weight: 700;
  letter-spacing: 0.01em;
  color: var(--aw-text);
}

.chip-select {
  width: 128px;
}

.chip-select :deep(.el-select__wrapper) {
  border-radius: var(--aw-radius-full);
  background: var(--aw-surface-3);
  box-shadow: none;
  min-height: 32px;
}

/* ============ 表格 ============ */
.table {
  flex: 1;
  min-height: 0;
}

.thead,
.trow {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 84px 96px 64px 110px 76px;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
}

.thead {
  font-size: 12px;
  color: var(--aw-text-3);
  border-bottom: 1px solid var(--aw-border);
}

.trow {
  border-radius: var(--aw-radius-m);
  font-size: 13.5px;
  color: var(--aw-text);
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease);
}

.trow:hover {
  background: var(--aw-surface-3);
}

.cell-name {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.name-copy {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.name-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 550;
}

.kb-name {
  width: fit-content;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  padding: 1px 7px;
  border-radius: var(--aw-radius-full);
  background: var(--aw-surface-3);
  color: var(--aw-text-3);
  font-size: 11px;
  line-height: 1.5;
}

.cell-dim {
  color: var(--aw-text-2);
  font-size: 12.5px;
}

.file-badge {
  flex: none;
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 9px;
  color: #fff;
  font-size: 9.5px;
  font-weight: 700;
  letter-spacing: 0.02em;
}

.file-badge.sm {
  width: 30px;
  height: 30px;
  font-size: 8.5px;
}

.file-badge.big {
  width: 44px;
  height: 44px;
  font-size: 11px;
  border-radius: 11px;
}

.row-actions {
  display: flex;
  justify-content: flex-end;
  gap: 2px;
  opacity: 0;
  transition: opacity var(--aw-dur) var(--aw-ease);
}

.trow:hover .row-actions {
  opacity: 1;
}

.row-icon {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--aw-text-3);
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease);
}

.row-icon:hover {
  background: var(--aw-surface-3);
  color: var(--aw-text);
}

.row-icon.danger:hover {
  background: var(--el-color-danger-light-9);
  color: var(--el-color-danger);
}

/* ============ 状态徽标 ============ */
.status {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 10px;
  border-radius: var(--aw-radius-full);
  font-size: 11.5px;
  font-weight: 550;
  white-space: nowrap;
}

.status.pending {
  background: var(--aw-surface-3);
  color: var(--aw-text-2);
}

.status.parsing {
  background: var(--el-color-primary-light-9);
  color: var(--aw-primary);
}

.status.parsed {
  background: var(--el-color-success-light-9);
  color: var(--el-color-success-dark-2);
}

.status.failed {
  background: var(--el-color-danger-light-9);
  color: var(--el-color-danger);
  cursor: help;
}

/* 已解析但向量不全：琥珀色，与「可检索」的绿色明确区分。 */
.status.text-only {
  background: var(--el-color-warning-light-9);
  color: var(--el-color-warning-dark-2);
  cursor: help;
}

.status .dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
  animation: pulse 1.1s ease-in-out infinite;
}

@keyframes pulse {
  0%,
  100% {
    opacity: 0.25;
  }
  50% {
    opacity: 1;
  }
}

/* ============ 空态 ============ */
.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 56px 0;
  color: var(--aw-text-3);
}

.empty .el-icon {
  font-size: 34px;
}

.empty p {
  font-size: 13px;
}

.empty.small {
  padding: 28px 0;
}

/* ============ 网格视图 ============ */
.grid {
  flex: 1;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
  gap: 14px;
  align-content: start;
}

.gcard {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 16px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  cursor: pointer;
  transition: box-shadow var(--aw-dur) var(--aw-ease), border-color var(--aw-dur) var(--aw-ease);
}

.gcard:hover {
  border-color: var(--aw-border-strong);
  box-shadow: var(--aw-shadow-2);
}

.gname {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13.5px;
  font-weight: 550;
}

.gmeta {
  font-size: 12px;
  color: var(--aw-text-3);
}

.gdel {
  position: absolute;
  top: 10px;
  right: 10px;
}

/* ============ 右侧上传面板 ============ */
.upload-panel {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-height: 0;
  padding: 20px 18px;
  background: var(--aw-doc-panel);
  border-radius: var(--aw-radius-l);
}

.panel-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.panel-title {
  margin: 0;
  font-size: 16px;
  font-weight: 650;
  color: var(--aw-text);
}

.panel-sub {
  margin: 3px 0 0;
  font-size: 11.5px;
  color: var(--aw-text-3);
}

.link-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex: none;
  border: none;
  background: transparent;
  color: var(--aw-doc-accent);
  font-family: inherit;
  font-size: 12px;
  cursor: pointer;
  padding: 2px 0;
}

.link-btn:hover {
  text-decoration: underline;
}

.field-label {
  margin-bottom: -8px;
  font-size: 11.5px;
  font-weight: 550;
  color: var(--aw-text-2);
}

.kb-select {
  width: 100%;
}

.kb-select :deep(.el-select__wrapper) {
  min-height: 34px;
  background: var(--aw-surface);
  box-shadow: none;
  border: 1px solid var(--aw-border);
}

.empty-hint {
  font-size: 12px;
  color: var(--aw-text-3);
}

.readonly-note {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 36px 16px;
  border: 1px dashed var(--aw-border);
  border-radius: var(--aw-radius-m);
  color: var(--aw-text-3);
  text-align: center;
}

.readonly-note .el-icon {
  font-size: 28px;
}

.readonly-note p {
  margin: 0;
  font-size: 12.5px;
}

.form-hint {
  margin-left: 8px;
  color: var(--aw-text-3);
  font-size: 12px;
}

.dropzone {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 30px 16px 24px;
  border: 2px dashed var(--aw-doc-dash);
  border-radius: var(--aw-radius-m);
  text-align: center;
  cursor: pointer;
  transition: border-color var(--aw-dur) var(--aw-ease), background-color var(--aw-dur) var(--aw-ease);
}

.dropzone:hover,
.dropzone:focus-visible {
  border-color: var(--aw-doc-ink-2);
  background: rgb(255 255 255 / 0.55);
  outline: none;
}

.dz-icon {
  font-size: 30px;
  color: var(--aw-doc-ink-2);
}

.dz-text {
  font-size: 13px;
  color: var(--aw-text-2);
}

.dz-hint {
  font-size: 11.5px;
  color: var(--aw-text-3);
}

.btn-dark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 7px;
  padding: 9px 22px;
  border: none;
  border-radius: var(--aw-radius-full);
  background: var(--aw-doc-ink-2);
  color: #fff;
  font-size: 13px;
  font-weight: 550;
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease), transform var(--aw-dur) var(--aw-ease);
}

.btn-dark:hover {
  background: var(--aw-doc-ink);
}

.btn-dark:active {
  transform: scale(0.98);
}

.btn-dark.wide {
  width: 100%;
}

.upload-list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.upload-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 12px;
  background: var(--aw-surface);
  border-radius: var(--aw-radius-m);
  box-shadow: var(--aw-shadow-1);
}

.upload-body {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.upload-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12.5px;
  font-weight: 550;
}

.bar {
  height: 4px;
  border-radius: 2px;
  background: var(--aw-surface-3);
  overflow: hidden;
}

.bar i {
  display: block;
  height: 100%;
  border-radius: 2px;
  background: var(--aw-doc-accent);
  transition: width 200ms var(--aw-ease);
}

.upload-err {
  font-size: 11.5px;
  color: var(--el-color-danger);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-footer {
  padding-top: 4px;
}

/* ============ 明细抽屉 ============ */
.detail {
  display: flex;
  flex-direction: column;
  height: 100%;
  gap: 14px;
}

.detail-head {
  display: flex;
  gap: 14px;
  align-items: flex-start;
}

.detail-title {
  min-width: 0;
}

.detail-title h2 {
  font-size: 17px;
  font-weight: 650;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.detail-sub {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
  font-size: 12.5px;
  color: var(--aw-text-2);
}

.detail-error {
  margin-top: 8px;
  padding: 8px 12px;
  border-radius: var(--aw-radius-s);
  background: var(--el-color-danger-light-9);
  color: var(--el-color-danger);
  font-size: 12.5px;
}

/* 降级提示：不是错误（全文检索仍可用），但必须让人看见覆盖度不全。 */
.detail-warn {
  margin-top: 8px;
  padding: 8px 12px;
  border-radius: var(--aw-radius-s);
  background: var(--el-color-warning-light-9);
  color: var(--el-color-warning-dark-2);
  font-size: 12.5px;
  line-height: 1.55;
}

.detail-sub .vec-short {
  color: var(--el-color-warning-dark-2);
  font-weight: 550;
}

.detail-actions {
  display: flex;
  gap: 10px;
}

.btn-ghost {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 7px 16px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-full);
  background: var(--aw-surface);
  color: var(--aw-text-2);
  font-size: 12.5px;
  text-decoration: none;
  cursor: pointer;
  transition: border-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease);
}

.btn-ghost:hover {
  border-color: var(--aw-border-strong);
  color: var(--aw-text);
}

.btn-ghost.danger:hover {
  border-color: var(--el-color-danger-light-5);
  color: var(--el-color-danger);
}

.detail-tabs {
  display: flex;
  gap: 4px;
  border-bottom: 1px solid var(--aw-border);
}

.detail-tabs button {
  padding: 9px 16px;
  border: none;
  background: transparent;
  color: var(--aw-text-3);
  font-size: 13px;
  cursor: pointer;
  border-bottom: 2px solid transparent;
  transition: color var(--aw-dur) var(--aw-ease), border-color var(--aw-dur) var(--aw-ease);
}

.detail-tabs button.on {
  color: var(--aw-doc-ink);
  font-weight: 600;
  border-bottom-color: var(--aw-doc-accent);
}

.detail-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.page-item {
  padding: 12px 2px 14px;
  border-bottom: 1px solid var(--aw-border);
}

.page-item h3 {
  margin-bottom: 8px;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--aw-text-2);
}

.chunk-heading {
  margin-bottom: 6px;
  font-size: 12.5px;
  color: var(--aw-doc-accent);
  font-weight: 550;
}

.page-item pre {
  margin: 0;
  padding: 10px 12px;
  border-radius: var(--aw-radius-s);
  background: var(--aw-surface-2);
  font-size: 12px;
  line-height: 1.7;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: inherit;
  color: var(--aw-text);
  max-height: 260px;
  overflow-y: auto;
}

/* ============ 窄屏 ============ */
@media (max-width: 980px) {
  .doc-page {
    grid-template-columns: 1fr;
    grid-template-rows: minmax(0, 1fr) auto;
  }

  .upload-panel {
    max-height: 300px;
  }
}
</style>

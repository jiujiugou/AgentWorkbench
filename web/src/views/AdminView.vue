<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  Delete,
  Key,
  Plus,
  RefreshRight,
  UserFilled,
} from '@element-plus/icons-vue'
import { cachedUser, type AuthUser } from '@/api/auth'
import { membersApi, type Member, type PendingInvite } from '@/api/members'
import {
  archiveKnowledgeBase,
  createKnowledgeBase,
  listKnowledgeBases,
  unarchiveKnowledgeBase,
  updateKnowledgeBase,
  type KnowledgeBaseDto,
} from '@/api/knowledgeBases'
import { listAuditEvents, type AuditEvent } from '@/api/audit'

type Role = 'admin' | 'member'
type Visibility = 'public' | 'private'

const tab = ref<'members' | 'knowledge' | 'audit'>('members')
const loading = ref(false)
const me = ref<AuthUser | null>(null)
const members = ref<Member[]>([])
const kbs = ref<KnowledgeBaseDto[]>([])
const invites = ref<PendingInvite[]>([])
const invitesLoading = ref(false)

const auditEvents = ref<AuditEvent[]>([])
const auditLoading = ref(false)
const auditNextCursor = ref<string | null>(null)
const auditAction = ref('')
const auditResourceKind = ref('')

const myLevel = computed(() => me.value?.levelRank ?? 1)

const memberDialog = ref(false)
const memberMode = ref<'create' | 'edit'>('create')
const memberSaving = ref(false)
const editingMember = ref<Member | null>(null)
const memberForm = reactive<{
  email: string
  password: string
  role: Role
  levelRank: number
}>({ email: '', password: '', role: 'member', levelRank: 1 })

const inviteDialog = ref(false)
const inviteSaving = ref(false)
const inviteForm = reactive<{ email: string; role: Role }>({ email: '', role: 'member' })
const inviteLink = ref('')

const kbDialog = ref(false)
const kbSaving = ref(false)
const editingKb = ref<KnowledgeBaseDto | null>(null)
const kbForm = reactive<{
  name: string
  visibility: Visibility
  minLevelRank: number
}>({ name: '', visibility: 'private', minLevelRank: 1 })

async function load(): Promise<void> {
  loading.value = true
  try {
    const [user, list, bases] = await Promise.all([
      cachedUser(),
      membersApi.list(),
      listKnowledgeBases(),
    ])
    me.value = user
    members.value = list
    kbs.value = bases
  } catch (e) {
    ElMessage.error(`加载管理数据失败：${(e as Error).message}`)
  } finally {
    loading.value = false
  }
}

async function loadInvites(): Promise<void> {
  invitesLoading.value = true
  try {
    invites.value = await membersApi.listInvites()
  } catch (e) {
    ElMessage.error(`加载邀请失败：${(e as Error).message}`)
  } finally {
    invitesLoading.value = false
  }
}

async function loadAudit(reset = true): Promise<void> {
  auditLoading.value = true
  try {
    const page = await listAuditEvents({
      limit: 50,
      before: reset ? null : auditNextCursor.value,
      action: auditAction.value.trim() || undefined,
      resourceKind: auditResourceKind.value.trim() || undefined,
    })
    auditEvents.value = reset ? page.events : [...auditEvents.value, ...page.events]
    auditNextCursor.value = page.nextCursor
  } catch (e) {
    ElMessage.error(`加载审计失败：${(e as Error).message}`)
  } finally {
    auditLoading.value = false
  }
}

onMounted(() => {
  void load()
  void loadInvites()
})

watch(tab, (value) => {
  if (value === 'audit' && auditEvents.value.length === 0) void loadAudit(true)
})

function roleLabel(role: string): string {
  return role === 'admin' ? '管理员' : '成员'
}

function statusLabel(status: string): string {
  return status === 'active' ? '启用' : '已停用'
}

function canManage(member: Member): boolean {
  return member.id !== me.value?.id && member.levelRank <= myLevel.value
}

function canEditKb(kb: KnowledgeBaseDto): boolean {
  if (kb.archivedAt) return false
  return kb.visibility === 'public' || (kb.minLevelRank ?? 1) <= myLevel.value
}

async function archiveKb(kb: KnowledgeBaseDto): Promise<void> {
  if (kb.isDefault || !canEditKb(kb)) return
  try {
    await ElMessageBox.confirm(
      `归档「${kb.name}」？归档后列表、检索和下载都看不到它，但资料不会删除。`,
      '归档知识库',
      { type: 'warning', confirmButtonText: '归档' },
    )
  } catch {
    return
  }
  try {
    await archiveKnowledgeBase(kb.id)
    ElMessage.success('知识库已归档')
    await load()
  } catch (e) {
    ElMessage.error(`归档失败：${(e as Error).message}`)
  }
}

async function unarchiveKb(kb: KnowledgeBaseDto): Promise<void> {
  try {
    await unarchiveKnowledgeBase(kb.id)
    ElMessage.success('已取消归档')
    await load()
  } catch (e) {
    ElMessage.error(`取消归档失败：${(e as Error).message}`)
  }
}

function fmtTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString()
}

function openCreateMember(): void {
  memberMode.value = 'create'
  editingMember.value = null
  memberForm.email = ''
  memberForm.password = ''
  memberForm.role = 'member'
  memberForm.levelRank = 1
  memberDialog.value = true
}

function openEditMember(member: Member): void {
  if (!canManage(member)) return
  memberMode.value = 'edit'
  editingMember.value = member
  memberForm.email = member.email
  memberForm.password = ''
  memberForm.role = member.role === 'admin' ? 'admin' : 'member'
  memberForm.levelRank = member.levelRank
  memberDialog.value = true
}

async function saveMember(): Promise<void> {
  memberSaving.value = true
  try {
    if (memberMode.value === 'create') {
      if (memberForm.password.length < 12) {
        ElMessage.warning('临时口令至少 12 位')
        return
      }
      await membersApi.create({
        email: memberForm.email.trim(),
        password: memberForm.password,
        role: memberForm.role,
        levelRank: memberForm.levelRank,
      })
      ElMessage.success('已创建成员')
    } else if (editingMember.value) {
      await membersApi.update(editingMember.value.id, {
        role: memberForm.role,
        levelRank: memberForm.levelRank,
      })
      ElMessage.success('已更新角色/等级')
    }
    memberDialog.value = false
    await load()
  } catch (e) {
    ElMessage.error(`保存失败：${(e as Error).message}`)
  } finally {
    memberSaving.value = false
  }
}

async function resetPassword(member: Member): Promise<void> {
  if (!canManage(member)) return
  let password: string
  try {
    const result = await ElMessageBox.prompt(`为「${member.email}」设置新口令`, '重置密码', {
      inputType: 'password',
      inputPlaceholder: '至少 12 位',
      inputValidator: (value) => value.length >= 12 || '口令至少 12 位',
      confirmButtonText: '重置',
      cancelButtonText: '取消',
    })
    password = result.value
  } catch (e) {
    if (e === 'cancel' || e === 'close') return
    ElMessage.error((e as Error).message)
    return
  }
  try {
    await membersApi.resetPassword(member.id, password)
    ElMessage.success('口令已重置，对方下次登录必须改密')
  } catch (e) {
    ElMessage.error(`重置失败：${(e as Error).message}`)
  }
}

async function toggleMember(member: Member): Promise<void> {
  if (!canManage(member)) return
  const disabling = member.status === 'active'
  try {
    await ElMessageBox.confirm(
      disabling
        ? `停用「${member.email}」？其已登录会话会立即失效。`
        : `恢复「${member.email}」的登录权限？`,
      disabling ? '停用成员' : '恢复成员',
      { type: 'warning', confirmButtonText: disabling ? '停用' : '恢复' },
    )
  } catch {
    return
  }
  try {
    if (disabling) await membersApi.disable(member.id)
    else await membersApi.enable(member.id)
    ElMessage.success(disabling ? '已停用' : '已恢复')
    await load()
  } catch (e) {
    ElMessage.error(`操作失败：${(e as Error).message}`)
  }
}

function openInvite(): void {
  inviteForm.email = ''
  inviteForm.role = 'member'
  inviteLink.value = ''
  inviteDialog.value = true
}

async function createInvite(): Promise<void> {
  inviteSaving.value = true
  try {
    const result = await membersApi.invite({
      role: inviteForm.role,
      email: inviteForm.email.trim() || null,
    })
    inviteLink.value = `${window.location.origin}/invite/${encodeURIComponent(result.token)}`
    ElMessage.success('邀请已创建，链接只显示这一次')
  } catch (e) {
    ElMessage.error(`创建邀请失败：${(e as Error).message}`)
  } finally {
    inviteSaving.value = false
  }
}

async function revokeInvite(invite: PendingInvite): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `撤回发给「${invite.email || '任何人'}」的邀请？原链接将立即失效。`,
      '撤回邀请',
      { type: 'warning', confirmButtonText: '撤回' },
    )
  } catch {
    return
  }
  try {
    await membersApi.revokeInvite(invite.inviteId)
    ElMessage.success('邀请已撤回')
    await loadInvites()
  } catch (e) {
    ElMessage.error(`撤回失败：${(e as Error).message}`)
  }
}

async function copyInvite(): Promise<void> {
  try {
    await navigator.clipboard.writeText(inviteLink.value)
    ElMessage.success('邀请链接已复制')
  } catch {
    ElMessage.warning('复制失败，请手动选中链接')
  }
}

function openCreateKb(): void {
  editingKb.value = null
  kbForm.name = ''
  kbForm.visibility = 'private'
  kbForm.minLevelRank = 1
  kbDialog.value = true
}

function openEditKb(kb: KnowledgeBaseDto): void {
  if (!canEditKb(kb)) return
  editingKb.value = kb
  kbForm.name = kb.name
  kbForm.visibility = kb.visibility === 'public' ? 'public' : 'private'
  kbForm.minLevelRank = kb.minLevelRank ?? 1
  kbDialog.value = true
}

async function saveKb(): Promise<void> {
  const name = kbForm.name.trim()
  if (!name) {
    ElMessage.warning('请填写知识库名称')
    return
  }
  if (kbForm.visibility === 'private' && (kbForm.minLevelRank < 1 || kbForm.minLevelRank > myLevel.value)) {
    ElMessage.warning(`私人库门槛必须在 L1~L${myLevel.value} 之间`)
    return
  }
  kbSaving.value = true
  try {
    const payload = {
      name,
      visibility: kbForm.visibility,
      minLevelRank: kbForm.visibility === 'private' ? kbForm.minLevelRank : null,
    }
    if (editingKb.value) {
      await updateKnowledgeBase(editingKb.value.id, payload)
      ElMessage.success('已更新知识库')
    } else {
      await createKnowledgeBase(payload)
      ElMessage.success('已创建知识库')
    }
    kbDialog.value = false
    await load()
  } catch (e) {
    ElMessage.error(`保存知识库失败：${(e as Error).message}`)
  } finally {
    kbSaving.value = false
  }
}
</script>

<template>
  <div v-loading="loading" class="admin-page">
    <header class="admin-head">
      <div>
        <h1>管理</h1>
        <p>账号等级决定能看哪些知识库；角色只决定能不能管理账号与知识库元数据。</p>
      </div>
      <el-button :icon="RefreshRight" round @click="load">刷新</el-button>
    </header>

    <el-tabs v-model="tab" class="admin-tabs">
      <el-tab-pane label="成员" name="members">
        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>成员</h2>
              <p class="panel-sub">当前账号等级 L{{ myLevel }}；只能管理不高于自己的账号。</p>
            </div>
            <div class="panel-actions">
              <el-button :icon="UserFilled" round @click="openInvite">发邀请</el-button>
              <el-button type="primary" :icon="Plus" round @click="openCreateMember">新增成员</el-button>
            </div>
          </div>

          <el-table :data="members" empty-text="暂无成员" class="admin-table">
            <el-table-column prop="email" label="邮箱" min-width="220" show-overflow-tooltip />
            <el-table-column label="角色" width="100">
              <template #default="{ row }">{{ roleLabel(row.role) }}</template>
            </el-table-column>
            <el-table-column label="等级" width="80">
              <template #default="{ row }">L{{ row.levelRank }}</template>
            </el-table-column>
            <el-table-column label="状态" width="100">
              <template #default="{ row }">
                <el-tag :type="row.status === 'active' ? 'success' : 'info'" effect="light" round>
                  {{ statusLabel(row.status) }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="330">
              <template #default="{ row }">
                <span v-if="row.id === me?.id" class="muted">当前账号</span>
                <template v-else>
                  <el-button text type="primary" :disabled="!canManage(row)" @click="openEditMember(row)">
                    角色/等级
                  </el-button>
                  <el-button text :icon="Key" :disabled="!canManage(row)" @click="resetPassword(row)">
                    重置密码
                  </el-button>
                  <el-button
                    text
                    :type="row.status === 'active' ? 'danger' : 'success'"
                    :disabled="!canManage(row)"
                    @click="toggleMember(row)"
                  >
                    {{ row.status === 'active' ? '停用' : '恢复' }}
                  </el-button>
                </template>
              </template>
            </el-table-column>
          </el-table>

          <div v-loading="invitesLoading" class="invite-box">
            <div class="invite-head">
              <div>
                <h3>待用邀请</h3>
                <p>链接只显示一次；撤回后原链接立即失效。</p>
              </div>
              <el-button :icon="RefreshRight" text round @click="loadInvites">刷新</el-button>
            </div>
            <el-table v-if="invites.length" :data="invites" size="small" class="admin-table">
              <el-table-column label="邮箱" min-width="200">
                <template #default="{ row }">{{ row.email || '不限邮箱' }}</template>
              </el-table-column>
              <el-table-column label="角色" width="90">
                <template #default="{ row }">{{ roleLabel(row.role) }}</template>
              </el-table-column>
              <el-table-column label="过期时间" width="170">
                <template #default="{ row }">{{ fmtTime(row.expiresAt) }}</template>
              </el-table-column>
              <el-table-column label="操作" width="100">
                <template #default="{ row }">
                  <el-button text type="danger" :icon="Delete" @click="revokeInvite(row)">
                    撤回
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
            <p v-else class="muted invite-empty">没有待用邀请。</p>
          </div>
        </section>
      </el-tab-pane>

      <el-tab-pane label="知识库" name="knowledge">
        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>知识库</h2>
              <p class="panel-sub">公共库全员可读；私人库按最低等级开放。文档继承知识库可见性。</p>
            </div>
            <el-button type="primary" :icon="Plus" round @click="openCreateKb">新建知识库</el-button>
          </div>

          <el-table :data="kbs" empty-text="暂无知识库" class="admin-table">
            <el-table-column prop="name" label="名称" min-width="220" show-overflow-tooltip>
              <template #default="{ row }">
                <span>{{ row.name }}</span>
                <el-tag v-if="row.isDefault" size="small" effect="plain" class="inline-tag">默认</el-tag>
                <el-tag v-if="row.archivedAt" size="small" type="info" effect="light" class="inline-tag">
                  已归档
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="可见性" width="110">
              <template #default="{ row }">
                <el-tag :type="row.visibility === 'public' ? 'success' : 'warning'" effect="light" round>
                  {{ row.visibility === 'public' ? '公共' : '私人' }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="最低等级" width="100">
              <template #default="{ row }">
                {{ row.visibility === 'private' ? `L${row.minLevelRank ?? 1}+` : '全员' }}
              </template>
            </el-table-column>
            <el-table-column prop="createdAt" label="创建时间" width="170" />
            <el-table-column label="操作" width="190">
              <template #default="{ row }">
                <el-button text type="primary" :disabled="!canEditKb(row)" @click="openEditKb(row)">
                  编辑
                </el-button>
                <el-button
                  v-if="row.archivedAt"
                  text
                  type="success"
                  @click="unarchiveKb(row)"
                >
                  取消归档
                </el-button>
                <el-button
                  v-else-if="!row.isDefault && canEditKb(row)"
                  text
                  type="danger"
                  @click="archiveKb(row)"
                >
                  归档
                </el-button>
              </template>
            </el-table-column>
          </el-table>
        </section>
      </el-tab-pane>

      <el-tab-pane label="审计" name="audit">
        <section v-loading="auditLoading" class="panel">
          <div class="panel-head">
            <div>
              <h2>审计</h2>
              <p class="panel-sub">账号、等级、知识库与邀请的管理动作；只读，按当前团队隔离。</p>
            </div>
            <el-button :icon="RefreshRight" round @click="loadAudit(true)">刷新</el-button>
          </div>
          <div class="audit-filters">
            <el-input v-model="auditAction" clearable placeholder="action，例如 user.update" />
            <el-input
              v-model="auditResourceKind"
              clearable
              placeholder="resourceKind，例如 knowledge_base"
            />
            <el-button type="primary" @click="loadAudit(true)">查询</el-button>
          </div>
          <el-table :data="auditEvents" empty-text="暂无审计记录" class="admin-table">
            <el-table-column label="时间" width="180">
              <template #default="{ row }">{{ fmtTime(row.occurredAt) }}</template>
            </el-table-column>
            <el-table-column prop="action" label="动作" min-width="160" />
            <el-table-column label="操作人" min-width="160" show-overflow-tooltip>
              <template #default="{ row }">{{ row.actorUserId || '系统' }}</template>
            </el-table-column>
            <el-table-column label="资源" min-width="200" show-overflow-tooltip>
              <template #default="{ row }">
                <span v-if="row.resourceKind">{{ row.resourceKind }} · {{ row.resourceId || '—' }}</span>
                <span v-else>—</span>
              </template>
            </el-table-column>
            <el-table-column label="变更" min-width="180" show-overflow-tooltip>
              <template #default="{ row }">
                <span v-if="row.oldValue || row.newValue">
                  {{ row.oldValue || '—' }} → {{ row.newValue || '—' }}
                </span>
                <span v-else>—</span>
              </template>
            </el-table-column>
          </el-table>
          <div v-if="auditNextCursor" class="audit-more">
            <el-button text type="primary" @click="loadAudit(false)">加载更多</el-button>
          </div>
        </section>
      </el-tab-pane>
    </el-tabs>

    <el-dialog
      v-model="memberDialog"
      :title="memberMode === 'create' ? '新增成员' : '编辑角色/等级'"
      width="440px"
      append-to-body
    >
      <el-form label-position="top">
        <el-form-item label="邮箱">
          <el-input v-model="memberForm.email" :disabled="memberMode === 'edit'" placeholder="name@example.com" />
        </el-form-item>
        <el-form-item v-if="memberMode === 'create'" label="临时口令">
          <el-input v-model="memberForm.password" type="password" show-password placeholder="至少 12 位" />
        </el-form-item>
        <el-form-item label="角色">
          <el-select v-model="memberForm.role" class="full">
            <el-option label="成员" value="member" />
            <el-option label="管理员" value="admin" />
          </el-select>
        </el-form-item>
        <el-form-item label="等级">
          <el-input-number v-model="memberForm.levelRank" :min="1" :max="myLevel" />
          <span class="form-hint">最高 L{{ myLevel }}</span>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button text @click="memberDialog = false">取消</el-button>
        <el-button type="primary" :loading="memberSaving" @click="saveMember">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="inviteDialog" title="发邀请" width="480px" append-to-body>
      <el-form label-position="top">
        <el-form-item label="角色">
          <el-select v-model="inviteForm.role" class="full">
            <el-option label="成员" value="member" />
            <el-option label="管理员" value="admin" />
          </el-select>
        </el-form-item>
        <el-form-item label="限定邮箱（可选）">
          <el-input v-model="inviteForm.email" placeholder="留空则拿到链接的人都能用" />
        </el-form-item>
        <p class="dialog-note">邀请建出的账号默认 L1；等级需要在成员列表里再调整。</p>
        <el-form-item v-if="inviteLink" label="邀请链接">
          <div class="invite-link">
            <el-input v-model="inviteLink" readonly />
            <el-button type="primary" @click="copyInvite">复制</el-button>
          </div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button text @click="inviteDialog = false">关闭</el-button>
        <el-button type="primary" :loading="inviteSaving" @click="createInvite">
          {{ inviteLink ? '再建一张' : '生成链接' }}
        </el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="kbDialog"
      :title="editingKb ? '编辑知识库' : '新建知识库'"
      width="440px"
      append-to-body
    >
      <el-form label-position="top">
        <el-form-item label="名称">
          <el-input v-model="kbForm.name" maxlength="40" placeholder="例如：报价资料" />
        </el-form-item>
        <el-form-item label="可见性">
          <el-radio-group v-model="kbForm.visibility" :disabled="editingKb?.isDefault">
            <el-radio-button value="private">私人库</el-radio-button>
            <el-radio-button value="public">公共库</el-radio-button>
          </el-radio-group>
          <span v-if="editingKb?.isDefault" class="form-hint">默认库必须保持公共</span>
        </el-form-item>
        <el-form-item v-if="kbForm.visibility === 'private'" label="最低可见等级">
          <el-input-number v-model="kbForm.minLevelRank" :min="1" :max="myLevel" />
          <span class="form-hint">L{{ kbForm.minLevelRank }} 及以上可读</span>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button text @click="kbDialog = false">取消</el-button>
        <el-button type="primary" :loading="kbSaving" @click="saveKb">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.admin-page {
  height: 100%;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 22px 26px;
  background: var(--aw-surface);
  border-radius: var(--aw-radius-l);
  box-shadow: var(--aw-shadow-1);
}

.admin-head,
.panel-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.admin-head h1,
.panel-head h2 {
  margin: 0;
  color: var(--aw-text);
  font-weight: 700;
}

.admin-head h1 {
  font-size: 24px;
}

.panel-head h2 {
  font-size: 17px;
}

.admin-head p,
.panel-sub {
  margin: 5px 0 0;
  color: var(--aw-text-3);
  font-size: 12.5px;
  line-height: 1.55;
}

.panel-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.admin-tabs :deep(.el-tabs__header) {
  margin-bottom: 14px;
}

.panel {
  padding: 18px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  background: var(--aw-surface-2);
}

.admin-table {
  margin-top: 14px;
}

.admin-table :deep(.el-table__inner-wrapper::before) {
  display: none;
}

.muted {
  color: var(--aw-text-3);
  font-size: 12.5px;
}

.inline-tag {
  margin-left: 8px;
}

.full {
  width: 100%;
}

.form-hint {
  margin-left: 8px;
  color: var(--aw-text-3);
  font-size: 12px;
}

.dialog-note {
  margin: 0 0 12px;
  color: var(--aw-text-3);
  font-size: 12px;
  line-height: 1.55;
}

.invite-link {
  display: flex;
  gap: 8px;
  width: 100%;
}

.invite-box {
  margin-top: 22px;
  padding-top: 16px;
  border-top: 1px solid var(--aw-border);
}

.invite-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.invite-head h3 {
  margin: 0;
  font-size: 14px;
  color: var(--aw-text);
}

.invite-head p {
  margin: 3px 0 0;
  color: var(--aw-text-3);
  font-size: 12px;
}

.invite-empty {
  margin: 14px 0 0;
}

.audit-filters {
  display: flex;
  gap: 8px;
  margin-top: 14px;
}

.audit-filters .el-input {
  max-width: 260px;
}

.audit-more {
  display: flex;
  justify-content: center;
  padding-top: 12px;
}

@media (max-width: 720px) {
  .admin-page {
    padding: 16px;
  }

  .admin-head,
  .panel-head {
    flex-direction: column;
  }

  .panel-actions {
    width: 100%;
  }
}
</style>

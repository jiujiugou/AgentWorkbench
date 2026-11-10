<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, RefreshRight } from '@element-plus/icons-vue'
import { cachedUser, type AuthUser } from '@/api/auth'
import { membersApi, type Member } from '@/api/members'

/**
 * 成员管理：直接建号，不走邀请。
 *
 * 2026-11-10 删掉了「知识库」与「审计」两个 tab：
 * 前者服务的多知识库 + 账号等级随产品裁决一起砍了，后者打的是 `/api/admin/events`，
 * 那个端点从来没有过（v3 的审计走 `/api/actions/{requestId}/audit`）。
 * 留在这里的两个 tab 会一直 404，而每次打开管理页都报一次错。
 */

type Role = 'admin' | 'member'

const loading = ref(false)
const me = ref<AuthUser | null>(null)
const members = ref<Member[]>([])

const memberDialog = ref(false)
const memberMode = ref<'create' | 'edit'>('create')
const memberSaving = ref(false)
const editingMember = ref<Member | null>(null)
const memberForm = reactive<{
  userName: string
  displayName: string
  email: string
  password: string
  role: Role
}>({ userName: '', displayName: '', email: '', password: '', role: 'member' })

async function load(): Promise<void> {
  loading.value = true
  try {
    const [user, list] = await Promise.all([cachedUser(), membersApi.list()])
    me.value = user
    members.value = list
  } catch (e) {
    ElMessage.error(`加载成员失败：${(e as Error).message}`)
  } finally {
    loading.value = false
  }
}

onMounted(load)

function roleLabel(role: string): string {
  if (role === 'owner') return '所有者'
  if (role === 'admin') return '管理员'
  return '成员'
}

function statusLabel(status: string): string {
  return status === 'active' ? '启用' : '已停用'
}

/**
 * 这一行能不能改。
 *
 * 只做提示，真正的拦截在服务端：owner 账号接口一律拒绝改（`owner_immutable`）。
 * 前端把按钮灰掉是为了让人一眼看出「这个不能动」，不是安全边界。
 */
function canManage(member: Member): boolean {
  if (member.id === me.value?.userId) return false
  return member.role !== 'owner'
}

function openCreateMember(): void {
  memberMode.value = 'create'
  editingMember.value = null
  memberForm.userName = ''
  memberForm.displayName = ''
  memberForm.email = ''
  memberForm.password = ''
  memberForm.role = 'member'
  memberDialog.value = true
}

function openEditMember(member: Member): void {
  if (!canManage(member)) return
  memberMode.value = 'edit'
  editingMember.value = member
  memberForm.userName = member.userName
  memberForm.displayName = member.displayName
  memberForm.email = member.email ?? ''
  memberForm.password = ''
  memberForm.role = member.role === 'admin' ? 'admin' : 'member'
  memberDialog.value = true
}

async function saveMember(): Promise<void> {
  if (memberMode.value === 'create') {
    if (!memberForm.userName.trim()) {
      ElMessage.warning('登录名不能为空')
      return
    }
    // 12 位是界面这一层的门槛。Identity 的策略可能更松，但初始口令要交到人手里，
    // 用户通常不会马上改，门槛定低了等于事实上的弱口令。
    if (memberForm.password.length < 12) {
      ElMessage.warning('初始口令至少 12 位')
      return
    }
  }

  memberSaving.value = true
  try {
    if (memberMode.value === 'create') {
      await membersApi.create({
        userName: memberForm.userName.trim(),
        displayName: memberForm.displayName.trim() || memberForm.userName.trim(),
        email: memberForm.email.trim() || null,
        password: memberForm.password,
        role: memberForm.role,
      })
      ElMessage.success('已创建账号，请把初始口令当面交给对方')
    } else if (editingMember.value) {
      await membersApi.update(editingMember.value.id, {
        displayName: memberForm.displayName.trim() || editingMember.value.userName,
        role: memberForm.role,
      })
      ElMessage.success('已保存')
    }
    memberDialog.value = false
    await load()
  } catch (e) {
    ElMessage.error(`保存失败：${(e as Error).message}`)
  } finally {
    memberSaving.value = false
  }
}

async function toggleMember(member: Member): Promise<void> {
  if (!canManage(member)) return
  const disabling = member.status === 'active'
  try {
    await ElMessageBox.confirm(
      disabling
        ? `停用「${member.displayName || member.userName}」？停用后对方无法再登录，已登录的会话在 Cookie 校验到期后失效。`
        : `恢复「${member.displayName || member.userName}」的登录权限？`,
      disabling ? '停用账号' : '恢复账号',
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
</script>

<template>
  <div v-loading="loading" class="admin-page">
    <header class="admin-head">
      <div>
        <h1>成员</h1>
        <p>在这里开账号、改角色、停用离职人员。登录名就是登录框里填的那个，邮箱只是备注。</p>
      </div>
      <el-button :icon="RefreshRight" round @click="load">刷新</el-button>
    </header>

    <section class="panel">
      <div class="panel-head">
        <div>
          <h2>账号列表</h2>
          <p class="panel-sub">
            共 {{ members.length }} 个；所有者账号不可在此修改。
          </p>
        </div>
        <div class="panel-actions">
          <el-button type="primary" :icon="Plus" round @click="openCreateMember">新增账号</el-button>
        </div>
      </div>

      <el-table :data="members" empty-text="暂无账号" class="admin-table">
        <el-table-column label="登录名" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">
            <span>{{ row.userName }}</span>
            <el-tag v-if="row.id === me?.userId" size="small" effect="plain" class="inline-tag">当前账号</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="姓名" min-width="140" show-overflow-tooltip>
          <template #default="{ row }">{{ row.displayName || '—' }}</template>
        </el-table-column>
        <el-table-column label="邮箱" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">{{ row.email || '—' }}</template>
        </el-table-column>
        <el-table-column label="角色" width="100">
          <template #default="{ row }">{{ roleLabel(row.role) }}</template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="row.status === 'active' ? 'success' : 'info'" effect="light" round>
              {{ statusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="220">
          <template #default="{ row }">
            <span v-if="!canManage(row)" class="muted">不可修改</span>
            <template v-else>
              <el-button text type="primary" @click="openEditMember(row)">姓名/角色</el-button>
              <el-button
                text
                :type="row.status === 'active' ? 'danger' : 'success'"
                @click="toggleMember(row)"
              >
                {{ row.status === 'active' ? '停用' : '恢复' }}
              </el-button>
            </template>
          </template>
        </el-table-column>
      </el-table>
    </section>

    <el-dialog
      v-model="memberDialog"
      :title="memberMode === 'create' ? '新增账号' : '修改姓名 / 角色'"
      width="460px"
      append-to-body
    >
      <el-form label-position="top">
        <el-form-item label="登录名">
          <el-input
            v-model="memberForm.userName"
            :disabled="memberMode === 'edit'"
            placeholder="例如 wang"
          />
        </el-form-item>
        <el-form-item label="姓名">
          <el-input v-model="memberForm.displayName" placeholder="界面上显示的名字" />
        </el-form-item>
        <el-form-item label="邮箱（可选）">
          <el-input v-model="memberForm.email" placeholder="仅作备注，不用于登录" />
        </el-form-item>
        <el-form-item v-if="memberMode === 'create'" label="初始口令">
          <el-input
            v-model="memberForm.password"
            type="password"
            show-password
            placeholder="至少 12 位，建好后当面交给对方"
          />
        </el-form-item>
        <el-form-item label="角色">
          <el-select v-model="memberForm.role" class="full">
            <el-option label="成员（只能发动作、审批自己有权批的）" value="member" />
            <el-option label="管理员（可管理账号与设置）" value="admin" />
          </el-select>
        </el-form-item>
        <p v-if="memberMode === 'create'" class="dialog-note">
          建号后没有「首次登录强制改密」这一步——本版没有这个字段。口令请当面交付并提醒对方自行修改。
        </p>
      </el-form>
      <template #footer>
        <el-button text @click="memberDialog = false">取消</el-button>
        <el-button type="primary" :loading="memberSaving" @click="saveMember">保存</el-button>
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

.dialog-note {
  margin: 0;
  color: var(--aw-text-3);
  font-size: 12px;
  line-height: 1.55;
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

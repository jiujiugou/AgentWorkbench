<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  Bell,
  ChatLineSquare,
  FolderOpened,
  Menu,
  Plus,
  Setting,
  SwitchButton,
  Tickets,
  UserFilled,
} from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import ConversationList from '@/components/ConversationList.vue'
import { cachedUser, logout, type AuthUser } from '@/api/auth'
import { actionsApi } from '@/api/actions'
import { QUICK_STARTS } from '@/suggestions'
import { useAgentStore } from '@/stores/agent'
import { useHealthStore } from '@/stores/health'

const agent = useAgentStore()
const health = useHealthStore()
const route = useRoute()
const router = useRouter()

const drawerOpen = ref(false)
const composerOpen = ref(false)
const draft = ref('')
const user = ref<AuthUser | null>(null)

const isChat = computed(() => route.path.startsWith('/chat'))
const isPasswordChange = computed(() => route.name === 'change-password')
const isInvite = computed(() => route.name === 'invite')
const isAdmin = computed(() => user.value?.role === 'owner' || user.value?.role === 'admin')
const activeNav = computed(() => {
  if (route.path.startsWith('/admin')) return '/admin'
  if (route.path.startsWith('/settings')) return '/settings'
  if (route.path.startsWith('/approvals')) return '/approvals'
  // /actions 与 /actions/:requestId 都高亮同一项：详情是列表的下钻，不是并列页面。
  if (route.path.startsWith('/actions')) return '/actions'
  if (isChat.value) return '/chat'
  return '/'
})

const healthTip = computed(() => {
  const h = health.health
  if (h.error) return `后端异常：${h.error}`
  if (h.status === 'connected') return h.modelConfigured ? '后端正常 · 模型已配置' : '后端正常 · 模型未配置'
  return '正在检查后端…'
})

async function refreshUser(): Promise<void> {
  user.value = await cachedUser()
}

watch(
  () => route.fullPath,
  () => {
    void refreshUser()
  },
)


/** 打开「新建对话」弹窗。**不直接 newChat**：先让人决定第一句说什么，再开始。 */
function openComposer(): void {
  draft.value = ''
  composerOpen.value = true
  drawerOpen.value = false
}

/**
 * 弹窗确认：清空当前会话 → 把第一句交给 ChatView 发。
 *
 * 第一句由 ChatView 发而不是这里发：滚动、输入框自动增高、generating 态都在那边，
 * 在这里再写一遍等于两处都能发消息，而漏改的那一处不会报错。
 */
function startNewChat(): void {
  // 生成中不许开新对话：`newChat()` 会拦住，但那时弹窗已经关了、第一句也写进
  // `pendingQuestion` 了——`handleSend` 因为 generating 直接 return，那句就没了。
  // 在关弹窗前拦住，用户还看得见自己写的东西。
  if (agent.generating) {
    ElMessage.warning('正在生成回答，先停止或等它跑完')
    return
  }
  agent.newChat()
  const first = draft.value.trim()
  if (first) agent.pendingQuestion = first
  composerOpen.value = false
  if (route.path !== '/chat') void router.push('/chat')
}

/** 快捷开场：填进输入框而不是直接开跑——人还有机会改。 */
function useQuickStart(text: string): void {
  draft.value = text
}

async function signOut(): Promise<void> {
  try {
    await logout()
  } catch (e) {
    // **失败就不走**：登出要删的是服务端那一行，删不掉的时候票仍然有效
    // （cookie 是 HttpOnly，界面上清不掉它）。把人送到登录页等于告诉他
    // 「已经退出了」，而下一个拿到这台机器的人仍然可以用他的身份——
    // 那是一个看得见的错误，换一个看不见的。
    ElMessage.error(`退出登录失败：${(e as Error).message}`)
    return
  }
  agent.newChat()
  user.value = null
  void router.push('/login')
}

/**
 * 侧边栏的待审批角标。
 *
 * 存在的理由只有一个：**模型挂起一单时，用户正停在对话页，看不到任何信号。**
 * 没有角标的话，他会以为「模型说它提交了」只是句空话，而实际上有一件事正等着他点。
 *
 * 计数是提示，不是数据源：拿不到就保持原值、不弹错——每个网络抖动都打扰人。
 * 与审批页同样用轮询（Hub 还没有挂起事件），间隔放宽到 15 秒。
 */
const pendingCount = ref(0)
let pendingTimer: number | undefined

async function refreshPendingCount(): Promise<void> {
  try {
    pendingCount.value = (await actionsApi.pending()).length
  } catch {
    /* 静默：角标失准比打断人更可接受 */
  }
}

onMounted(() => {
  void refreshPendingCount()
  pendingTimer = window.setInterval(() => {
    if (document.visibilityState === 'visible') void refreshPendingCount()
  }, 15_000)
})

onUnmounted(() => {
  if (pendingTimer !== undefined) window.clearInterval(pendingTimer)
})

onMounted(() => {
  void refreshUser()
  // 改密页不恢复业务数据：此刻服务端还没放行，restore() 只会制造一串 403。
  if (!isPasswordChange.value && !isInvite.value) void agent.restore()
  health.start()
})
</script>

<template>
  <RouterView v-if="isPasswordChange || isInvite" />
  <div v-else class="shell">
    <!-- 桌面端：深墨绿图标侧边栏 -->
    <aside class="sidebar">
      <RouterLink to="/" class="brand" title="AgentWorkbench">
        <span class="brand-mark">AW</span>
      </RouterLink>

      <nav class="nav">
        <RouterLink to="/" class="nav-item" :class="{ active: activeNav === '/' }" title="资料">
          <el-icon><FolderOpened /></el-icon>
        </RouterLink>
        <RouterLink to="/chat" class="nav-item" :class="{ active: activeNav === '/chat' }" title="对话">
          <el-icon><ChatLineSquare /></el-icon>
        </RouterLink>
        <!-- 待审批：模型的写动作挂起后没有这个入口，用户在界面上就永远批准不了（S07 → S08 断链） -->
        <RouterLink
          to="/approvals"
          class="nav-item"
          :class="{ active: activeNav === '/approvals' }"
          title="待审批"
        >
          <el-icon><Bell /></el-icon>
          <span v-if="pendingCount" class="nav-badge">{{ pendingCount > 99 ? '99+' : pendingCount }}</span>
        </RouterLink>
        <!-- 追溯：判据②「任一动作能答 谁/何时/依据哪条规则/变成什么」的入口。
             与待审批并列而不是藏在会话里——它要能脱离某次对话回答「系统在我这里做过什么」 -->
        <RouterLink
          to="/actions"
          class="nav-item"
          :class="{ active: activeNav === '/actions' }"
          title="动作追溯"
        >
          <el-icon><Tickets /></el-icon>
        </RouterLink>
        <RouterLink
          v-if="isAdmin"
          to="/admin"
          class="nav-item"
          :class="{ active: activeNav === '/admin' }"
          title="管理"
        >
          <el-icon><UserFilled /></el-icon>
        </RouterLink>
        <RouterLink to="/settings" class="nav-item" :class="{ active: activeNav === '/settings' }" title="设置">
          <el-icon><Setting /></el-icon>
        </RouterLink>
      </nav>

      <div class="sidebar-footer">
        <el-tooltip :content="healthTip" placement="right">
          <button
            class="health-dot"
            :class="health.health.status === 'connected' ? 'ok' : health.health.status === 'error' ? 'bad' : ''"
            @click="router.push('/settings')"
          >
            <i />
          </button>
        </el-tooltip>
        <!-- 登出（REQ-01）。没有这个入口，`POST /api/auth/logout` 在产品里不可达：
             换账号、把共用机器交还给别人，都只能靠清浏览器数据。 -->
        <el-tooltip content="退出登录" placement="right">
          <button class="signout" aria-label="退出登录" @click="signOut">
            <el-icon><SwitchButton /></el-icon>
          </button>
        </el-tooltip>
      </div>
    </aside>

    <!-- 移动端顶栏 -->
    <header class="mobile-bar">
      <button class="icon-btn" title="会话列表" @click="drawerOpen = true">
        <el-icon><Menu /></el-icon>
      </button>
      <span class="mobile-title">AgentWorkbench</span>
      <RouterLink v-if="isAdmin" to="/admin" class="icon-btn" title="管理">
        <el-icon><UserFilled /></el-icon>
      </RouterLink>
      <RouterLink to="/settings" class="icon-btn" title="设置">
        <el-icon><Setting /></el-icon>
      </RouterLink>
    </header>

    <el-drawer v-model="drawerOpen" direction="ltr" size="280px" :with-header="false">
      <div class="drawer-body">
        <button class="new-chat" @click="openComposer">
          <el-icon><Plus /></el-icon>新对话
        </button>
        <div class="drawer-label">最近会话</div>
        <ConversationList />
      </div>
    </el-drawer>

    <main class="main" :class="{ flush: isChat }">
      <div class="main-body" :class="{ chat: isChat }">
        <aside v-if="isChat" class="conv-panel">
          <div class="conv-side-head">
            <span class="conv-side-title">对话</span>
            <button class="new-chat" @click="openComposer">
              <el-icon><Plus /></el-icon>新对话
            </button>
          </div>
          <ConversationList />
        </aside>
        <RouterView />
      </div>
    </main>

    <!-- 新建对话。**弹窗里只有「第一句」这一件事**：入口切换在对话页顶部，
         切换入口本身就会新建会话；在这里再摆一次模式选项会造出第二个真源。 -->
    <el-dialog
      v-model="composerOpen"
      class="new-chat-dialog"
      width="460px"
      align-center
      :show-close="false"
      append-to-body
    >
      <template #header>
        <div class="dialog-head">
          <span class="dialog-badge"><el-icon><ChatLineSquare /></el-icon></span>
          <div class="dialog-copy">
            <h3>新建对话</h3>
            <p>先说一句，也可以进去再想</p>
          </div>
        </div>
      </template>

      <el-input
        v-model="draft"
        type="textarea"
        :rows="3"
        resize="none"
        maxlength="500"
        placeholder="想问什么？留空也行"
        @keydown.enter.exact.prevent="startNewChat"
      />

      <div class="dialog-starts">
        <span class="dialog-starts-label">常用开场</span>
        <button v-for="s in QUICK_STARTS" :key="s" class="start-chip" @click="useQuickStart(s)">
          {{ s }}
        </button>
      </div>

      <template #footer>
        <div class="dialog-footer">
          <el-button text @click="composerOpen = false">取消</el-button>
          <el-button type="primary" round @click="startNewChat">开始对话</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.shell {
  height: 100%;
  display: grid;
  grid-template-columns: 68px 1fr;
  background: var(--aw-doc-page);
}

/* ---------- 侧边栏 ---------- */
.sidebar {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 18px;
  padding: 16px 0 18px;
  background: var(--aw-doc-ink);
}

.brand {
  display: grid;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 11px;
  background: rgb(255 255 255 / 0.1);
  color: #f4efe9;
  font-size: 13px;
  font-weight: 750;
  letter-spacing: 0.04em;
  text-decoration: none;
  transition: background-color var(--aw-dur) var(--aw-ease);
}

.brand:hover {
  background: rgb(255 255 255 / 0.16);
}

.nav {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-top: 6px;
}

.nav-item {
  display: grid;
  place-items: center;
  width: 42px;
  height: 42px;
  border-radius: 50%;
  color: rgb(244 239 233 / 0.62);
  text-decoration: none;
  /* 角标绝对定位在右上角：不影响图标居中，也不改导航项的尺寸。 */
  position: relative;
  transition: background-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease);
}

/* 有单子等人处置时的提醒。红点不是装饰：没有它，用户停在对话页就完全看不到
   「模型已经挂起一张单、正等着你批」这件事。 */
.nav-badge {
  position: absolute;
  top: 2px;
  right: 2px;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  border-radius: 8px;
  background: var(--aw-warning);
  color: #fff;
  font-size: 10.5px;
  font-weight: 700;
  line-height: 16px;
  text-align: center;
  box-sizing: border-box;
  pointer-events: none;
}

.nav-item .el-icon {
  font-size: 19px;
}

.nav-item:hover {
  color: #f4efe9;
  background: rgb(255 255 255 / 0.09);
}

.nav-item.active {
  color: #fff;
  background: var(--aw-doc-accent);
  box-shadow: 0 4px 12px rgb(0 0 0 / 0.22);
}

.sidebar-footer {
  margin-top: auto;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
}

.signout {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  border: none;
  border-radius: 50%;
  background: transparent;
  color: rgb(244 239 233 / 0.55);
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease);
}

.signout:hover {
  color: #f4efe9;
  background: rgb(255 255 255 / 0.09);
}

.health-dot {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  border: none;
  border-radius: 50%;
  background: rgb(255 255 255 / 0.08);
  cursor: pointer;
}

.health-dot i {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: #b9c2be;
  transition: background-color var(--aw-dur) var(--aw-ease);
}

.health-dot.ok i {
  background: #5fce9d;
  box-shadow: 0 0 0 3px rgb(95 206 157 / 0.18);
}

.health-dot.bad i {
  background: #ef6b5e;
  box-shadow: 0 0 0 3px rgb(239 107 94 / 0.18);
}

/* ---------- 主区 ---------- */
.main {
  height: 100%;
  min-width: 0;
  min-height: 0;
  padding: 16px 18px;
}

/* 对话页铺满：不留外框，由会话栏与消息区自己分区。
   资料页仍然用外边距——那是「一张张卡片」的形态，和对话的「一整块画布」不是一回事。 */
.main.flush {
  padding: 0;
}

.main-body {
  height: 100%;
  min-height: 0;
}

.main-body.chat {
  display: grid;
  /* 会话栏固定，消息区吃掉剩下的全部宽度。`minmax(0,1fr)` 不是 `1fr`：
     长标题/长代码块会把 1fr 撑破，而撑破的方向是把输入框挤出屏幕。 */
  grid-template-columns: 268px minmax(0, 1fr);
  height: 100%;
  min-height: 0;
}

.conv-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
  padding: 18px 12px 12px;
  background: var(--aw-surface-3);
  border-right: 1px solid var(--aw-border);
}

.conv-side-head {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 0 2px;
}

.conv-side-title {
  padding-left: 6px;
  font-size: 11px;
  font-weight: 650;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--aw-text-3);
}

.conv-panel > :deep(.conv-list) {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.new-chat {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 10px 12px;
  border: none;
  border-radius: var(--aw-radius-m);
  background: linear-gradient(135deg, var(--aw-primary), #7c6cf0);
  color: #fff;
  font-family: inherit;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  box-shadow: 0 6px 16px rgb(79 70 229 / 0.22);
  transition: transform var(--aw-dur) var(--aw-ease), box-shadow var(--aw-dur) var(--aw-ease);
}

.new-chat:hover {
  transform: translateY(-1px);
  box-shadow: 0 10px 22px rgb(79 70 229 / 0.28);
}

.new-chat:active {
  transform: translateY(0);
}

.new-chat .el-icon {
  font-size: 15px;
}

/* ---------- 新建对话弹窗 ---------- */
.dialog-head {
  display: flex;
  align-items: center;
  gap: 12px;
}

.dialog-badge {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  flex: none;
  border-radius: 13px;
  color: #fff;
  font-size: 19px;
  background: linear-gradient(135deg, var(--aw-primary), #8b7cf6);
  box-shadow: 0 6px 16px rgb(79 70 229 / 0.28);
}

.dialog-copy h3 {
  margin: 0;
  font-size: 16px;
  font-weight: 650;
  color: var(--aw-text);
}

.dialog-copy p {
  margin: 2px 0 0;
  font-size: 12.5px;
  color: var(--aw-text-3);
}

.dialog-starts {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 14px;
}

.dialog-starts-label {
  width: 100%;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.06em;
  color: var(--aw-text-3);
}

.start-chip {
  max-width: 100%;
  padding: 6px 12px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-full);
  background: var(--aw-surface-2);
  color: var(--aw-text-2);
  font-family: inherit;
  font-size: 12.5px;
  text-align: left;
  cursor: pointer;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  transition: border-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease),
    background-color var(--aw-dur) var(--aw-ease);
}

.start-chip:hover {
  border-color: var(--el-color-primary-light-5);
  background: var(--el-color-primary-light-9);
  color: var(--aw-primary);
}

.dialog-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

/* ---------- 移动端 ---------- */
.mobile-bar {
  display: none;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 0 12px;
  background: var(--aw-doc-ink);
  color: #f4efe9;
}

.mobile-title {
  font-size: 14px;
  font-weight: 650;
}

.icon-btn {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border: none;
  border-radius: var(--aw-radius-s);
  background: transparent;
  color: rgb(244 239 233 / 0.75);
  cursor: pointer;
  text-decoration: none;
  transition: background-color var(--aw-dur) var(--aw-ease);
}

.icon-btn:hover {
  background: rgb(255 255 255 / 0.1);
}

.icon-btn .el-icon {
  font-size: 18px;
}

.drawer-body {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px 10px;
  height: 100%;
}

.drawer-body > :deep(.conv-list) {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.drawer-label {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--aw-text-3);
  padding: 4px 10px 2px;
}

@media (max-width: 900px) {
  .shell {
    grid-template-columns: 1fr;
    grid-template-rows: 52px 1fr;
  }

  .sidebar {
    display: none;
  }

  .mobile-bar {
    display: flex;
  }

  .main {
    padding: 10px;
  }

  /* 会话栏隐藏后，grid 的第一列仍然是 268px——不移掉这一列，
     消息区会被挤在左边 268px 里，右边留一条谁也不知道哪来的空白。 */
  .main-body.chat {
    grid-template-columns: minmax(0, 1fr);
  }

  .conv-panel {
    display: none;
  }
}
</style>

<!-- 弹窗被 append-to-body 挂到 body 下，scoped 选择器够不着，所以这一段是全局的。
     只改这一个类的内部留白与圆角，不改 el-dialog 的默认行为（遮罩、ESC、焦点圈都留着）。 -->
<style>
.new-chat-dialog {
  border-radius: var(--aw-radius-l);
  overflow: hidden;
}

.new-chat-dialog .el-dialog__header {
  margin-right: 0;
  padding: 20px 22px 6px;
}

.new-chat-dialog .el-dialog__body {
  padding: 10px 22px 6px;
}

.new-chat-dialog .el-dialog__footer {
  padding: 12px 22px 20px;
}

.new-chat-dialog .el-textarea__inner {
  padding: 12px 14px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-m);
  background: var(--aw-surface-2);
  box-shadow: none;
  font-family: inherit;
  font-size: 14px;
  line-height: 1.65;
  color: var(--aw-text);
  transition: border-color var(--aw-dur) var(--aw-ease), background-color var(--aw-dur) var(--aw-ease);
}

.new-chat-dialog .el-textarea__inner:focus {
  border-color: var(--aw-primary);
  background: var(--aw-surface);
  box-shadow: 0 0 0 3px rgb(79 70 229 / 0.1);
}
</style>

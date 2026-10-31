<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ChatLineSquare, FolderOpened, Menu, Setting } from '@element-plus/icons-vue'
import ConversationList from '@/components/ConversationList.vue'
import { useAgentStore } from '@/stores/agent'
import { useHealthStore } from '@/stores/health'

const agent = useAgentStore()
const health = useHealthStore()
const route = useRoute()
const router = useRouter()

const drawerOpen = ref(false)

const isChat = computed(() => route.path.startsWith('/chat'))
const activeNav = computed(() => {
  if (route.path.startsWith('/settings')) return '/settings'
  if (isChat.value) return '/chat'
  return '/'
})

const healthTip = computed(() => {
  const h = health.health
  if (h.error) return `后端异常：${h.error}`
  if (h.status === 'connected') return h.modelConfigured ? '后端正常 · 模型已配置' : '后端正常 · 模型未配置'
  return '正在检查后端…'
})

function onNewChat(): void {
  agent.newChat()
  drawerOpen.value = false
  if (route.path !== '/chat') void router.push('/chat')
}

onMounted(() => {
  void agent.restore()
  health.start()
})
</script>

<template>
  <div class="shell">
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
      </div>
    </aside>

    <!-- 移动端顶栏 -->
    <header class="mobile-bar">
      <button class="icon-btn" title="会话列表" @click="drawerOpen = true">
        <el-icon><Menu /></el-icon>
      </button>
      <span class="mobile-title">AgentWorkbench</span>
      <RouterLink to="/settings" class="icon-btn" title="设置">
        <el-icon><Setting /></el-icon>
      </RouterLink>
    </header>

    <el-drawer v-model="drawerOpen" direction="ltr" size="280px" :with-header="false">
      <div class="drawer-body">
        <button class="new-chat" @click="onNewChat">＋ 新对话</button>
        <div class="drawer-label">最近会话</div>
        <ConversationList />
      </div>
    </el-drawer>

    <main class="main">
      <div class="main-body" :class="{ chat: isChat }">
        <aside v-if="isChat" class="conv-panel">
          <button class="new-chat" @click="onNewChat">＋ 新对话</button>
          <ConversationList />
        </aside>
        <RouterView />
      </div>
    </main>
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
  transition: background-color var(--aw-dur) var(--aw-ease), color var(--aw-dur) var(--aw-ease);
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
  padding: 16px 18px;
}

.main-body {
  height: 100%;
  min-height: 0;
}

.main-body.chat {
  display: flex;
  gap: 16px;
}

.conv-panel {
  flex: none;
  width: 248px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px 12px;
  background: var(--aw-surface);
  border-radius: var(--aw-radius-l);
  box-shadow: var(--aw-shadow-1);
}

.conv-panel > :deep(.conv-list) {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.new-chat {
  padding: 9px;
  border: none;
  border-radius: var(--aw-radius-m);
  background: var(--aw-doc-ink-2);
  color: #fff;
  font-size: 13px;
  font-weight: 550;
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease);
}

.new-chat:hover {
  background: var(--aw-doc-ink);
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

  .conv-panel {
    display: none;
  }
}
</style>

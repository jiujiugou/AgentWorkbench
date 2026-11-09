<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { login } from '@/api/auth'
import { useAgentStore } from '@/stores/agent'

const route = useRoute()
const router = useRouter()
const agent = useAgentStore()

const email = ref('')
const password = ref('')
const busy = ref(false)

async function submit(): Promise<void> {
  if (!email.value.trim() || !password.value) {
    ElMessage.warning('输入用户名和密码')
    return
  }
  busy.value = true
  try {
    const user = await login(email.value.trim(), password.value)
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/'
    if (user.mustChangePassword) {
      // 先别 restore：业务请求此刻会被 403，徒增一串错误。
      ElMessage.warning('首次登录请先修改初始口令')
      void router.push({ path: '/change-password', query: { redirect } })
      return
    }
    // 登录成功后必须**重跑一次初始化**：整个 SPA 只在 App.vue 挂载时初始化一次，
    // 而那时用户还在登录页（所有请求都是 401）。不补这一句，会话列表与"当前模型"
    // 会一直空着，界面显示「未配置模型 · 去设置」——明明配好了，用户以为没配。
    // （D7 UI 验收抓到：登录后进对话页，模型标识一直停在"未配置模型"，刷新才正常。）
    await agent.restore()
    ElMessage.success(`你好，${user.displayName || user.userName}`)
    void router.push(redirect)
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="login-wrap">
    <form class="login-card" @submit.prevent="submit">
      <span class="brand-mark">AW</span>
      <h1>AgentWorkbench</h1>
      <p class="sub">登录后使用资料库与对话</p>
      <input
        v-model="email"
        type="text"
        placeholder="用户名"
        autocomplete="username"
        autocapitalize="off"
        spellcheck="false"
      />
      <input v-model="password" type="password" placeholder="密码" autocomplete="current-password" />
      <button type="submit" :disabled="busy">{{ busy ? '登录中…' : '登录' }}</button>
      <p class="hint">没有账号？由团队管理员发出邀请，用邀请链接建号。</p>
    </form>
  </div>
</template>

<style scoped>
.login-wrap {
  height: 100%;
  display: grid;
  place-items: center;
  background: var(--aw-doc-ink);
}

.login-card {
  width: 340px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 36px 32px 28px;
  background: var(--aw-surface);
  border-radius: 20px;
  box-shadow: var(--aw-shadow-3);
}

.brand-mark {
  display: grid;
  place-items: center;
  width: 46px;
  height: 46px;
  border-radius: 13px;
  background: var(--aw-doc-ink);
  color: #f4efe9;
  font-size: 15px;
  font-weight: 750;
}

h1 {
  font-size: 18px;
  font-weight: 700;
  color: var(--aw-text);
}

.sub {
  margin-bottom: 10px;
  font-size: 12.5px;
  color: var(--aw-text-3);
}

input {
  width: 100%;
  padding: 11px 14px;
  border: 1px solid var(--aw-border);
  border-radius: var(--aw-radius-full);
  font-size: 13.5px;
  outline: none;
  transition: border-color var(--aw-dur) var(--aw-ease);
}

input:focus {
  border-color: var(--aw-doc-ink-2);
}

button {
  width: 100%;
  margin-top: 6px;
  padding: 11px;
  border: none;
  border-radius: var(--aw-radius-full);
  background: var(--aw-doc-ink-2);
  color: #fff;
  font-size: 14px;
  font-weight: 600;
  cursor: pointer;
  transition: background-color var(--aw-dur) var(--aw-ease);
}

button:hover {
  background: var(--aw-doc-ink);
}

button:disabled {
  opacity: 0.6;
  cursor: default;
}

.hint {
  font-size: 11.5px;
  color: var(--aw-text-3);
}
</style>

<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { login } from '@/api/auth'

const route = useRoute()
const router = useRouter()

const userName = ref('')
const password = ref('')
const busy = ref(false)

async function submit(): Promise<void> {
  if (!userName.value.trim() || !password.value) {
    ElMessage.warning('输入用户名和密码')
    return
  }
  busy.value = true
  try {
    const user = await login(userName.value.trim(), password.value)
    ElMessage.success(`你好，${user.displayName || user.userName}`)
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/'
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
      <input v-model="userName" placeholder="用户名" autocomplete="username" />
      <input v-model="password" type="password" placeholder="密码" autocomplete="current-password" />
      <button type="submit" :disabled="busy">{{ busy ? '登录中…' : '登录' }}</button>
      <p class="hint">演示账号：wang / demo1234</p>
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

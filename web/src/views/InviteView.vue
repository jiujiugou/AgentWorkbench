<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { acceptInvite } from '@/api/auth'
import { useAgentStore } from '@/stores/agent'

const route = useRoute()
const router = useRouter()
const agent = useAgentStore()

const token = computed(() => String(route.params.token ?? ''))
const email = ref('')
const password = ref('')
const busy = ref(false)

async function submit(): Promise<void> {
  if (!token.value) {
    ElMessage.error('邀请链接无效')
    return
  }
  if (!email.value.trim() || password.value.length < 12) {
    ElMessage.warning('请填写邮箱，口令至少 12 位')
    return
  }
  busy.value = true
  try {
    const user = await acceptInvite(token.value, email.value.trim(), password.value)
    await agent.restore()
    ElMessage.success(`账号已创建：${user.email}`)
    void router.push('/')
  } catch (e) {
    ElMessage.error((e as Error).message)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="invite-wrap">
    <form class="invite-card" @submit.prevent="submit">
      <span class="brand-mark">AW</span>
      <h1>接受邀请</h1>
      <p class="sub">设置你的邮箱和口令，创建账号后直接进入工作台。</p>
      <input
        v-model="email"
        type="email"
        placeholder="邮箱"
        autocomplete="username"
        autocapitalize="off"
        spellcheck="false"
      />
      <input
        v-model="password"
        type="password"
        placeholder="口令（至少 12 位）"
        autocomplete="new-password"
      />
      <button type="submit" :disabled="busy">{{ busy ? '创建中…' : '创建账号' }}</button>
      <button type="button" class="link" @click="router.push('/login')">已有账号，去登录</button>
    </form>
  </div>
</template>

<style scoped>
.invite-wrap {
  height: 100%;
  display: grid;
  place-items: center;
  background: var(--aw-doc-ink);
}

.invite-card {
  width: 360px;
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
  text-align: center;
  line-height: 1.6;
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

button.link {
  padding: 2px;
  background: transparent;
  color: var(--aw-text-3);
  font-size: 12px;
  font-weight: 500;
}

button.link:hover {
  background: transparent;
  color: var(--aw-text);
}
</style>

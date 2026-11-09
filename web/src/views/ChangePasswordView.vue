<script setup lang="ts">
/**
 * 首次登录改密页（I-V19）。
 *
 * 这个页面在服务端判定之后才有意义：后端把业务接口关掉，前端只是把人送到
 * 正确的地方。**不要**用它替代后端闸门——直接调 API 的客户端必须同样 403。
 */
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { changePassword } from '@/api/auth'
import { useAgentStore } from '@/stores/agent'

const MIN_PASSWORD_LENGTH = 12

const route = useRoute()
const router = useRouter()
const agent = useAgentStore()

const oldPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const busy = ref(false)

async function submit(): Promise<void> {
  if (!oldPassword.value || !newPassword.value) {
    ElMessage.warning('请填写旧口令和新口令')
    return
  }
  if (newPassword.value.length < MIN_PASSWORD_LENGTH) {
    ElMessage.warning(`新口令至少 ${MIN_PASSWORD_LENGTH} 位`)
    return
  }
  if (newPassword.value !== confirmPassword.value) {
    ElMessage.warning('两次输入的新口令不一致')
    return
  }
  if (oldPassword.value === newPassword.value) {
    ElMessage.warning('新口令不能与当前口令相同')
    return
  }

  busy.value = true
  try {
    await changePassword(oldPassword.value, newPassword.value)
    ElMessage.success('口令已修改')
    // 标记已在服务端清掉；这里恢复业务数据，再回到登录前想去的页面。
    await agent.restore()
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
  <div class="change-wrap">
    <form class="change-card" @submit.prevent="submit">
      <span class="brand-mark">AW</span>
      <h1>修改初始口令</h1>
      <p class="sub">这是引导时设置的一次性口令，修改后才能使用资料库与对话。</p>

      <label>
        <span>当前口令</span>
        <input
          v-model="oldPassword"
          type="password"
          autocomplete="current-password"
          placeholder="引导时设置的口令"
        />
      </label>
      <label>
        <span>新口令</span>
        <input
          v-model="newPassword"
          type="password"
          autocomplete="new-password"
          :placeholder="`至少 ${MIN_PASSWORD_LENGTH} 位`"
        />
      </label>
      <label>
        <span>再输一次新口令</span>
        <input
          v-model="confirmPassword"
          type="password"
          autocomplete="new-password"
          placeholder="与新口令一致"
        />
      </label>

      <button type="submit" :disabled="busy">{{ busy ? '提交中…' : '确认修改' }}</button>
    </form>
  </div>
</template>

<style scoped>
.change-wrap {
  height: 100%;
  display: grid;
  place-items: center;
  background: var(--aw-doc-ink);
}

.change-card {
  width: 380px;
  display: flex;
  flex-direction: column;
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
  margin: 0 auto;
  border-radius: 13px;
  background: var(--aw-doc-ink);
  color: #f4efe9;
  font-size: 15px;
  font-weight: 750;
}

h1 {
  text-align: center;
  font-size: 18px;
  font-weight: 700;
  color: var(--aw-text);
}

.sub {
  margin-bottom: 6px;
  text-align: center;
  font-size: 12.5px;
  line-height: 1.6;
  color: var(--aw-text-3);
}

label {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

label span {
  font-size: 12px;
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
</style>

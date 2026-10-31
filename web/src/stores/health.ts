import { defineStore } from 'pinia'
import { ref } from 'vue'

export type BackendHealthStatus = 'connected' | 'disconnected' | 'connecting' | 'error'

export interface BackendHealthInfo {
  status: BackendHealthStatus
  lastCheck: Date | null
  error: string | null
  modelConfigured: boolean
}

/** 后端健康探测（/api/health，10s 轮询）。全局单例：侧栏页脚与设置页共用。 */
export const useHealthStore = defineStore('health', () => {
  const health = ref<BackendHealthInfo>({
    status: 'connecting',
    lastCheck: null,
    error: null,
    modelConfigured: false,
  })

  let timer: ReturnType<typeof setInterval> | null = null
  let ac: AbortController | null = null

  async function check(): Promise<void> {
    if (ac) ac.abort()
    ac = new AbortController()
    try {
      const response = await fetch('/api/health', {
        signal: ac.signal,
        cache: 'no-store',
      })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      const data = await response.json()
      health.value = {
        status: 'connected',
        lastCheck: new Date(),
        error: null,
        modelConfigured: data.modelConfigured ?? false,
      }
    } catch (e) {
      if ((e as Error).name === 'AbortError') return
      health.value = {
        status: 'error',
        lastCheck: new Date(),
        error: (e as Error).message,
        modelConfigured: false,
      }
    }
  }

  function start(): void {
    if (timer) return
    void check()
    timer = setInterval(() => void check(), 10000)
  }

  function stop(): void {
    if (timer) {
      clearInterval(timer)
      timer = null
    }
    if (ac) {
      ac.abort()
      ac = null
    }
  }

  return { health, check, start, stop }
})

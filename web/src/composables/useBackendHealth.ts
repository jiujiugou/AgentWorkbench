import { ref, onUnmounted } from 'vue'

export type BackendHealthStatus = 'connected' | 'disconnected' | 'connecting' | 'error'

export interface BackendHealthInfo {
  status: BackendHealthStatus
  lastCheck: Date | null
  error: string | null
  modelConfigured: boolean
}

export interface UseBackendHealthOptions {
  endpoint?: string
  intervalMs?: number
  immediate?: boolean
}

export function useBackendHealth(options: UseBackendHealthOptions = {}) {
  const {
    endpoint = '/api/health',
    intervalMs = 10000,
    immediate = true,
  } = options

  const health = ref<BackendHealthInfo>({
    status: 'connecting',
    lastCheck: null,
    error: null,
    modelConfigured: false,
  })

  let timer: ReturnType<typeof setInterval> | null = null
  let abortController: AbortController | null = null

  async function check(): Promise<void> {
    if (abortController) abortController.abort()
    abortController = new AbortController()

    health.value.status = 'connecting'

    try {
      const response = await fetch(endpoint, {
        signal: abortController.signal,
        cache: 'no-store',
      })

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`)
      }

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
    if (immediate) check()
    timer = setInterval(check, intervalMs)
  }

  function stop(): void {
    if (timer) {
      clearInterval(timer)
      timer = null
    }
    if (abortController) {
      abortController.abort()
      abortController = null
    }
  }

  function forceCheck(): void {
    check()
  }

  onUnmounted(() => {
    stop()
  })

  return {
    health,
    start,
    stop,
    forceCheck,
  }
}
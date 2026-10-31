/**
 * 身份 · /api/auth（Cookie 认证）
 * 登录成功后 Cookie 由浏览器自动携带（vite 代理同源）。
 */

export interface AuthUser {
  userId: string
  userName: string
  displayName: string
  role: string
  tenantId: string
  tenantName?: string | null
}

// 登录态缓存：null = 还没查过。登录/登出必须同步改它，否则路由守卫会用旧结果。
let signedIn: boolean | null = null
let pending: Promise<boolean> | null = null

export function cachedSignedIn(): Promise<boolean> {
  if (signedIn !== null) return Promise.resolve(signedIn)
  pending ??= fetchMe().then((u) => {
    signedIn = u !== null
    return signedIn
  })
  return pending
}

async function handle<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    let message = text
    try {
      message = (JSON.parse(text) as { message?: string }).message ?? text
    } catch {
      /* 原文返回 */
    }
    throw new Error(message || `HTTP ${response.status}`)
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export async function login(userName: string, password: string): Promise<AuthUser> {
  const user = await handle<AuthUser>(
    await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userName, password }),
    }),
  )
  signedIn = true
  return user
}

async function fetchMe(): Promise<AuthUser | null> {
  try {
    return await handle<AuthUser>(await fetch('/api/auth/me'))
  } catch {
    return null
  }
}

export async function logout(): Promise<void> {
  await fetch('/api/auth/logout', { method: 'POST' })
  signedIn = false
}

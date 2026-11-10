import { handle } from './http'

/**
 * 成员与账号（2026-11-10 改：直接建号，不走邀请）。
 *
 * v1 是「发邀请链接 → 对方自己设密码」。私有化部署里没有邮件通道，
 * 链接还得有人转发，实际用不起来。现在由管理员建号并当面交付初始口令。
 *
 * `levelRank`（账号等级 1–5）随知识库可见性一起移除：它只服务多知识库的
 * 阅读权限，那套概念已按产品裁决砍掉。角色仍只有 owner / admin / member。
 */
export interface Member {
  id: string
  /** 登录名。登录框填的是这个，不是邮箱。 */
  userName: string
  displayName: string
  email: string | null
  role: 'owner' | 'admin' | 'member' | string
  status: 'active' | 'disabled' | string
  tenantId: string
  createdAt: string
}

export interface CreateMemberInput {
  userName: string
  displayName: string
  email?: string | null
  password: string
  role: 'admin' | 'member'
}

export interface UpdateMemberInput {
  displayName?: string
  role?: 'admin' | 'member'
}

const jsonHeaders = { 'Content-Type': 'application/json' }

export const membersApi = {
  list: () => fetch('/api/members').then((r) => handle<Member[]>(r)),

  create: (input: CreateMemberInput) =>
    fetch('/api/members', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify(input),
    }).then((r) => handle<Member>(r)),

  update: (id: string, input: UpdateMemberInput) =>
    fetch(`/api/members/${id}`, {
      method: 'PATCH',
      headers: jsonHeaders,
      body: JSON.stringify(input),
    }).then((r) => handle<Member>(r)),

  disable: (id: string) =>
    fetch(`/api/members/${id}/disable`, { method: 'POST' }).then((r) => handle<void>(r)),

  enable: (id: string) =>
    fetch(`/api/members/${id}/enable`, { method: 'POST' }).then((r) => handle<void>(r)),
}

import { handle } from './http'

/** 成员与账号管理（REQ-03 / REQ-25）。角色与等级是两个独立轴。 */
export interface Member {
  id: string
  email: string
  role: 'admin' | 'member' | string
  status: 'active' | 'disabled' | string
  tenantId: string
  levelRank: number
}

export interface CreateMemberInput {
  email: string
  password: string
  role: 'admin' | 'member'
  levelRank: number
}

export interface UpdateMemberInput {
  role?: 'admin' | 'member'
  levelRank?: number
}

export interface InviteInput {
  role: 'admin' | 'member'
  email?: string | null
}

export interface InviteResult {
  inviteId: string
  token: string
}

export interface PendingInvite {
  inviteId: string
  email: string | null
  role: 'admin' | 'member' | string
  expiresAt: string
  createdAt: string
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

  resetPassword: (id: string, password: string) =>
    fetch(`/api/members/${id}/reset-password`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ password }),
    }).then((r) => handle<void>(r)),

  disable: (id: string) =>
    fetch(`/api/members/${id}/disable`, { method: 'POST' }).then((r) => handle<void>(r)),

  enable: (id: string) =>
    fetch(`/api/members/${id}/enable`, { method: 'POST' }).then((r) => handle<void>(r)),

  invite: (input: InviteInput) =>
    fetch('/api/members/invites', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify(input),
    }).then((r) => handle<InviteResult>(r)),

  listInvites: () => fetch('/api/members/invites').then((r) => handle<PendingInvite[]>(r)),

  revokeInvite: (id: string) =>
    fetch(`/api/members/invites/${id}`, { method: 'DELETE' }).then((r) => handle<void>(r)),
}

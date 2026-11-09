import { handle } from './http'

/** 管理员审计（ADR 0012）。只读；后端已按当前团队过滤。 */
export interface AuditEvent {
  eventId: string
  occurredAt: string
  actorUserId: string | null
  targetUserId: string | null
  action: string
  resourceKind: string | null
  resourceId: string | null
  oldValue: string | null
  newValue: string | null
  requestId: string | null
}

export interface AuditPage {
  events: AuditEvent[]
  nextCursor: string | null
}

export interface AuditQuery {
  limit?: number
  before?: string | null
  action?: string
  resourceKind?: string
  resourceId?: string
  actorUserId?: string
}

export async function listAuditEvents(query: AuditQuery = {}): Promise<AuditPage> {
  const params = new URLSearchParams()
  if (query.limit) params.set('limit', String(query.limit))
  if (query.before) params.set('before', query.before)
  if (query.action) params.set('action', query.action)
  if (query.resourceKind) params.set('resource_kind', query.resourceKind)
  if (query.resourceId) params.set('resource_id', query.resourceId)
  if (query.actorUserId) params.set('actor_user_id', query.actorUserId)
  const suffix = params.toString()
  return handle<AuditPage>(await fetch(`/api/admin/events${suffix ? `?${suffix}` : ''}`))
}

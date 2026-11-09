import { handle } from './http'

/** 知识库元数据（REQ-24）。文档继承知识库可见性，不单独设 ACL。 */
export interface KnowledgeBaseDto {
  id: string
  name: string
  visibility: 'public' | 'private' | string
  minLevelRank: number | null
  isDefault: boolean
  createdAt: string
  archivedAt?: string | null
}

export interface CreateKnowledgeBaseInput {
  name: string
  visibility: 'public' | 'private'
  minLevelRank?: number | null
}

export interface UpdateKnowledgeBaseInput {
  name?: string
  visibility?: 'public' | 'private'
  minLevelRank?: number | null
}

// C# 后端没有 /api/knowledge-bases（多库 / 可见性是 Python 侧 REQ-24）。
// 列表降级成一个默认库，让文档上传与查看不被这个不存在的元数据端点挡住；
// 下面的创建 / 修改 / 归档仍发真实请求，后端不支持时会如实报错，不假造成功。
const DEFAULT_KB: KnowledgeBaseDto = {
  id: 'default',
  name: '默认知识库',
  visibility: 'public',
  minLevelRank: null,
  isDefault: true,
  createdAt: new Date(0).toISOString(),
}

export async function listKnowledgeBases(): Promise<KnowledgeBaseDto[]> {
  return [{ ...DEFAULT_KB }]
}

export async function createKnowledgeBase(
  input: CreateKnowledgeBaseInput,
): Promise<KnowledgeBaseDto> {
  return handle<KnowledgeBaseDto>(
    await fetch('/api/knowledge-bases', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    }),
  )
}

export async function updateKnowledgeBase(
  id: string,
  input: UpdateKnowledgeBaseInput,
): Promise<KnowledgeBaseDto> {
  return handle<KnowledgeBaseDto>(
    await fetch(`/api/knowledge-bases/${id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    }),
  )
}

export function archiveKnowledgeBase(id: string): Promise<void> {
  return fetch(`/api/knowledge-bases/${id}/archive`, { method: 'POST' }).then((r) =>
    handle<void>(r),
  )
}

export function unarchiveKnowledgeBase(id: string): Promise<void> {
  return fetch(`/api/knowledge-bases/${id}/unarchive`, { method: 'POST' }).then((r) =>
    handle<void>(r),
  )
}

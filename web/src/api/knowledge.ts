export interface KnowledgeCapabilities {
  retrieve: boolean
  ask: boolean
  upload: boolean
  rerank: boolean
}

export interface KnowledgeStatus {
  provider: string
  capabilities: KnowledgeCapabilities
  embeddingConfigured: boolean
  embeddingProvider: string
  embeddingModel: string
  documentCount: number
  indexedChunkCount: number
}

export interface KnowledgeDocument {
  id: string
  fileName: string
  status: string
  pageCount: number
  chunkCount: number
  indexed: boolean
}

export interface RetrievalSource {
  chunkId: number
  documentId: string
  fileName: string
  seq: number
  heading?: string | null
  pageFrom: number
  pageTo: number
  vectorScore?: number | null
  textScore?: number | null
  fusedScore: number
  content: string
}

async function handle<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(text || `HTTP ${response.status}`)
  }
  return (await response.json()) as T
}

export const knowledgeApi = {
  health: () => fetch('/api/knowledge/health').then((r) => handle<KnowledgeStatus>(r)),
  documents: () => fetch('/api/knowledge/documents').then((r) => handle<KnowledgeDocument[]>(r)),
  reindex: (id: string) =>
    fetch(`/api/knowledge/documents/${id}/index`, { method: 'POST' }).then((r) => handle<unknown>(r)),
  retrieve: (query: string, topK: number) =>
    fetch('/api/knowledge/retrieve', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ query, topK }),
    }).then((r) => handle<RetrievalSource[]>(r)),
}

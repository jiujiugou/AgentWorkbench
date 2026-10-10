export interface Conversation {
  id: string
  title: string
  createdAt: string
  updatedAt: string
}

export interface ConversationMessage {
  id: number
  role: 'user' | 'assistant'
  content: string
  createdAt: string
}

async function handle<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(text || `HTTP ${response.status}`)
  }
  return (await response.json()) as T
}

export const conversationsApi = {
  list: () => fetch('/api/conversations').then((r) => handle<Conversation[]>(r)),
  create: (title?: string) =>
    fetch('/api/conversations', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title }),
    }).then((r) => handle<Conversation>(r)),
  messages: (id: string) =>
    fetch(`/api/conversations/${id}/messages`).then((r) => handle<ConversationMessage[]>(r)),
  remove: (id: string) => fetch(`/api/conversations/${id}`, { method: 'DELETE' }),
}

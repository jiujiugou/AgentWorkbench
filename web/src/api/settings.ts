export interface ProviderModel {
  id: number
  providerId: number
  model: string
  displayName?: string | null
  isDefault: boolean
}

export interface Provider {
  id: number
  name: string
  baseUrl: string
  apiKeyMasked: string
  hasApiKey: boolean
  isActive: boolean
  updatedAt: string
  models: ProviderModel[]
}

export interface SettingsPreview {
  baseUrl: string
  model: string
  embeddingModel: string
  temperature: number
  systemPrompt: string
  apiKeyConfigured: boolean
  configured: boolean
  activeProviderId?: number | null
}

export interface ProviderInput {
  name?: string
  baseUrl: string
  apiKey?: string
}

export interface TestResult {
  ok: boolean
  latencyMs: number
  count?: number
  error?: string
}

const jsonHeaders = { 'Content-Type': 'application/json' }

function parseError(text: string): string {
  if (!text) return ''
  try {
    const obj = JSON.parse(text) as { message?: string }
    return obj.message ?? text
  } catch {
    return text
  }
}

async function handle<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(parseError(text) || `HTTP ${response.status}`)
  }
  if (response.status === 204) return undefined as T
  const text = await response.text()
  if (!text) return undefined as T
  return JSON.parse(text) as T
}

export const settingsApi = {
  preview: () => fetch('/api/settings').then((r) => handle<SettingsPreview>(r)),

  listProviders: () => fetch('/api/settings/providers').then((r) => handle<Provider[]>(r)),

  createProvider: (input: ProviderInput) =>
    fetch('/api/settings/providers', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify(input),
    }).then((r) => handle<Provider>(r)),

  updateProvider: (id: number, input: ProviderInput) =>
    fetch(`/api/settings/providers/${id}`, {
      method: 'PUT',
      headers: jsonHeaders,
      body: JSON.stringify(input),
    }).then((r) => handle<Provider>(r)),

  deleteProvider: (id: number) =>
    fetch(`/api/settings/providers/${id}`, { method: 'DELETE' }).then((r) => handle<void>(r)),

  activateProvider: (id: number) =>
    fetch(`/api/settings/providers/${id}/activate`, { method: 'POST' }).then((r) => handle<unknown>(r)),

  addModel: (providerId: number, model: string, displayName?: string) =>
    fetch(`/api/settings/providers/${providerId}/models`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ model, displayName }),
    }).then((r) => handle<ProviderModel>(r)),

  deleteModel: (modelId: number) =>
    fetch(`/api/settings/models/${modelId}`, { method: 'DELETE' }).then((r) => handle<void>(r)),

  setDefaultModel: (providerId: number, modelId: number) =>
    fetch(`/api/settings/providers/${providerId}/models/${modelId}/default`, { method: 'PUT' }).then((r) =>
      handle<unknown>(r),
    ),

  fetchModels: (baseUrl: string, apiKey: string) =>
    fetch('/api/settings/fetch-models', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ baseUrl, apiKey }),
    }).then((r) => handle<{ models: string[] }>(r)),

  fetchModelsForProvider: (id: number) =>
    fetch(`/api/settings/providers/${id}/fetch-models`, { method: 'POST' }).then((r) =>
      handle<{ models: string[] }>(r),
    ),

  testProvider: (id: number) =>
    fetch(`/api/settings/providers/${id}/test`, { method: 'POST' }).then((r) => handle<TestResult>(r)),
}

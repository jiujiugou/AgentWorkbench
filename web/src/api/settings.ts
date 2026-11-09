import { handle } from './http'

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
  // Python 版没有 temperature 这个旋钮：后端恒返回 null，界面显示「不适用」
  temperature: number | null
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


export const settingsApi = {
  preview: () => fetch('/api/settings').then((r) => handle<SettingsPreview>(r)),

  // C# SettingsController.ListProviders 返回裸数组；Python 侧才是 {providers:[...]} 包装。
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

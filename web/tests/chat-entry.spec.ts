/**
 * F7 入口状态（D-V2 / D-V3 的界面一侧）：入口必须跟当前会话的 `kind` 走。
 * 路由切回或重开页面时，知识库会话不能被显示成通用入口；没有会话时，
 * 默认入口是知识库问答，而不是通用对话。
 */

import { shallowMount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }))

import ChatView from '@/views/ChatView.vue'
import { useAgentStore } from '@/stores/agent'
import type { Conversation } from '@/api/conversations'

function conversation(id: string, kind: 'knowledge' | 'general'): Conversation {
  return { id, title: id, kind, createdAt: '', updatedAt: '' }
}

function mountView() {
  return shallowMount(ChatView, {
    global: {
      stubs: {
        ChatMessage: true,
        'el-icon': true,
        'el-select': true,
        'el-option': true,
        'el-option-group': true,
        'el-button': true,
      },
    },
  })
}

beforeEach(() => {
  setActivePinia(createPinia())
  Element.prototype.scrollTo = vi.fn()
})

describe('ChatView · F7 入口', () => {
  it('test_defaults_to_knowledge_entry', () => {
    const wrapper = mountView()

    expect(wrapper.find('.mode-badge').text()).toContain('知识库问答')
    expect(wrapper.find('.mode-badge').text()).not.toContain('未使用企业资料')
  })

  it('test_general_conversation_remount_keeps_general_entry', () => {
    const store = useAgentStore()
    store.conversations = [conversation('g1', 'general')]
    store.conversationId = 'g1'

    const wrapper = mountView()

    expect(wrapper.find('.mode-badge').text()).toContain('未使用企业资料')
  })

  it('test_knowledge_conversation_remount_keeps_knowledge_entry', () => {
    const store = useAgentStore()
    store.conversations = [conversation('k1', 'knowledge')]
    store.conversationId = 'k1'

    const wrapper = mountView()

    expect(wrapper.find('.mode-badge').text()).toContain('知识库问答')
    expect(wrapper.find('.mode-badge').text()).not.toContain('未使用企业资料')
  })
})

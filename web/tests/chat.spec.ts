/**
 * F7 界面契约（D-V2）：两种答案必须一眼可分。
 *
 * 这一层测的是“用户看到什么”，不是接口字段。把通用回答渲染成普通气泡，
 * 或者只在接口里放 mode 而界面不显示，都会让用户把模型常识当成企业原文。
 */

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import ChatMessage from '@/components/ChatMessage.vue'

function mountAnswer(mode: 'knowledge' | 'general') {
  return mount(ChatMessage, {
    props: {
      message: { id: 'm1', role: 'assistant', content: '这是一段回答。' },
      mode,
    },
    global: {
      stubs: {
        'el-icon': true,
        MagicStick: true,
        CopyDocument: true,
        RunTrace: true,
      },
    },
  })
}

describe('ChatMessage · 通用/知识库标识', () => {
  it('test_general_answer_is_labelled_as_not_using_company_data', () => {
    const wrapper = mountAnswer('general')

    expect(wrapper.text()).toContain('未使用企业资料')
  })

  it('test_knowledge_answer_does_not_carry_the_general_label', () => {
    const wrapper = mountAnswer('knowledge')

    expect(wrapper.text()).not.toContain('未使用企业资料')
  })
})

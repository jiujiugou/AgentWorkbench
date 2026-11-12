/**
 * 渲染层断言：轨迹这一块**画出来的是什么**（C-V5）。
 *
 * 为什么读侧的对齐不够：后端已经验过"步骤按 seq 返回、长输出不截断、
 * in-flight 与返回空分开"，但那一层对了，界面上仍可能把它们合成一句话——
 * 用户看到的不是数据，是这四个字的标签。所以这里 mount 真组件，断言文本。
 *
 * 五条各自钉一个失效形态：
 * - 顺序被 v-for 的 key 或排序改掉 → 回放时"生成"在"检索"前面
 * - 失败步把 error 藏起来只显示返回 → 用户以为成功了
 * - "发出去了没回来"写成"返回了空" → 排查方向从"这次调用"被带偏到"工具本身"
 * - 四段流水线说成"1 轮迭代" → 用户以为模型在反复调工具（它一次都没选过）
 * - 拒答被画成原样状态码（或干脆没标签） → "资料里没有"被当成系统故障
 */

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import RunTrace from '@/components/RunTrace.vue'
import { toToolSteps, type Trace } from '@/api/conversations'
import type { RunSummary, ToolStepEvent } from '@/types'

function step(overrides: Partial<ToolStepEvent> = {}): ToolStepEvent {
  return {
    iteration: 0,
    name: '检索知识库',
    arguments: '{"query":"E2 报警"}',
    resultPreview: '{"top":[{"seq":12}]}',
    error: null,
    ...overrides,
  }
}

function run(overrides: Partial<RunSummary> = {}): RunSummary {
  return {
    runId: 'run-1234abcd',
    iterations: 1,
    pipeline: true,
    model: 'glm-4-flash',
    latencyMs: 1200,
    status: 'succeeded',
    code: null,
    steps: 1,
    tools: 1,
    ...overrides,
  }
}

/** 图标与 el-icon 都 stub 掉：断言的是文本与结构，不是图标渲染。 */
function mountTrace(props: { tools?: ToolStepEvent[]; run?: RunSummary }) {
  return mount(RunTrace, {
    props,
    global: {
      stubs: {
        'el-icon': true,
        Cpu: true,
        ArrowRight: true,
        Warning: true,
      },
    },
  })
}

async function openBody(wrapper: ReturnType<typeof mountTrace>) {
  await wrapper.find('.trace-head').trigger('click')
}

describe('RunTrace', () => {
  it('test_every_step_is_rendered_in_order', async () => {
    const wrapper = mountTrace({
      tools: [
        step({ name: '检索知识库' }),
        step({ name: '生成回答', arguments: null, resultPreview: '通讯超时，检查波特率[1]。' }),
      ],
      run: run({ steps: 2 }),
    })
    await openBody(wrapper)

    const names = wrapper.findAll('.step-name').map((node) => node.text())
    expect(names).toEqual(['检索知识库', '生成回答'])
    // 序号由渲染顺序给出：回放时不能出现 #2 在 #1 上面
    expect(wrapper.findAll('.step-idx').map((node) => node.text())).toEqual(['#1', '#2'])
  })

  it('test_failed_step_shows_error_instead_of_result', async () => {
    const wrapper = mountTrace({
      tools: [
        step({
          name: '生成回答',
          resultPreview: '这段返回不该出现',
          error: 'TimeoutError: model timeout',
        }),
      ],
      run: run({ status: 'failed' }),
    })
    await openBody(wrapper)

    // 失败步显示"异常"，且**不再显示返回**：两个都显示会让人以为它一半成功
    expect(wrapper.find('.step-row .step-v.error').text()).toContain('TimeoutError')
    expect(wrapper.text()).not.toContain('这段返回不该出现')
    expect(wrapper.find('.step').classes()).toContain('failed')
  })

  it('test_in_flight_step_reads_as_no_return_not_empty', () => {
    // "发出去了没回来"在数据里是 output 与 error 都为空（三态之一）。
    // 翻译发生在 toToolSteps：它必须给一句话，而不是留给界面显示空白。
    const trace: Trace = {
      runId: 'run-1',
      question: 'E2 报警怎么处理？',
      status: 'cancelled',
      reason: 'run_timeout',
      model: 'glm-4-flash',
      promptVersion: 'abc123',
      startedAt: '2026-11-03T09:00:00',
      finishedAt: null,
      durationMs: null,
      promptTokens: null,
      completionTokens: null,
      citationOutOfRange: null,
      citationUnsupported: null,
      error: null,
      steps: [
        {
          seq: 1,
          kind: 'retrieval',
          toolName: 'search_knowledge',
          input: '{"query":"E2"}',
          output: null,
          error: null,
          model: null,
          durationMs: null,
          promptTokens: null,
          completionTokens: null,
          createdAt: '2026-11-03T09:00:01',
          inFlight: true,
        },
      ],
    }

    const [translated] = toToolSteps(trace)

    expect(translated.resultPreview).toBeNull()
    expect(translated.error).toContain('没有返回')
    // 与"工具返回了空串"不是一回事：后者不该有这句
    expect(translated.error).not.toContain('空')
  })

  it('test_pipeline_does_not_say_n_rounds', () => {
    const wrapper = mountTrace({ tools: [step()], run: run({ pipeline: true }) })

    expect(wrapper.find('.trace-metrics').text()).toContain('四段流水线')
    expect(wrapper.text()).not.toContain('轮迭代')
  })

  it('test_refused_run_is_labelled_as_refusal', () => {
    const wrapper = mountTrace({ tools: [step()], run: run({ status: 'refused', code: 'knowledge_insufficient' }) })

    const badge = wrapper.find('.trace-status')
    expect(badge.text()).toBe('拒答（资料不足）')
    // 不许把状态码原样吐给用户：`refused` 是给程序看的，不是给读者看的
    expect(badge.text()).not.toBe('refused')
    expect(badge.classes()).toContain('warn')
  })
})

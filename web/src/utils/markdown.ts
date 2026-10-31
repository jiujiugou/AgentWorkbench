import MarkdownIt from 'markdown-it'
// 按需注册常用语言，避免全量语言包（~700kB）进包
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import c from 'highlight.js/lib/languages/c'
import cpp from 'highlight.js/lib/languages/cpp'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import go from 'highlight.js/lib/languages/go'
import java from 'highlight.js/lib/languages/java'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import python from 'highlight.js/lib/languages/python'
import rust from 'highlight.js/lib/languages/rust'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'

hljs.registerLanguage('bash', bash)
hljs.registerLanguage('shell', bash)
hljs.registerLanguage('c', c)
hljs.registerLanguage('cpp', cpp)
hljs.registerLanguage('csharp', csharp)
hljs.registerLanguage('cs', csharp)
hljs.registerLanguage('css', css)
hljs.registerLanguage('diff', diff)
hljs.registerLanguage('go', go)
hljs.registerLanguage('java', java)
hljs.registerLanguage('javascript', javascript)
hljs.registerLanguage('js', javascript)
hljs.registerLanguage('json', json)
hljs.registerLanguage('markdown', markdown)
hljs.registerLanguage('md', markdown)
hljs.registerLanguage('python', python)
hljs.registerLanguage('py', python)
hljs.registerLanguage('rust', rust)
hljs.registerLanguage('rs', rust)
hljs.registerLanguage('sql', sql)
hljs.registerLanguage('typescript', typescript)
hljs.registerLanguage('ts', typescript)
hljs.registerLanguage('xml', xml)
hljs.registerLanguage('html', xml)
hljs.registerLanguage('yaml', yaml)
hljs.registerLanguage('yml', yaml)

const md = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
})

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

/**
 * 代码块渲染为带语言头 + 复制按钮的容器：
 * <div class="code-block"><div class="code-head">…</div><pre class="hljs"><code>…</code></pre></div>
 * 复制按钮通过事件委托处理（见 ChatMessage.vue）。
 */
md.options.highlight = (str: string, lang: string): string => {
  const known = lang && hljs.getLanguage(lang)
  const label = known ? lang : 'text'
  let inner: string
  if (known) {
    try {
      inner = hljs.highlight(str, { language: lang }).value
    } catch {
      inner = escapeHtml(str)
    }
  } else {
    inner = escapeHtml(str)
  }
  return (
    `<div class="code-block">` +
    `<div class="code-head"><span class="code-lang">${escapeHtml(label)}</span>` +
    `<button type="button" class="code-copy">复制</button></div>` +
    `<pre class="hljs"><code>${inner}</code></pre></div>`
  )
}

export function renderMarkdown(text: string): string {
  return md.render(text ?? '')
}

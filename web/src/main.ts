import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import 'highlight.js/styles/github.css'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { installSessionExpiry } from './session'
import './style.css'

const app = createApp(App)
app.use(createPinia())
app.use(router)
app.use(ElementPlus)

// 会话失效的处理装一次（见 src/session.ts）。
installSessionExpiry(router)

app.mount('#app')

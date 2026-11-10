import { fileURLToPath, URL } from 'node:url'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    // 产物直接进后端的静态目录，`npm run build` 即发布。
    //
    // 此前输出到 web/dist，而后端 UseStaticFiles() 服务的是 wwwroot——那个目录
    // 根本不存在。结果是「构建通过」和「浏览器里看到新界面」之间一直缺一步：
    // 只能用 npm run dev 起 5173 才能看到改动，走 8080 永远是空的。
    outDir: '../src/Host/AgentWorkbench.Webapi/wwwroot',
    // outDir 在项目根之外，vite 默认不敢清空，显式允许。
    // 这里只放构建产物，没有手写文件，清空是安全的。
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:8080', changeOrigin: true },
      '/hubs': { target: 'http://localhost:8080', changeOrigin: true, ws: true },
    },
  },
})

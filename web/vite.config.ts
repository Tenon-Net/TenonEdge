import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { bundleBudget } from './bundle-budget'

export default defineConfig({
    plugins: [react(), tailwindcss(), bundleBudget()],
    resolve: {
        alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
    },
    build: { target: ['chrome111', 'edge111', 'safari16.4', 'firefox128'] },
    server: { proxy: { '/api': 'http://127.0.0.1:18081' } },
})

import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Dev-server proxy. The browser never resolves Docker service names, so the
// app always calls relative /api paths and the proxy forwards them.
export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.AGENT_API_URL ?? 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
})

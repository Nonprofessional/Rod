import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The operator UI is built by Vite (dev: `npm run dev`) and served, in
// production, as static files from the teamserver host's wwwroot. So:
//  - dev: Vite serves the SPA and proxies the API back to the teamserver
//    (http://localhost:5080, see launchSettings.json) -- same origin from the
//    browser's view, no CORS.
//  - build: emit into ../wwwroot so `dotnet run` serves the built bundle at /.
const apiTarget = process.env.ROD_API_TARGET ?? 'http://localhost:5080'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // Relative base so the bundle resolves wherever the host mounts it.
  base: './',
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/engagements': apiTarget,
      '/implants': apiTarget,
      '/health': apiTarget,
    },
  },
})

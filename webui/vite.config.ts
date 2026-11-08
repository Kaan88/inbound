import { defineConfig } from 'vitest/config';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';

// The app: index.html into dist/. The sign-in page is a separate build (vite.signin.config.ts) into dist/signin/,
// because the server serves only that folder before sign-in. Nothing here runs at the server's run time.
const server = process.env.INBOUND_SERVER ?? 'http://127.0.0.1:8080';

export default defineConfig({
  plugins: [tailwindcss(), svelte()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    assetsDir: 'assets',
    sourcemap: false,
    target: 'es2022',
  },
  server: {
    // npm run dev against a running server: the API and the WebSocket go to it.
    proxy: {
      '/api': { target: server, ws: true, changeOrigin: false },
      '/health': server,
    },
  },
  test: {
    include: ['tests/**/*.test.ts'],
    environment: 'node',
  },
});

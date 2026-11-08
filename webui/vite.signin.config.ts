import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import { resolve } from 'node:path';

// The sign-in and setup page: everything it needs lives under dist/signin/, the only files the server sends before
// sign-in (besides /favicon.svg and /health).
export default defineConfig({
  root: resolve(import.meta.dirname, 'signin'),
  base: '/signin/',
  publicDir: false,
  plugins: [tailwindcss(), svelte({ configFile: resolve(import.meta.dirname, 'svelte.config.js') })],
  build: {
    outDir: resolve(import.meta.dirname, 'dist/signin'),
    emptyOutDir: true,
    assetsDir: 'assets',
    sourcemap: false,
    target: 'es2022',
  },
});

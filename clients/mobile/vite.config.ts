import { resolve } from 'node:path';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
      '@codexflow-shared': resolve(__dirname, '../shared'),
    },
  },
  server: {
    fs: {
      allow: [
        __dirname,
        resolve(__dirname, '../shared'),
        resolve(__dirname, '../../tests/vectors'),
      ],
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
  },
});

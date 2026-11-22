import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
      '@codexflow-shared': resolve(__dirname, '../shared'),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: [resolve(__dirname, 'tests/setup.ts')],
    include: ['tests/unit/**/*.test.{ts,tsx}'],
  },
});

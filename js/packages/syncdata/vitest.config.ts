import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
    exclude: ['test/lynxsim/**', 'test/interop/**'],
    environment: 'node',
    testTimeout: 30000,
  },
});

import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

const syncdata = (path: string) => fileURLToPath(new URL(`../syncdata/src/${path}`, import.meta.url));

// Tests run against @g9tm/syncdata's SOURCES (no build step between the two packages).
export default defineConfig({
  resolve: {
    alias: [{ find: /^@g9tm\/syncdata$/, replacement: syncdata('index.ts') }],
  },
  test: {
    include: ['test/**/*.test.ts'],
    exclude: ['test/lynxsim/**', 'test/interop/**'],
    environment: 'node',
    testTimeout: 30000,
  },
});

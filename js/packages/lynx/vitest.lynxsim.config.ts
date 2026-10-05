import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

const syncdata = (path: string) => fileURLToPath(new URL(`../syncdata/src/${path}`, import.meta.url));

// `npm run test:lynxsim`: the interop scenario bundled for Lynx and run in the Lynx-shaped sandbox against the real .NET
// sample server (needs the disposable PostgreSQL, G9SYNC_TEST_PG; skipped without it).
export default defineConfig({
  resolve: {
    alias: [
      { find: /^@g9tm\/syncdata$/, replacement: syncdata('index.ts') },
      { find: /^@g9tm\/syncdata\/signalr$/, replacement: syncdata('signalr/index.ts') },
      { find: /^@g9tm\/syncdata\/sqlite$/, replacement: syncdata('sqlite/index.ts') },
    ],
  },
  test: {
    include: ['test/lynxsim/**/*.test.ts'],
    environment: 'node',
    testTimeout: 900000,
    hookTimeout: 600000,
    fileParallelism: false,
  },
});

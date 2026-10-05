import { defineConfig } from 'vitest/config';

// `npm run test:interop`: the TypeScript client against the real .NET sample server (samples/G9SyncData.Samples.Server) on
// the disposable PostgreSQL (G9SYNC_TEST_PG, default localhost:15433), over both hub protocols, plus the comparison of the
// database it writes with the one the .NET sample client writes. Skipped when no PostgreSQL answers.
export default defineConfig({
  test: {
    include: ['test/interop/**/*.test.ts'],
    environment: 'node',
    testTimeout: 900000,
    hookTimeout: 600000,
    fileParallelism: false,
  },
});

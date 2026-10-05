import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { pluginReactLynx } from '@lynx-js/react-rsbuild-plugin';
import { defineConfig } from '@lynx-js/rspeedy';

const here = dirname(fileURLToPath(import.meta.url));
// The interop scenario is the @g9tm/syncdata test file, imported from the repository by path. It imports the engine
// from the package SOURCES ('../../src/…'); here those requests go to the PACKED package in node_modules instead
// (dist mirrors src file by file), so the scenario, @g9tm/syncdata-lynx and the app share one copy of the engine —
// one default frame decompressor (useLynxBrotli) and the code that ships.
const interopDir = resolve(here, '..', '..', 'packages', 'syncdata', 'test', 'interop').toLowerCase();
const syncdataDist = join(here, 'node_modules', '@g9tm', 'syncdata', 'dist');

export default defineConfig({
  source: { entry: './src/index.tsx' },
  plugins: [pluginReactLynx()],
  output: { minify: false },
  tools: {
    rspack: (config, { rspack }) => {
      config.plugins ??= [];
      config.plugins.push(
        new rspack.NormalModuleReplacementPlugin(/^\.\.\/\.\.\/src\//, (data) => {
          if (!data.context || resolve(data.context).toLowerCase() !== interopDir) return;
          data.request = join(syncdataDist, data.request.slice('../../src/'.length));
        }),
      );
      return config;
    },
  },
});

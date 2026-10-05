// A Node stand-in for Lynx's background thread, for tests (the same file lives in G9SyncData/js/scripts/).
//
// Bundles an entry module with esbuild (ES2019, like Lynx's own build) and runs it in a fresh `node:vm` context that
// has only what the background thread has (G9LynxControls plan/specs/02-Lynx-Platform-Rules.md §1, §5):
//   - the ECMAScript built-ins, WITHOUT BigInt, Intl, WebAssembly, SharedArrayBuffer and Atomics;
//   - no TextEncoder/TextDecoder, URL, Blob, btoa/atob, crypto, performance, queueMicrotask, WebSocket, structuredClone;
//   - `setTimeout`/`clearTimeout`/`setInterval`/`clearInterval`, `lynx` and (optionally) `fetch` as identifiers of the
//     bundle's wrapper function, NOT properties of `globalThis` (`globalThis.setTimeout` is undefined, as on a device);
//   - `AbortController`/`AbortSignal` and `console` on `globalThis`;
//   - `NativeModules`: host implementations behind a bridge that copies every value into the sandbox realm, the way
//     Lynx's bridge creates fresh JS values (so code that relies on `instanceof` of a host object fails here too).
//
// The entry module's default export is called with `host` (functions the test provides) and its resolved value is
// copied back to the test. What this cannot prove (PrimJS's own quirks, bridge latency, device threading) belongs to a
// device run.
import { build } from 'esbuild';
import vm from 'node:vm';

const REMOVED_BUILTINS = ['BigInt', 'BigInt64Array', 'BigUint64Array', 'Intl', 'WebAssembly', 'SharedArrayBuffer', 'Atomics'];

/** Bundles `entry` (TS/JS, ESM) into an IIFE whose default export is `__g9Entry.default`. */
export async function bundleForLynx(entry, { define = {}, alias = {} } = {}) {
  const result = await build({
    entryPoints: [entry],
    bundle: true,
    write: false,
    format: 'iife',
    globalName: '__g9Entry',
    platform: 'neutral',
    target: 'es2019',
    mainFields: ['module', 'main'],
    conditions: ['import', 'default'],
    define: { 'process.env.NODE_ENV': '"production"', ...define },
    alias,
    logLevel: 'silent',
    legalComments: 'none',
  });
  return result.outputFiles[0].text;
}

// Runs inside the sandbox: turns host-realm values into sandbox-realm values (the bridge's copy semantics).
const PRELUDE = `
(function () {
  var g = globalThis;
  function copyIn(value) {
    if (value === null || value === undefined) return value === undefined ? undefined : null;
    var t = typeof value;
    if (t === 'string' || t === 'number' || t === 'boolean') return value;
    if (t === 'function') return value;
    var tag = Object.prototype.toString.call(value);
    if (tag === '[object ArrayBuffer]') { var src = new g.__g9HostUint8(value); var out = new Uint8Array(src.length); for (var i = 0; i < src.length; i++) out[i] = src[i]; return out.buffer; }
    if (tag === '[object Uint8Array]' || tag === '[object Int8Array]' || tag === '[object Uint8ClampedArray]') {
      var bytes = new g.__g9HostUint8(value.buffer, value.byteOffset, value.byteLength); var copy = new Uint8Array(bytes.length); for (var j = 0; j < bytes.length; j++) copy[j] = bytes[j]; return copy.buffer;
    }
    if (Array.isArray(value)) { var arr = []; for (var k = 0; k < value.length; k++) arr.push(copyIn(value[k])); return arr; }
    var obj = {}; var keys = Object.keys(value); for (var m = 0; m < keys.length; m++) obj[keys[m]] = copyIn(value[keys[m]]); return obj;
  }
  function wrapModule(hostModule) {
    var mod = {};
    Object.keys(hostModule).forEach(function (name) {
      var fn = hostModule[name];
      if (typeof fn !== 'function') { mod[name] = copyIn(fn); return; }
      mod[name] = function () {
        var args = [];
        for (var i = 0; i < arguments.length; i++) {
          var a = arguments[i];
          args.push(typeof a === 'function' ? (function (cb) { return function () { var cargs = []; for (var j = 0; j < arguments.length; j++) cargs.push(copyIn(arguments[j])); return cb.apply(null, cargs); }; })(a) : a);
        }
        return copyIn(fn.apply(hostModule, args));
      };
    });
    return mod;
  }
  var modules = {};
  var host = g.__g9HostNativeModules || {};
  Object.keys(host).forEach(function (name) { modules[name] = wrapModule(host[name]); });
  g.NativeModules = modules;
  g.__g9CopyIn = copyIn;
  delete g.__g9HostNativeModules;
})();
`;

/**
 * Runs a bundle in a Lynx-shaped context.
 * @param {object} options
 * @param {string} options.code - output of {@link bundleForLynx}
 * @param {Record<string, object>} [options.nativeModules] - host implementations exposed as `NativeModules.<name>`
 * @param {Record<string, unknown>} [options.host] - passed to the entry's default export
 * @param {typeof fetch} [options.fetch] - exposed as the wrapper identifier `fetch` (Lynx's fetch is core)
 * @param {number} [options.timeoutMs]
 */
export async function runInLynxSandbox({ code, nativeModules = {}, host = {}, fetch, timeoutMs = 120000 }) {
  const context = vm.createContext({
    console,
    AbortController,
    AbortSignal,
    __g9HostUint8: Uint8Array,
    __g9HostNativeModules: nativeModules,
    SystemInfo: { platform: 'lynxsim', lynxSdkVersion: '4.1.0' },
  });
  vm.runInContext(REMOVED_BUILTINS.map((name) => `delete globalThis.${name};`).join('\n'), context);
  vm.runInContext(PRELUDE, context);

  const timers = new Set();
  const track = (handle) => (timers.add(handle), handle);
  const setTimeoutWrapped = (fn, ms, ...args) => track(setTimeout(fn, ms, ...args));
  const setIntervalWrapped = (fn, ms, ...args) => track(setInterval(fn, ms, ...args));
  const lynx = {
    reportError: (error) => console.error('[lynx.reportError]', error),
    getJSModule: () => undefined,
  };

  // The bundle runs inside a wrapper whose parameters are the runtime's scoped identifiers, like a Lynx bundle.
  const wrapper = vm.runInContext(
    `(function (setTimeout, clearTimeout, setInterval, clearInterval, lynx, fetch) {\n${code}\n;return __g9Entry;\n})`,
    context,
    { filename: 'lynx-bundle.js' },
  );
  const entry = wrapper(setTimeoutWrapped, clearTimeout, setIntervalWrapped, clearInterval, lynx, fetch);
  if (!entry || typeof entry.default !== 'function') throw new Error('The sandbox entry must default-export a function.');

  let timer;
  try {
    const result = await Promise.race([
      entry.default(context.__g9CopyIn(host)),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`The sandbox run did not finish within ${timeoutMs} ms.`)), timeoutMs);
      }),
    ]);
    return result === undefined ? undefined : JSON.parse(JSON.stringify(result));
  } finally {
    clearTimeout(timer);
    for (const handle of timers) {
      clearTimeout(handle);
      clearInterval(handle);
    }
  }
}

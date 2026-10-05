declare module '*/scripts/lynx-sandbox.mjs' {
  export function bundleForLynx(entry: string, options?: { define?: Record<string, string>; alias?: Record<string, string> }): Promise<string>;
  export function runInLynxSandbox(options: {
    code: string;
    nativeModules?: Record<string, object>;
    host?: Record<string, unknown>;
    fetch?: typeof fetch;
    timeoutMs?: number;
  }): Promise<unknown>;
}

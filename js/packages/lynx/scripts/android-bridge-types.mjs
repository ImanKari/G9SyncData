// Lynx's Android bridge (LynxMethodWrapper) accepts only Callback, Promise, ReadableMap/ReadableArray, Dynamic, byte[],
// String, primitives and their boxes as @LynxMethod parameters. @lynx-js/autolink-codegen 0.6.0 writes `Object` for
// every function, object, array and ArrayBuffer parameter, and ONE such parameter leaves the whole module without methods
// in JS ("Got unknown param class: Object" in logcat; found on the emulator, G9SyncData LES-0050).
//
//   node scripts/android-bridge-types.mjs --fix   rewrites the generated Java specs from types/*.d.ts (`npm run codegen`
//                                                 runs it after the generator, so regenerating cannot bring `Object` back)
//   node scripts/android-bridge-types.mjs         checks the specs AND the Kotlin modules against the declarations, and
//                                                 that android/build.gradle.kts supplies androidx.annotation; exit 1 on a gap
//
// Run from the package directory (the one holding lynx.lib.json and types/). The same file lives in
// G9SignalRSuperNetCore and G9SyncData (js/packages/lynx/scripts) and G9LynxControls (packages/native/scripts): keep the
// copies identical. The JVM proof — Lynx's own signature builder over the compiled modules — is android-check's
// LynxBridgeSignatureTest; this check needs no JDK.
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';

const root = process.cwd();
const fix = process.argv.includes('--fix');
const problems = [];

/**
 * A TypeScript parameter → its Java type in the spec and the Kotlin types accepted in the module. `T | null`,
 * `T | undefined` and `name?: T` are nullable: the generator writes `@Nullable` before the Java type.
 */
function bridgeType(declared, optional) {
  const parts = splitTopLevel(declared, '|');
  const nullable = optional || parts.some((p) => p === 'null' || p === 'undefined');
  const t = parts.filter((p) => p !== 'null' && p !== 'undefined').join(' | ');
  let java;
  let kotlin;
  if (t.includes('=>')) [java, kotlin] = ['Callback', 'Callback'];
  else if (t === 'ArrayBuffer') [java, kotlin] = ['byte[]', 'ByteArray'];
  else if (t === 'string') [java, kotlin] = ['String', 'String'];
  else if (t === 'number') [java, kotlin] = nullable ? ['Double', 'Double'] : ['double', 'Double'];
  else if (t === 'boolean') [java, kotlin] = nullable ? ['Boolean', 'Boolean'] : ['boolean', 'Boolean'];
  else if (t.endsWith('[]') || /^(Readonly)?Array</.test(t)) [java, kotlin] = ['ReadableArray', 'ReadableArray'];
  else [java, kotlin] = ['ReadableMap', 'ReadableMap'];
  const primitive = java === 'double' || java === 'boolean';
  return {
    java: (nullable ? '@Nullable ' : '') + java,
    // Kotlin's nullable form is always accepted (a JS caller may pass null); the plain form only where Java allows it.
    kotlin: primitive ? [kotlin] : nullable ? [kotlin + '?'] : [kotlin + '?', kotlin],
  };
}

/** Splits at `separator` outside (), <>, {} and [] (an arrow's `>` does not close anything). */
function splitTopLevel(text, separator = ',') {
  const parts = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if ('(<{['.includes(c)) depth++;
    else if (')>}]'.includes(c) && !(c === '>' && text[i - 1] === '=')) depth--;
    else if (c === separator && depth === 0) {
      parts.push(text.slice(start, i));
      start = i + 1;
    }
  }
  parts.push(text.slice(start));
  return parts.map((p) => p.trim()).filter((p) => p.length > 0);
}

/** The text between the bracket that opens at `open` and its partner. */
function balanced(text, open, [opening, closing] = ['(', ')']) {
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    if (text[i] === opening) depth++;
    else if (text[i] === closing && --depth === 0) return text.slice(open + 1, i);
  }
  throw new Error(`unbalanced ${opening}${closing} at ${open}`);
}

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) {
      if (name !== 'build' && name !== 'generated') walk(full, out);
    } else out.push(full);
  }
  return out;
}

// 1. The declarations: every `/** @lynxmodule */ export declare class Name { method(param: Type, …): void; … }`.
const modules = [];
const typesDir = join(root, 'types');
for (const file of readdirSync(typesDir).map((f) => join(typesDir, f))) {
  const source = readFileSync(file, 'utf8');
  if (!source.includes('@lynxmodule')) continue;
  // Comments go first (a `{` or a parenthesis in prose must not count); `//` only at a line start or after a space.
  const text = source
    .replace(/\/\*[\s\S]*?\*\//g, (c) => (c.includes('@lynxmodule') ? '/*@lynxmodule*/' : ''))
    .replace(/(^|\s)\/\/.*$/gm, '$1');
  for (const match of text.matchAll(/\/\*@lynxmodule\*\/\s*export\s+declare\s+class\s+(\w+)\s*\{/g)) {
    const body = balanced(text, match.index + match[0].length - 1, ['{', '}']);
    const methods = [];
    for (const m of body.matchAll(/(\w+)\s*\(/g)) {
      if (methods.some((x) => m.index < x.end)) continue;
      const params = balanced(body, m.index + m[0].length - 1);
      const end = m.index + m[0].length + params.length + 1;
      if (!/^\s*:\s*void\s*;/.test(body.slice(end))) continue;
      methods.push({
        name: m[1],
        end,
        params: splitTopLevel(params).map((p) => {
          const colon = p.indexOf(':');
          const name = p.slice(0, colon).trim();
          return { name: name.replace('?', ''), type: bridgeType(p.slice(colon + 1), name.endsWith('?')) };
        }),
      });
    }
    modules.push({ name: match[1], declaredIn: file, methods });
  }
}
if (modules.length === 0) problems.push(`no /** @lynxmodule */ class under ${relative(root, typesDir)}`);

const lib = JSON.parse(readFileSync(join(root, 'lynx.lib.json'), 'utf8')).platforms.android;
const androidDir = join(root, lib.sourceDir);
const kotlinFiles = walk(join(androidDir, 'src', 'main')).filter((f) => f.endsWith('.kt'));

for (const module of modules) {
  // 2. The generated Java spec.
  const specPath = join(
    androidDir,
    'src',
    'main',
    'java',
    ...lib.packageName.split('.'),
    'generated',
    `${module.name}Spec.java`,
  );
  let spec = readFileSync(specPath, 'utf8');
  const eol = spec.includes('\r\n') ? '\r\n' : '\n';
  for (const method of module.methods) {
    const pattern = new RegExp(`(public abstract void ${method.name}\\()([^)]*)(\\);)`);
    const found = pattern.exec(spec);
    if (!found) {
      problems.push(`${relative(root, specPath)}: no method ${method.name} (run npm run codegen)`);
      continue;
    }
    const expected = method.params.map((p) => `${p.type.java} ${p.name}`).join(', ');
    if (found[2] === expected) continue;
    if (fix) spec = spec.replace(pattern, `$1${expected}$3`);
    else problems.push(`${relative(root, specPath)}: ${method.name}(${found[2]}) — the bridge needs (${expected})`);
  }
  if (fix) {
    spec = spec.replace(/^import com\.lynx\.react\.bridge\.\w+;\r?\n/gm, '');
    const code = spec.replace(/^import .*$/gm, '');
    const used = ['Callback', 'ReadableArray', 'ReadableMap'].filter((type) => new RegExp(`\\b${type} `).test(code));
    const imports = used.map((type) => `${eol}import com.lynx.react.bridge.${type};`).join('');
    spec = spec.replace(/(import com\.lynx\.jsbridge\.LynxMethod;)/, `$1${imports}`);
    const note =
      "// Parameter types: scripts/android-bridge-types.mjs --fix (Lynx's bridge rejects the generator's Object).";
    if (!spec.includes(note)) spec = spec.replace(/^(\/\/ Generated by .*)$/m, `$1${eol}${note}`);
    writeFileSync(specPath, spec);
    console.log(
      `android-bridge-types: ${relative(root, specPath)} rewritten from ${relative(root, module.declaredIn)}`,
    );
  }

  // 3. The Kotlin module that overrides the spec (searched from its class declaration on, so nested objects of other
  //    files and other classes do not answer for it).
  const classPattern = new RegExp(`class\\s+${module.name}\\s*\\(`);
  const moduleFile = kotlinFiles.find((f) => classPattern.test(readFileSync(f, 'utf8')));
  if (!moduleFile) {
    problems.push(`no Kotlin class ${module.name} under ${relative(root, androidDir)}/src/main`);
    continue;
  }
  const whole = readFileSync(moduleFile, 'utf8');
  const kotlin = whole.slice(whole.search(classPattern));
  for (const method of module.methods) {
    const at = kotlin.search(new RegExp(`override\\s+fun\\s+${method.name}\\s*\\(`));
    if (at < 0) {
      problems.push(`${relative(root, moduleFile)}: no override of ${method.name}`);
      continue;
    }
    const before = kotlin.slice(Math.max(0, kotlin.lastIndexOf('\n', kotlin.lastIndexOf('\n', at) - 1)), at);
    if (!before.includes('@LynxMethod'))
      problems.push(`${relative(root, moduleFile)}: ${method.name} is not annotated @LynxMethod`);
    const params = splitTopLevel(balanced(kotlin, kotlin.indexOf('(', at)));
    if (params.length !== method.params.length) {
      problems.push(
        `${relative(root, moduleFile)}: ${method.name} takes ${params.length} parameters, the declaration ${method.params.length}`,
      );
      continue;
    }
    params.forEach((p, i) => {
      const type = p.slice(p.indexOf(':') + 1).trim();
      const accepted = method.params[i].type.kotlin;
      if (!accepted.includes(type))
        problems.push(`${relative(root, moduleFile)}: ${method.name}(${p}) — the bridge needs ${accepted[0]}`);
    });
  }
}

// 4. The generated specs import androidx.annotation and lynx-processor's output uses @Keep, but Lynx's POMs declare it at
//    runtime scope only, so kapt fails in a host unless the library supplies it itself.
const gradle = readFileSync(join(androidDir, 'build.gradle.kts'), 'utf8');
if (!/compileOnly\("androidx\.annotation:annotation:/.test(gradle)) {
  problems.push(
    `${relative(root, androidDir)}/build.gradle.kts: needs compileOnly("androidx.annotation:annotation:…") (kapt: "cannot access Keep")`,
  );
}

if (problems.length > 0) {
  console.error(
    `android-bridge-types: ${modules.map((m) => m.name).join(', ')} do not match what Lynx's Android bridge accepts:`,
  );
  for (const p of problems) console.error(`  - ${p}`);
  if (!fix)
    console.error('Fix the Kotlin modules by hand; `node scripts/android-bridge-types.mjs --fix` rewrites the specs.');
  process.exit(1);
}
const count = modules.reduce((n, m) => n + m.methods.length, 0);
console.log(
  `android-bridge-types: ${modules.map((m) => m.name).join(', ')} — ${count} methods, specs and Kotlin modules match the bridge's types`,
);

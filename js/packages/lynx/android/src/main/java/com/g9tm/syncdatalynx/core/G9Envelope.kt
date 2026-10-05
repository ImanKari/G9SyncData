package com.g9tm.syncdatalynx.core

/**
 * Replies of the module contract (types/g9-syncdata-lynx-module.d.ts): `{ ok: true, value }` or
 * `{ ok: false, error: { code, message } }`, as plain Kotlin maps the Lynx layer copies into bridge containers.
 */
object G9Envelope {
  fun ok(value: Any? = null): Map<String, Any?> = mapOf("ok" to true, "value" to value)

  fun fail(code: String, message: String?): Map<String, Any?> =
    mapOf("ok" to false, "error" to mapOf("code" to code, "message" to (message ?: code)))
}

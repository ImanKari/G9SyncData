package com.g9tm.syncdatalynx

import com.g9tm.syncdatalynx.generated.G9SyncDataLynxModuleSpec
import com.lynx.jsbridge.LynxMethod
import java.lang.reflect.InvocationTargetException
import java.lang.reflect.Method
import org.junit.Assert.assertEquals
import org.junit.Assert.fail
import org.junit.Test

/**
 * Builds the signature of every `@LynxMethod` with Lynx's own builder (`LynxMethodWrapper`, what
 * `LynxModuleWrapper.findMethods` runs the first time a LynxView touches the module). A parameter type the bridge does
 * not know — `Object` / `Any?`, which @lynx-js/autolink-codegen 0.6.0 writes for every function, object, array and
 * ArrayBuffer — throws there, and the module reaches JS with NO methods at all (LES-0050). Compiling cannot see that.
 */
class LynxBridgeSignatureTest {
  private fun lynxSignatures(type: Class<*>): Map<String, String> {
    val wrapper = Class.forName("com.lynx.jsbridge.LynxMethodWrapper")
    val create = wrapper.getDeclaredConstructor(Method::class.java).apply { isAccessible = true }
    val signature = wrapper.getMethod("getSignature")
    return type.declaredMethods
      .filter { it.isAnnotationPresent(LynxMethod::class.java) }
      .associate { method ->
        method.name to
          try {
            signature.invoke(create.newInstance(method)) as String
          } catch (error: InvocationTargetException) {
            fail("Lynx rejects ${type.simpleName}.${method.name}${method.parameterTypes.map { it.simpleName }}: ${error.targetException.message}")
            throw error
          }
      }
  }

  @Test
  fun every_method_has_a_signature_the_Lynx_bridge_accepts() {
    val module = lynxSignatures(G9SyncDataLynxModule::class.java)
    // Every method the declaration (types/*.d.ts → the generated spec) promises is implemented AND annotated.
    assertEquals(lynxSignatures(G9SyncDataLynxModuleSpec::class.java).keys, module.keys)
    println("lynx signatures: $module")
  }
}

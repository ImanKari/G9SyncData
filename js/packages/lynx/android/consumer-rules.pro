# Lynx looks modules up by annotation name; keep them and their methods.
-keep @com.lynx.jsbridge.LynxNativeModule class * { *; }
-keepclassmembers class * { @com.lynx.jsbridge.LynxMethod *; }

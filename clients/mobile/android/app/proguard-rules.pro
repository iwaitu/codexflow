# Preserve CodexFlow Capacitor native plugin and QR scanner classes under R8
-keep class dev.codexflow.mobile.CodexFlowCryptoPlugin { *; }
-keep class dev.codexflow.mobile.QrScannerActivity { *; }
-keepclassmembers class * extends com.getcapacitor.Plugin {
    @com.getcapacitor.PluginMethod <methods>;
    @com.getcapacitor.annotation.ActivityCallback <methods>;
    @com.getcapacitor.annotation.PermissionCallback <methods>;
}

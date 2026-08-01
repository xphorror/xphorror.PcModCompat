# Consumer ProGuard rules for StArray.ModManager AAR library.
# These rules are included when the AAR is consumed by an application.
# See https://developer.android.com/studio/projects/android-library#considerations

# Keep ModManager public API
-keep class com.fizzd.connectedworlds.editorport.StArrayModManagerBootstrap {
    public *;
}
-keep class starray.android.modmanager.ModManagerNative {
    native <methods>;
}

# kotlinx.serialization (settings and scene JSON)
-keepattributes *Annotation*, InnerClasses
-keepclassmembers class app.kissik.studio.** { *** Companion; }
-keepclasseswithmembers class app.kissik.studio.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class app.kissik.studio.**$$serializer { *; }
# RTMP client uses reflection-free Kotlin, but keep its public API stable for logging
-keep class com.pedro.** { *; }
-dontwarn com.pedro.**
# OkHttp optional platforms
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
-dontwarn org.slf4j.**

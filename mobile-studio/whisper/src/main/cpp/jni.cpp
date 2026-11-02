// JNI bridge to whisper.cpp for KISSIK's on-device "Accurate" captions.
#include <jni.h>
#include <string>
#include "whisper.h"

extern "C" JNIEXPORT jlong JNICALL
Java_app_kissik_whisper_Whisper_nativeInit(JNIEnv *env, jclass, jstring path) {
    const char *p = env->GetStringUTFChars(path, nullptr);
    whisper_context_params cp = whisper_context_default_params();
    whisper_context *ctx = whisper_init_from_file_with_params(p, cp);
    env->ReleaseStringUTFChars(path, p);
    return reinterpret_cast<jlong>(ctx);
}

extern "C" JNIEXPORT void JNICALL
Java_app_kissik_whisper_Whisper_nativeFree(JNIEnv *, jclass, jlong ctx) {
    if (ctx) whisper_free(reinterpret_cast<whisper_context *>(ctx));
}

// Transcribes 16 kHz mono float samples; returns the text of all segments.
extern "C" JNIEXPORT jstring JNICALL
Java_app_kissik_whisper_Whisper_nativeTranscribe(JNIEnv *env, jclass, jlong ctx, jfloatArray samples, jstring lang, jint threads) {
    auto *c = reinterpret_cast<whisper_context *>(ctx);
    if (!c) return env->NewStringUTF("");
    const char *l = env->GetStringUTFChars(lang, nullptr);
    std::string language(l);
    env->ReleaseStringUTFChars(lang, l);

    whisper_full_params wp = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    wp.print_progress = false; wp.print_realtime = false; wp.print_timestamps = false; wp.print_special = false;
    wp.no_context = true; wp.translate = false; wp.n_threads = threads; wp.suppress_blank = true;
    wp.language = language.c_str();

    jsize n = env->GetArrayLength(samples);
    jfloat *data = env->GetFloatArrayElements(samples, nullptr);
    int rc = whisper_full(c, wp, data, n);
    env->ReleaseFloatArrayElements(samples, data, JNI_ABORT);
    if (rc != 0) return env->NewStringUTF("");

    std::string out;
    int segs = whisper_full_n_segments(c);
    for (int i = 0; i < segs; i++) out += whisper_full_get_segment_text(c, i);
    return env->NewStringUTF(out.c_str());
}

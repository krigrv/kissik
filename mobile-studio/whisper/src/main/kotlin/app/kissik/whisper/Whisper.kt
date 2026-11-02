package app.kissik.whisper

import java.io.Closeable

/** whisper.cpp (MIT) model loaded from a ggml file. Not thread-safe: use one thread per instance. */
class Whisper private constructor(private var ctx: Long) : Closeable {
    /** Transcribes 16 kHz mono samples in -1..1. [language] is a two-letter code or "auto". */
    fun transcribe(samples16k: FloatArray, language: String = "en", threads: Int = 4): String =
        if (ctx == 0L) "" else nativeTranscribe(ctx, samples16k, language, threads).trim()

    override fun close() { nativeFree(ctx); ctx = 0 }

    companion object {
        init { System.loadLibrary("kissik_whisper") }
        fun load(modelPath: String): Whisper? = nativeInit(modelPath).takeIf { it != 0L }?.let { Whisper(it) }
        @JvmStatic private external fun nativeInit(path: String): Long
        @JvmStatic private external fun nativeFree(ctx: Long)
        @JvmStatic private external fun nativeTranscribe(ctx: Long, samples: FloatArray, language: String, threads: Int): String
    }
}

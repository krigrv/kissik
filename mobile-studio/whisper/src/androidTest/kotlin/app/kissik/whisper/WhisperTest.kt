package app.kissik.whisper

import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Runs whisper.cpp on the device. Push the model and whisper.cpp's sample first:
 *   adb push ggml-base.bin /data/local/tmp/   and   adb push samples/jfk.wav /data/local/tmp/
 */
class WhisperTest {
    @Test fun transcribesSample() {
        val model = File("/data/local/tmp/ggml-base.bin"); val wav = File("/data/local/tmp/jfk.wav")
        assumeTrue("model and sample not pushed", model.exists() && wav.exists())
        val bytes = wav.readBytes()
        val data = ByteBuffer.wrap(bytes, 44, bytes.size - 44).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
        val pcm = FloatArray(data.remaining()) { data.get(it) / 32768f } // 16 kHz mono 16-bit
        val w = Whisper.load(model.path)!!
        val t0 = System.nanoTime()
        val text = w.transcribe(pcm, "en", threads = 4)
        android.util.Log.i("KISSIK", "whisper ${(System.nanoTime() - t0) / 1_000_000} ms: $text")
        w.close()
        assertTrue(text, text.contains("country", ignoreCase = true))
    }
}

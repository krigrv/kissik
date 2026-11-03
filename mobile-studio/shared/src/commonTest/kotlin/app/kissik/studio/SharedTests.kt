package app.kissik.studio

import app.kissik.studio.camera.CameraState
import app.kissik.studio.camera.Lens
import app.kissik.studio.camera.ProControls
import app.kissik.studio.captions.CaptionTrack
import app.kissik.studio.captions.Srt
import app.kissik.studio.crash.CrashReports
import app.kissik.studio.effects.BackgroundMode
import app.kissik.studio.effects.Compositor
import app.kissik.studio.effects.Effects
import app.kissik.studio.framing.AutoFramer
import app.kissik.studio.framing.NRect
import app.kissik.studio.image.*
import app.kissik.studio.recording.EncodedSample
import app.kissik.studio.recording.RecordingClock
import app.kissik.studio.recording.ReplayBuffer
import app.kissik.studio.recording.TimeLapse
import app.kissik.studio.scenes.SceneDocument
import app.kissik.studio.scenes.SceneTemplates
import app.kissik.studio.scenes.Transition
import app.kissik.studio.scenes.transitionFrame
import app.kissik.studio.stream.Destination
import app.kissik.studio.stream.Rtmp
import app.kissik.studio.teleprompter.Teleprompter
import app.kissik.studio.webcam.LinkStatus
import app.kissik.studio.webcam.Pairing
import app.kissik.studio.webcam.PcCommand
import app.kissik.studio.webcam.PhoneProtocol
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlin.math.abs
import kotlin.test.*

class ImageTests {
    @Test fun parsesDesktopColours() {
        assertEquals(0xFFFFFFFF.toInt(), parseColor("#FFFFFF"))
        assertEquals(0xE6141618.toInt(), parseColor("#E6141618"))
        assertEquals(0xFF112233.toInt(), parseColor("#123"))
        assertEquals(7, parseColor("nonsense", 7))
    }

    @Test fun blurKeepsFlatColourAndSoftensEdge() {
        val f = Frame(20, 1, IntArray(20) { if (it < 10) 0xFF000000.toInt() else 0xFFFFFFFF.toInt() })
        val b = f.boxBlur(2)
        assertEquals(0xFF000000.toInt(), b[0, 0]); assertEquals(0xFFFFFFFF.toInt(), b[19, 0])
        assertTrue(r(b[10, 0]) in 60..200, "edge pixel should be grey, was ${r(b[10, 0])}")
    }

    @Test fun compositeUsesMask() {
        val person = Frame.solid(2, 1, 0xFFFF0000.toInt()); val bg = Frame.solid(2, 1, 0xFF0000FF.toInt())
        val out = composite(person, bg, Mask(2, 1, floatArrayOf(1f, 0f)))
        assertEquals(0xFFFF0000.toInt(), out[0, 0]); assertEquals(0xFF0000FF.toInt(), out[1, 0])
    }

    @Test fun refinedMaskIsCleanedTowardsZeroAndOne() {
        val m = Mask(10, 1, FloatArray(10) { if (it < 5) 0.9f else 0.1f }).refined(feather = 0)
        assertEquals(1f, m.values[0]); assertEquals(0f, m.values[9])
    }

    @Test fun greenScreenKeysGreenNotSkin() {
        val f = Frame(2, 1, intArrayOf(0xFF00B140.toInt(), 0xFFE0AC90.toInt()))
        val m = chromaKeyMask(f)
        assertTrue(m.values[0] < 0.1f); assertTrue(m.values[1] > 0.9f)
    }

    @Test fun lightWrapOnlyTouchesEdges() {
        val comp = Frame.solid(4, 1, 0xFF202020.toInt()); val bg = Frame.solid(4, 1, 0xFFFFFFFF.toInt())
        val out = lightWrap(comp, bg, Mask(4, 1, floatArrayOf(0f, 0.6f, 1f, 1f)), strength = 1f, radius = 1)
        assertEquals(comp[0, 0], out[0, 0]); assertEquals(comp[3, 0], out[3, 0]); assertTrue(r(out[1, 0]) > 0x20)
    }

    @Test fun adjustmentsAndMono() {
        val f = Frame.solid(1, 1, 0xFF804020.toInt())
        assertTrue(r(f.adjusted(Adjustments(brightness = 0.5f))[0, 0]) > 0x80)
        val m = f.adjusted(Adjustments(), mono = true)[0, 0]
        assertEquals(r(m), g(m)); assertEquals(g(m), b(m))
    }

    @Test fun identityLutChangesNothing() {
        val f = Frame(3, 1, intArrayOf(0xFF102030.toInt(), 0xFF808080.toInt(), 0xFFF0E0D0.toInt()))
        val out = Lut3D.identity(17).apply(f)
        for (i in 0..2) assertTrue(abs(r(out.pixels[i]) - r(f.pixels[i])) <= 1)
    }

    @Test fun parsesCubeFile() {
        val cube = buildString {
            append("TITLE \"Invert\"\nLUT_3D_SIZE 2\nDOMAIN_MIN 0 0 0\n")
            for (b in 0..1) for (g in 0..1) for (r in 0..1) append("${1 - r} ${1 - g} ${1 - b}\n")
        }
        val lut = Lut3D.parseCube(cube)
        assertEquals("Invert", lut.title)
        assertEquals(0xFFFFFFFF.toInt(), lut.apply(Frame.solid(1, 1, 0xFF000000.toInt()))[0, 0])
        assertFailsWith<IllegalArgumentException> { Lut3D.parseCube("LUT_3D_SIZE 2\n0 0 0\n") }
    }

    @Test fun autoBestBrightensDarkAndBalancesCast() {
        val dark = Frame.solid(8, 8, 0xFF302010.toInt())
        val c = analyzeAuto(dark, 1)
        assertTrue(c.brightness > 0f)
        assertTrue(c.gains[2] > c.gains[0], "blue should be boosted on an orange cast")
    }

    @Test fun skinSmoothingLeavesNonSkinAlone() {
        val blue = Frame.solid(6, 6, 0xFF2040C0.toInt())
        assertContentEquals(blue.pixels, blue.smoothedSkin(1f).pixels)
    }

    @Test fun rotateAndMirror() {
        val f = Frame(2, 1, intArrayOf(1, 2))
        assertContentEquals(intArrayOf(1, 2), f.rotated(90).pixels.let { it })
        assertEquals(1, f.rotated(90).width); assertEquals(2, f.rotated(90).height)
        assertContentEquals(intArrayOf(2, 1), f.mirrored().pixels)
        assertContentEquals(intArrayOf(2, 1), f.rotated(180).pixels)
    }
}

class DocumentTests {
    @Test fun homographyMapsCorners() {
        val src = listOf(Pt(10f, 10f), Pt(90f, 20f), Pt(80f, 90f), Pt(5f, 80f))
        val dst = listOf(Pt(0f, 0f), Pt(100f, 0f), Pt(100f, 100f), Pt(0f, 100f))
        val h = Homography.fromPoints(src, dst)
        src.zip(dst).forEach { (s, d) ->
            val (x, y) = h.map(s.x.toDouble(), s.y.toDouble())
            assertTrue(abs(x - d.x) < 1e-6 && abs(y - d.y) < 1e-6)
        }
    }

    @Test fun quadOrdersLoosePoints() {
        val q = Quad.fromPoints(listOf(Pt(90f, 90f), Pt(0f, 0f), Pt(0f, 90f), Pt(90f, 0f)))
        assertEquals(Pt(0f, 0f), q.tl); assertEquals(Pt(90f, 0f), q.tr); assertEquals(Pt(90f, 90f), q.br); assertEquals(Pt(0f, 90f), q.bl)
    }

    @Test fun warpFlattensPage() {
        val f = Frame(100, 100) { 0 }.also { fr -> for (y in 20..79) for (x in 20..79) fr[x, y] = 0xFFFFFFFF.toInt() }
        val page = f.warpedPage(Quad(Pt(20f, 20f), Pt(79f, 20f), Pt(79f, 79f), Pt(20f, 79f)))
        assertEquals(59, page.width)
        assertTrue(page.pixels.all { it == 0xFFFFFFFF.toInt() })
    }

    @Test fun cleanBlackAndWhiteSurvivesShadow() {
        // Gradient "paper" with dark ink strokes: ink must be black, paper white, regardless of the shading.
        val f = Frame(200, 50)
        for (y in 0 until 50) for (x in 0 until 200) {
            val paper = 120 + x / 2
            val ink = x % 40 in 18..21
            val v = if (ink) paper - 90 else paper
            f[x, y] = argb(255, v, v, v)
        }
        val bw = f.cleanBlackAndWhite(window = 31)
        assertEquals(0xFF000000.toInt(), bw[19, 25]); assertEquals(0xFF000000.toInt(), bw[179, 25])
        assertEquals(0xFFFFFFFF.toInt(), bw[5, 25]); assertEquals(0xFFFFFFFF.toInt(), bw[190, 25])
    }

    @Test fun pdfHasPagesAndXref() {
        val jpeg = byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte(), 0xD9.toByte())
        val bytes = PdfWriter.write(listOf(PdfWriter.Page(jpeg, 100, 200), PdfWriter.Page(jpeg, 300, 100)))
        val pdf = String(CharArray(bytes.size) { (bytes[it].toInt() and 0xFF).toChar() }) // Latin-1, byte offsets kept
        assertTrue(pdf.startsWith("%PDF-1.4")); assertTrue(pdf.trimEnd().endsWith("%%EOF"))
        assertTrue(pdf.contains("/Count 2")); assertTrue(pdf.contains("/DCTDecode"))
        val xref = pdf.substringAfter("startxref\n").substringBefore('\n').toInt()
        assertEquals("xref", pdf.substring(xref, xref + 4))
    }
}

private fun Frame(w: Int, h: Int, init: (Int) -> Int) = app.kissik.studio.image.Frame(w, h, IntArray(w * h, init))

class FramingTests {
    @Test fun followsFaceAndZoomsIn() {
        val f = AutoFramer(aspect = 1f, reducedMotion = true)
        val crop = f.update(listOf(NRect(0.7f, 0.3f, 0.1f, 0.15f)), 0.033f)
        assertTrue(crop.w < 1f, "should zoom in")
        assertTrue(crop.cx > 0.5f, "should move toward the face on the right")
    }

    @Test fun springSettlesWithoutBigOvershoot() {
        val f = AutoFramer(aspect = 1f)
        var maxX = 0f
        repeat(300) { maxX = maxOf(maxX, f.update(listOf(NRect(0.6f, 0.4f, 0.1f, 0.1f)), 1 / 30f).cx) }
        val final = f.crop.cx
        assertTrue(maxX - final < 0.03f, "overshoot ${maxX - final}")
    }

    @Test fun returnsToFullAfterLosingFaces() {
        val f = AutoFramer(aspect = 1f, reducedMotion = true)
        f.update(listOf(NRect(0.4f, 0.4f, 0.1f, 0.1f)), 0.1f)
        repeat(30) { f.update(emptyList(), 0.1f) }
        assertEquals(1f, f.crop.w, 0.001f)
    }

    @Test fun compositorBlursOnlyBackground() {
        val w = 32; val h = 18
        val input = Frame(w, h) { i -> if ((i % w) % 2 == 0) 0xFF000000.toInt() else 0xFFFFFFFF.toInt() }
        val mask = Mask(w, h, FloatArray(w * h) { i -> if (i % w < w / 2) 1f else 0f })
        val out = Compositor().process(input, Effects(background = BackgroundMode.Blur, edgeRefine = false), mask, emptyList(), 0.03f)
        assertEquals(input[2, 9], out[2, 9]) // person side untouched
        val g = r(out[w - 3, 9]); assertTrue(g in 40..215, "background should be blurred grey, was $g")
    }
}

class SceneTests {
    private val desktopJson = """
        {"Id":"pack-x","Name":"Executive","Category":"Professional","Background":"#FF0E1116","Background2":"#FF1B2130",
         "Layers":[{"Kind":"camera","Name":"Video source","X":0,"Y":0,"W":1,"H":1,"Shape":"rect","CornerRadius":0.0,"Visible":true},
                   {"Kind":"text","Name":"Your Name","Text":"Your Name","X":0.065,"Y":0.79,"W":0.46,"H":0.07,"FontSize":0.056,"Bold":true,"Color":"#FFFFFFFF","Align":"left","Fill":"","Glyph":"ignored"}]}
    """.trimIndent()

    @Test fun readsDesktopSceneJson() {
        val s = SceneDocument.fromJson(desktopJson)
        assertEquals("Executive", s.name); assertEquals(2, s.layers.size)
        assertEquals("camera", s.camera.kind); assertEquals(1, s.overlays.size)
        assertEquals("Your Name", s.overlays[0].displayName)
        assertTrue(s.overlays[0].bold)
        assertEquals("Executive, Professional", s.toString())
    }

    @Test fun roundTripsWithDesktopFieldNames() {
        val json = SceneTemplates.builtIn.first { it.id == "lower-third" }.toJson()
        assertTrue(json.contains("\"Layers\"")); assertTrue(json.contains("\"FontSize\""))
        assertEquals(SceneTemplates.builtIn.first { it.id == "lower-third" }, SceneDocument.fromJson(json))
    }

    @Test fun readsPackList() {
        val list = SceneDocument.listFromJson("\uFEFF[$desktopJson,$desktopJson]")
        assertEquals(2, list.size)
    }

    @Test fun transitions() {
        assertEquals(0.5f, transitionFrame(Transition.Fade, 0.5f).newAlpha, 0.001f)
        assertEquals(0f, transitionFrame(Transition.Cut, 0.5f).newAlpha)
        assertEquals(1f, transitionFrame(Transition.Slide, 0f).newOffset)
        assertEquals(0f, transitionFrame(Transition.Slide, 1f).newOffset)
        assertTrue(transitionFrame(Transition.Zoom, 1f).oldScale > 1f)
    }
}

class ProtocolTests {
    @Test fun parsesPairingLink() {
        val p = Pairing.parse("https://192.168.1.20:47820/?t=0123456789ABCDEF0123456789abcdef")!!
        assertEquals("192.168.1.20", p.host); assertEquals(47820, p.port)
        assertEquals("wss://192.168.1.20:47820/ws?t=0123456789abcdef0123456789abcdef", p.wifiUrl)
        assertEquals("ws://localhost:47821/ws?t=0123456789abcdef0123456789abcdef", p.usbUrl)
        assertNull(Pairing.parse("http://1.2.3.4:47820/?t=0123456789abcdef"))
        assertNull(Pairing.parse("https://1.2.3.4/?t=xyz"))
        assertEquals(47820, Pairing.parse("https://pc.local/?t=0123456789abcdef")!!.port)
    }

    @Test fun parsesEveryCommand() {
        assertEquals(PcCommand.Facing(true), PcCommand.parse("front"))
        assertEquals(PcCommand.Lens("2"), PcCommand.parse("lens:2"))
        assertEquals(PcCommand.Quality(2160), PcCommand.parse("q:2160"))
        assertEquals(PcCommand.Zoom(2.5f), PcCommand.parse("zoom:2.5"))
        assertEquals(PcCommand.Iso(null), PcCommand.parse("iso:auto"))
        assertEquals(PcCommand.Shutter(16666666), PcCommand.parse("shutter:16666666"))
        assertEquals(PcCommand.FocusTap(0.25f, 1f), PcCommand.parse("focus:tap:0.25,1.4"))
        assertEquals(PcCommand.Fps(0), PcCommand.parse("fps:auto"))
        assertEquals(PcCommand.Codec("h264"), PcCommand.parse("codec:h264"))
        assertEquals(PcCommand.KeyFrame, PcCommand.parse("key"))
        assertEquals(PcCommand.MicDevice(null), PcCommand.parse("micdev:auto"))
        assertEquals(PcCommand.NoiseSuppression(true), PcCommand.parse("ns:on"))
        assertTrue(PcCommand.parse("teleport:mars") is PcCommand.Unknown)
        assertTrue(PcCommand.parse("codec:vp9") is PcCommand.Unknown)
    }

    @Test fun statusJsonHasProtocolFields() {
        val cam = CameraState(ready = true, lenses = listOf(Lens("0", false, "Main")), lensId = null, width = 1280, height = 720,
            hasTorch = false, manual = true, isoMin = 100, isoMax = 3200, stab = null, hdr = true)
        val j = Json.parseToJsonElement(PhoneProtocol.status(cam, LinkStatus("Pixel", 0.5f, false))).jsonObject
        assertEquals("back", j["facing"]!!.jsonPrimitive.content)
        assertEquals("null", j["torch"].toString()); assertEquals("null", j["stab"].toString())
        assertEquals("100", j["isoMin"].toString()); assertEquals("1280", j["w"].toString())
        assertTrue(j.containsKey("lenses") && j.containsKey("wbModes") && j.containsKey("micModes"))
    }

    @Test fun packetLayouts() {
        val v = PhoneProtocol.videoPacket(byteArrayOf(0, 0, 0, 1, 0x67), true, 0x0102030405L)
        assertEquals('V'.code.toByte(), v[0]); assertEquals(1, v[1].toInt())
        assertEquals(0x05, v[2].toInt()); assertEquals(0x01, v[6].toInt()); assertEquals(0x67, v[14].toInt())
        val a = PhoneProtocol.audioPacket(floatArrayOf(1f), 48000)
        assertEquals('A'.code.toByte(), a[0]); assertEquals(48000, (a[1].toInt() and 0xFF) or ((a[2].toInt() and 0xFF) shl 8) or ((a[3].toInt() and 0xFF) shl 16))
        assertEquals(9, a.size)
    }
}

class RecordingTests {
    @Test fun replayBufferTrimsToKeyframe() {
        val rb = ReplayBuffer(seconds = 2)
        for (i in 0 until 150) rb.add(EncodedSample(ByteArray(10), i * 33_333L, keyFrame = i % 30 == 0))
        val clip = rb.snapshot()
        assertTrue(clip.first().keyFrame)
        val span = clip.last().timeUs - clip.first().timeUs
        assertTrue(span in 1_900_000L..3_100_000L, "span $span")
    }

    @Test fun replayBufferRespectsByteLimit() {
        val rb = ReplayBuffer(seconds = 30, maxBytes = 1000)
        for (i in 0 until 100) rb.add(EncodedSample(ByteArray(100), i * 33_333L, keyFrame = i % 10 == 0))
        assertTrue(rb.size <= 20)
    }

    @Test fun clockExcludesPauses() {
        val c = RecordingClock()
        c.start(0); c.pause(1000); c.resume(5000)
        assertEquals(3000, c.elapsedMs(7000)); assertEquals(4_000_000, c.pausedUs())
        assertEquals("01:05", RecordingClock.format(65_000)); assertEquals("1:00:00", RecordingClock.format(3_600_000))
    }

    @Test fun timeLapseMath() {
        val t = TimeLapse(1000, 30)
        assertEquals(30f, t.speedUp); assertTrue(t.shouldCapture(-1, 0)); assertFalse(t.shouldCapture(0, 999))
        assertEquals(1_000_000L, t.frameTimeUs(30))
    }
}

class MiscTests {
    @Test fun rtmpValidation() {
        assertNull(Rtmp.validate("rtmp://live.example.com/app"))
        assertNull(Rtmp.validate("rtmps://live.example.com:443/app"))
        assertNotNull(Rtmp.validate("http://x/app")); assertNotNull(Rtmp.validate("rtmp://host")); assertNotNull(Rtmp.validate("rtmp://h:99999/a"))
        assertEquals("rtmp://h/live" to "abc", Rtmp.split("rtmp://h/live/abc"))
        assertEquals("rtmp://h/live/abc", Destination("1", "x", "rtmp://h/live/", "abc").publishUrl)
    }

    @Test fun captionsWrapAndExpire() {
        val c = CaptionTrack(maxChars = 10, holdMs = 1000)
        c.onFinal("hello there my friend", 0)
        c.onPartial("how are")
        val now = c.lines(10)
        assertEquals(2, now.size); assertTrue(now.last().endsWith("are")); assertTrue(now.all { it.length <= 10 })
        assertEquals(listOf("how are"), c.lines(5000))
        assertTrue(Srt.write(listOf(Srt.Cue(0, 1500, "Hi"))).contains("00:00:00,000 --> 00:00:01,500"))
    }

    @Test fun teleprompterScrollsAtWpm() {
        val t = Teleprompter(120); t.playing = true
        t.tick(1f, 1000f, 100) // 2 words/s × 10 px/word
        assertEquals(20f, t.offsetPx, 0.01f)
        assertEquals(3, Teleprompter.wordCount(" one two\nthree "))
    }

    @Test fun crashScrubbingMatchesDesktopRules() {
        val s = CrashReports.scrub("at C:\\Users\\Asha Rao\\x.cs /home/asha/a /Users/bob/b mail a.b@c.com on Asha's Pixel", "Asha's Pixel")
        assertFalse(s.contains("Asha Rao")); assertFalse(s.contains("asha/")); assertFalse(s.contains("bob"))
        assertTrue(s.contains("<email>")); assertTrue(s.contains("<device>"))
        assertEquals(16000, CrashReports.scrub("x".repeat(20000)).length)
    }

    @Test fun crashEnvelopeAndDsn() {
        val ep = CrashReports.parseDsn(CrashReports.DSN)!!
        assertEquals("https://o4512361962340352.ingest.de.sentry.io/api/4512361969877072/envelope/", ep.url)
        assertEquals("a8a20e3e7d86bf3fd8363ab4b1eaeb74", ep.key)
        val env = CrashReports.buildEnvelope(CrashReports.DSN, "2026-11-02T10:00:00Z\njava.lang.IllegalStateException: boom at /data/user/0/app",
            "0.1.0", "Android", "15", "2026-11-02T10:00:00Z", id = "abc")
        val lines = env.trim().split('\n')
        assertEquals(3, lines.size)
        val evt = Json.parseToJsonElement(lines[2]).jsonObject
        assertTrue(lines[2].contains("java.lang.IllegalStateException")); assertTrue(lines[2].contains("/data/user/<n>/"))
        assertEquals("kissik-mobile@0.1.0", evt["release"]!!.jsonPrimitive.content)
        assertEquals(2, CrashReports.splitEntries("2026-01-01T00:00 a\nmore\n2026-01-02T00:00 b").size)
    }

    @Test fun settingsSurviveUnknownFields() {
        val s = Settings(mode = UiMode.Studio, replaySeconds = 60)
        assertEquals(s, Settings.fromJson(s.toJson()))
        assertEquals(Settings(), Settings.fromJson("{\"futureField\":1}"))
        assertEquals(Settings(), Settings.fromJson("garbage"))
    }

    @Test fun proControlHelpers() {
        assertEquals("1/60", ProControls.shutterLabel(1_000_000_000L / 60))
        assertEquals(listOf(100, 200, 400, 800), ProControls.isoStops(100, 800))
        assertEquals(3, ProControls.evIndex(1f, 1f / 3, -6, 6))
    }
}

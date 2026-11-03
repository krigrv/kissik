package app.kissik.studio.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import app.kissik.studio.Studio
import app.kissik.studio.camera.ProControls
import app.kissik.studio.captions.CaptionEngineKind
import app.kissik.studio.effects.BackgroundMode
import app.kissik.studio.image.Filter
import app.kissik.studio.platform.WebcamState
import app.kissik.studio.recording.Quality
import app.kissik.studio.platform.RecordOptions
import app.kissik.studio.recording.TimeLapse
import app.kissik.studio.scenes.SceneDocument
import app.kissik.studio.scenes.Transition
import app.kissik.studio.stream.Destination
import app.kissik.studio.stream.LinkState
import app.kissik.studio.stream.Rtmp
import app.kissik.studio.stream.StreamSettings
import app.kissik.studio.stream.VideoCodec
import app.kissik.studio.webcam.Pairing
import kotlin.math.roundToInt

// ── Small accessible building blocks ─────────────────────────────────────────

@Composable
fun LabeledSlider(label: String, value: Float, range: ClosedFloatingPointRange<Float>, valueText: String, steps: Int = 0, onChange: (Float) -> Unit) {
    Column(Modifier.padding(vertical = 4.dp)) {
        Row { Text(label, Modifier.weight(1f)); Text(valueText, color = MaterialTheme.colorScheme.primary) }
        Slider(value, onChange, valueRange = range, steps = steps,
            modifier = Modifier.heightIn(min = Brand.minTouch).semantics { contentDescription = label; stateDescription = valueText })
    }
}

@Composable
fun SwitchRow(label: String, checked: Boolean, detail: String? = null, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = Brand.minTouch)
        .toggleable(value = checked, enabled = enabled, role = Role.Switch, onValueChange = onChange), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(label)
            if (detail != null) Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Switch(checked, null, enabled = enabled)
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun <T> Choices(items: List<T>, selected: T?, label: (T) -> String, onPick: (T) -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        items.forEach { item ->
            FilterChip(selected = item == selected, onClick = { onPick(item) }, label = { Text(label(item)) },
                modifier = Modifier.heightIn(min = Brand.minTouch))
        }
    }
}

@Composable
fun SectionTitle(text: String) =
    Text(text, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(top = 12.dp, bottom = 4.dp).semantics { heading() })

// ── Camera ───────────────────────────────────────────────────────────────────

@Composable
fun CameraPanel(studio: Studio) {
    val c = studio.platform.camera
    val s by c.state.collectAsState()
    SectionTitle("Lens")
    Choices(s.lenses, s.lenses.firstOrNull { it.id == s.lensId }, { it.label }) { c.selectLens(it.id) }
    if (s.zoomMax > s.zoomMin) LabeledSlider("Zoom", s.zoom, s.zoomMin..s.zoomMax, fmtZoom(s.zoom)) { c.setZoom(it) }
    if (s.evStep > 0f) LabeledSlider("Exposure", s.ev.toFloat(), s.evMin.toFloat()..s.evMax.toFloat(),
        "${if (s.ev > 0) "+" else ""}${(s.ev * s.evStep * 10).roundToInt() / 10f} EV", steps = (s.evMax - s.evMin - 1).coerceAtLeast(0)) { c.setEv(it.roundToInt()) }
    if (s.manual) {
        SectionTitle("Manual exposure")
        val isos = ProControls.isoStops(s.isoMin, s.isoMax)
        Choices(listOf<Int?>(null) + isos, s.iso, { it?.let { v -> "ISO $v" } ?: "Auto ISO" }) { c.setIso(it) }
        val shutters = ProControls.shutterStops.filter { it in s.shutterMin..s.shutterMax }
        Choices(listOf<Long?>(null) + shutters, s.shutter, { it?.let(ProControls::shutterLabel) ?: "Auto shutter" }) { c.setShutter(it) }
    }
    if (s.focusManual) {
        SectionTitle("Focus")
        SwitchRow("Auto focus", s.focus == null, "Tap the picture to focus on a spot") { c.setFocus(if (it) null else 0.5f) }
        s.focus?.let { f -> LabeledSlider("Focus distance", f, 0f..1f, if (f < 0.05f) "Far" else if (f > 0.95f) "Nearest" else "${(f * 100).roundToInt()}%") { c.setFocus(it) } }
    }
    SectionTitle("White balance")
    Choices(s.wbModes, s.wb, ProControls::wbLabel) { c.setWhiteBalance(it) }
    if (s.fpsList.isNotEmpty()) {
        SectionTitle("Frame rate")
        Choices(listOf(0) + s.fpsList, s.fps, { if (it == 0) "Auto" else "$it fps" }) { c.setFps(it) }
    }
    SectionTitle("More")
    s.hdr?.let { SwitchRow("HDR", it, "Keeps detail in bright skies and dark rooms") { on -> c.setHdr(on) } }
    s.stab?.let { SwitchRow("Stabilisation", it, "Smooths out handshake") { on -> c.setStabilization(on) } }
    if (s.hasTorch) SwitchRow("Light", s.torch) { c.setTorch(it) }
    SectionTitle("Capture size")
    Choices(listOf(720, 1080, 2160).filter { it <= maxOf(s.maxWidth, s.maxHeight) || s.maxWidth == 0 }, minOf(s.width, s.height),
        { if (it == 2160) "4K" else "${it}p" }) { c.setQuality(it) }
}

// ── Background ───────────────────────────────────────────────────────────────

@Composable
fun BackgroundPanel(studio: Studio) {
    val fx = studio.effects
    Choices(BackgroundMode.entries, fx.background, { it.label }) { m -> studio.updateEffects { it.copy(background = m) } }
    when (fx.background) {
        BackgroundMode.Blur -> LabeledSlider("Blur strength", fx.blurStrength, 0f..1f, "${(fx.blurStrength * 100).roundToInt()}%") { v -> studio.updateEffects { it.copy(blurStrength = v) } }
        BackgroundMode.Replace, BackgroundMode.GreenScreen -> {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { studio.platform.files.pickBackground(false) { it?.let(studio::setBackgroundSource) } }) { Icon(Icons.Filled.Image, null); Spacer(Modifier.width(6.dp)); Text("Picture") }
                OutlinedButton(onClick = { studio.platform.files.pickBackground(true) { it?.let(studio::setBackgroundSource) } }) { Icon(Icons.Filled.Movie, null); Spacer(Modifier.width(6.dp)); Text("Looping video") }
            }
            studio.backgroundSource?.let { Text("Using: ${it.label}", style = MaterialTheme.typography.bodySmall) }
            if (fx.background == BackgroundMode.Replace)
                LabeledSlider("Light wrap", fx.lightWrap, 0f..1f, "${(fx.lightWrap * 100).roundToInt()}%") { v -> studio.updateEffects { it.copy(lightWrap = v) } }
            else LabeledSlider("Key tolerance", fx.keyTolerance, 0.05f..0.6f, "${(fx.keyTolerance * 100).roundToInt()}") { v -> studio.updateEffects { it.copy(keyTolerance = v) } }
        }
        BackgroundMode.Remove -> {
            SectionTitle("Colour")
            val colors = listOf(0xFF1A1D22.toInt() to "Charcoal", 0xFFFFFFFF.toInt() to "White", 0xFF00B140.toInt() to "Green", 0xFF2E5BFF.toInt() to "Blue", 0xFFE8551A.toInt() to "Orange")
            Choices(colors, colors.firstOrNull { it.first == fx.backgroundColor }, { it.second }) { c -> studio.updateEffects { it.copy(backgroundColor = c.first) } }
        }
        BackgroundMode.None -> Text("Blur, replace or remove what's behind you. It all runs on the phone; nothing is uploaded.", style = MaterialTheme.typography.bodyMedium)
    }
    if (fx.needsMask) SwitchRow("Refine edges", fx.edgeRefine, "Softer, steadier outline around hair and shoulders") { v -> studio.updateEffects { it.copy(edgeRefine = v) } }
}

// ── Framing ──────────────────────────────────────────────────────────────────

@Composable
fun FramingPanel(studio: Studio) {
    val fx = studio.effects
    SwitchRow("Auto-framing", fx.autoFrame, "Follows your face and keeps you nicely framed") { v -> studio.updateEffects { it.copy(autoFrame = v) } }
    if (!fx.autoFrame) {
        val m = fx.manual
        LabeledSlider("Zoom", m.zoom, 1f..4f, fmtZoom((m.zoom * 10).roundToInt() / 10f)) { v -> studio.updateEffects { it.copy(manual = m.copy(zoom = v)) } }
        LabeledSlider("Left / right", m.panX, -1f..1f, "${(m.panX * 100).roundToInt()}") { v -> studio.updateEffects { it.copy(manual = m.copy(panX = v)) } }
        LabeledSlider("Up / down", m.panY, -1f..1f, "${(m.panY * 100).roundToInt()}") { v -> studio.updateEffects { it.copy(manual = m.copy(panY = v)) } }
    }
    SwitchRow("Mirror", fx.manual.mirror) { v -> studio.updateEffects { it.copy(manual = it.manual.copy(mirror = v)) } }
    SectionTitle("Rotate")
    Choices(listOf(0, 90, 180, 270), fx.manual.rotation, { "$it°" }) { r -> studio.updateEffects { it.copy(manual = it.manual.copy(rotation = r)) } }
    SwitchRow("Vertical 9:16", fx.vertical, "For stories and shorts") { v -> studio.updateEffects { it.copy(vertical = v) } }
    TextButton(onClick = { studio.updateEffects { it.copy(manual = app.kissik.studio.framing.ManualFraming(), autoFrame = false) } }) { Text("Reset framing") }
}

// ── Looks ────────────────────────────────────────────────────────────────────

@Composable
fun LooksPanel(studio: Studio) {
    val fx = studio.effects
    SwitchRow("Auto best picture", fx.autoBest, "Balances exposure, colour and contrast for you") { v -> studio.updateEffects { it.copy(autoBest = v) } }
    SectionTitle("Filter")
    Choices(Filter.entries, fx.filter, { it.label }) { f -> studio.updateEffects { it.copy(filter = f) } }
    LabeledSlider("Skin smoothing", fx.smoothing, 0f..1f, "${(fx.smoothing * 100).roundToInt()}%") { v -> studio.updateEffects { it.copy(smoothing = v) } }
    SectionTitle("Adjust")
    val a = fx.adjust
    fun pct(v: Float) = "${if (v > 0) "+" else ""}${(v * 100).roundToInt()}"
    LabeledSlider("Brightness", a.brightness, -1f..1f, pct(a.brightness)) { v -> studio.updateEffects { it.copy(adjust = a.copy(brightness = v)) } }
    LabeledSlider("Contrast", a.contrast, -1f..1f, pct(a.contrast)) { v -> studio.updateEffects { it.copy(adjust = a.copy(contrast = v)) } }
    LabeledSlider("Saturation", a.saturation, -1f..1f, pct(a.saturation)) { v -> studio.updateEffects { it.copy(adjust = a.copy(saturation = v)) } }
    LabeledSlider("Warmth", a.temperature, -1f..1f, pct(a.temperature)) { v -> studio.updateEffects { it.copy(adjust = a.copy(temperature = v)) } }
    LabeledSlider("Tint", a.tint, -1f..1f, pct(a.tint)) { v -> studio.updateEffects { it.copy(adjust = a.copy(tint = v)) } }
    LabeledSlider("Vignette", a.vignette, 0f..1f, pct(a.vignette)) { v -> studio.updateEffects { it.copy(adjust = a.copy(vignette = v)) } }
    SectionTitle("LUT (.cube)")
    Row(verticalAlignment = Alignment.CenterVertically) {
        OutlinedButton(onClick = { studio.platform.files.pickLut { it?.let { (n, t) -> studio.loadLut(n, t) } } }) { Text(if (studio.lutName == null) "Load a LUT…" else "Change LUT") }
        studio.lutName?.let { Text(it, Modifier.padding(horizontal = 8.dp).weight(1f)); TextButton(onClick = studio::clearLut) { Text("Remove") } }
    }
    if (fx.lut != null) LabeledSlider("LUT strength", fx.lutStrength, 0f..1f, "${(fx.lutStrength * 100).roundToInt()}%") { v -> studio.updateEffects { it.copy(lutStrength = v) } }
    TextButton(onClick = { studio.updateEffects { it.copy(filter = Filter.None, adjust = app.kissik.studio.image.Adjustments(), smoothing = 0f, autoBest = false) } }) { Text("Reset looks") }
}

// ── Scenes ───────────────────────────────────────────────────────────────────

@Composable
fun ScenesPanel(studio: Studio) {
    SectionTitle("Transition")
    Choices(Transition.entries, studio.transition, { it.label }) { studio.transition = it }
    studio.scene?.let { SceneEditor(studio, it) }
    val byCat = studio.scenes.groupBy { it.category }
    byCat.forEach { (cat, list) ->
        SectionTitle(cat)
        Choices(list, studio.scene ?: list.firstOrNull { it.id == "blank" }.takeIf { studio.scene == null }, { it.name }) { studio.selectScene(it) }
    }
}

@Composable
private fun SceneEditor(studio: Studio, scene: SceneDocument) {
    SectionTitle("Edit \"${scene.name}\"")
    scene.layers.forEachIndexed { i, l ->
        if (l.kind == "text") OutlinedTextField(l.text, { t ->
            studio.editScene(scene.copy(layers = scene.layers.toMutableList().also { it[i] = l.copy(text = t) }))
        }, label = { Text(l.name.ifBlank { "Text" }) }, modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp))
        else if (l.kind != "camera") SwitchRow("Show ${l.displayName}", l.visible) { v ->
            studio.editScene(scene.copy(layers = scene.layers.toMutableList().also { it[i] = l.copy(visible = v) }))
        }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = {
            val layer = app.kissik.studio.scenes.SceneLayer(kind = "text", text = "New text", x = 0.05, y = 0.05, w = 0.5, h = 0.08, fontSize = 0.05, bold = true, fill = "#99000000")
            studio.editScene(scene.copy(layers = scene.layers + layer))
        }) { Text("Add text") }
        OutlinedButton(onClick = {
            val layer = app.kissik.studio.scenes.SceneLayer(kind = "shape", name = "Shape", x = 0.05, y = 0.85, w = 0.3, h = 0.08, color = "#CCE8551A", cornerRadius = 0.02)
            studio.editScene(scene.copy(layers = scene.layers + layer))
        }) { Text("Add shape") }
    }
}

// ── Record ───────────────────────────────────────────────────────────────────

@Composable
fun RecordPanel(studio: Studio) {
    val m = studio.platform.media
    val st by m.state.collectAsState()
    var quality by remember { mutableStateOf(Quality.HD) }
    SectionTitle("Quality")
    Choices(listOf(Quality.HD, Quality.UHD), quality, { it.label }) { quality = it }
    SwitchRow("Vertical 9:16", studio.effects.vertical) { v -> studio.updateEffects { it.copy(vertical = v) } }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(vertical = 8.dp)) {
        if (!st.recording) Button(onClick = { m.startRecording(RecordOptions(quality, studio.effects.vertical)) }) { Icon(Icons.Filled.FiberManualRecord, null); Spacer(Modifier.width(6.dp)); Text("Record") }
        else {
            Button(onClick = { m.stopRecording() }) { Icon(Icons.Filled.Stop, null); Spacer(Modifier.width(6.dp)); Text("Stop") }
            OutlinedButton(onClick = { if (st.paused) m.resume() else m.pause() }) { Text(if (st.paused) "Resume" else "Pause") }
        }
        OutlinedButton(onClick = { m.snapshot() }) { Icon(Icons.Filled.PhotoCamera, null); Spacer(Modifier.width(6.dp)); Text("Photo") }
    }
    Text("4K records the camera directly. Effects, scenes and 9:16 record at the processed size.", style = MaterialTheme.typography.bodySmall)
    SectionTitle("Replay buffer")
    SwitchRow("Keep the last ${studio.settings.replaySeconds} seconds", st.replayOn, "Tap Save to keep a moment that just happened") { m.setReplay(it, studio.settings.replaySeconds) }
    if (st.replayOn) Row(verticalAlignment = Alignment.CenterVertically) {
        Text("Buffered ${st.replayBufferedSec}s", Modifier.weight(1f))
        Button(onClick = { m.saveReplay() }) { Text("Save replay") }
    }
    Choices(listOf(15, 30, 60), studio.settings.replaySeconds, { "${it}s" }) { sec -> studio.updateSettings { it.copy(replaySeconds = sec) }; if (st.replayOn) m.setReplay(true, sec) }
}

// ── Live ─────────────────────────────────────────────────────────────────────

@Composable
fun LivePanel(studio: Studio) {
    val live = studio.platform.live
    val states by live.states.collectAsState()
    val dests = studio.settings.destinations
    var settings by remember { mutableStateOf(StreamSettings()) }
    val isLive = states.values.any { it == LinkState.Live || it == LinkState.Connecting }

    dests.forEach { d ->
        Row(Modifier.fillMaxWidth().heightIn(min = Brand.minTouch), verticalAlignment = Alignment.CenterVertically) {
            Checkbox(d.enabled, { on -> studio.updateSettings { s -> s.copy(destinations = s.destinations.map { if (it.id == d.id) it.copy(enabled = on) else it }) } },
                modifier = Modifier.semantics { contentDescription = "Stream to ${d.name}" })
            Column(Modifier.weight(1f)) { Text(d.name); Text(d.masked, style = MaterialTheme.typography.bodySmall) }
            val st = states[d.id] ?: LinkState.Idle
            Text(when (st) { LinkState.Idle -> ""; LinkState.Connecting -> "Connecting…"; LinkState.Live -> "LIVE"; LinkState.Failed -> "Failed" },
                color = if (st == LinkState.Live) Color(0xFFD32F2F) else MaterialTheme.colorScheme.onSurface)
            IconButton(onClick = { studio.updateSettings { s -> s.copy(destinations = s.destinations.filter { it.id != d.id }) } }) { Icon(Icons.Filled.Delete, "Remove ${d.name}") }
        }
    }
    AddDestination { studio.updateSettings { s -> s.copy(destinations = s.destinations + it) } }
    SectionTitle("Video")
    Choices(listOf(720, 1080), settings.shortSide, { "${it}p" }) { settings = settings.copy(shortSide = it, bitrateKbps = StreamSettings.suggestedKbps(it, settings.fps)) }
    Choices(listOf(30, 60), settings.fps, { "$it fps" }) { settings = settings.copy(fps = it, bitrateKbps = StreamSettings.suggestedKbps(settings.shortSide, it)) }
    Choices(VideoCodec.entries.filter { it == VideoCodec.H264 || live.supportsHevc }, settings.codec, { it.label }) { settings = settings.copy(codec = it) }
    Text("${settings.bitrateKbps} kbps, hardware encoder", style = MaterialTheme.typography.bodySmall)
    Spacer(Modifier.height(8.dp))
    Button(onClick = { if (isLive) live.stop() else live.start(dests.filter { it.enabled }, settings) }, enabled = isLive || dests.any { it.enabled },
        colors = if (isLive) ButtonDefaults.buttonColors(containerColor = Color(0xFFD32F2F)) else ButtonDefaults.buttonColors()) {
        Text(if (isLive) "End stream" else "Go live")
    }
}

@Composable
private fun AddDestination(onAdd: (Destination) -> Unit) {
    var name by remember { mutableStateOf("") }
    var url by remember { mutableStateOf("") }
    var key by remember { mutableStateOf("") }
    SectionTitle("Add a destination")
    OutlinedTextField(name, { name = it }, label = { Text("Name") }, modifier = Modifier.fillMaxWidth())
    OutlinedTextField(url, { v ->
        if (key.isEmpty() && v.count { it == '/' } >= 4) { val (u, k) = Rtmp.split(v); url = u; key = k } else url = v
    }, label = { Text("Server (rtmp:// or rtmps://)") }, modifier = Modifier.fillMaxWidth(), isError = url.isNotEmpty() && Rtmp.validate(url) != null,
        supportingText = { if (url.isNotEmpty()) Rtmp.validate(url)?.let { Text(it) } })
    OutlinedTextField(key, { key = it }, label = { Text("Stream key") }, visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth())
    TextButton(enabled = Rtmp.validate(url) == null, onClick = {
        onAdd(Destination(id = url + key.hashCode(), name = name.ifBlank { url.substringAfter("://").substringBefore('/') }, url = url.trim(), key = key.trim()))
        name = ""; url = ""; key = ""
    }) { Text("Add") }
}

// ── Audio ────────────────────────────────────────────────────────────────────

@Composable
fun AudioPanel(studio: Studio) {
    val a = studio.platform.audio
    val mics by a.mics.collectAsState(); val sel by a.selectedMic.collectAsState()
    val level by a.level.collectAsState(); val muted by a.muted.collectAsState(); val ns by a.noiseSuppression.collectAsState()
    DisposableEffect(Unit) { a.startMeter(); onDispose { a.stopMeter() } }
    SectionTitle("Microphone")
    Choices(listOf<app.kissik.studio.platform.Mic?>(null) + mics, mics.firstOrNull { it.id == sel }, { it?.label ?: "Automatic" }) { a.selectMic(it?.id) }
    SectionTitle("Level")
    LinearProgressIndicator(progress = { if (muted) 0f else level }, modifier = Modifier.fillMaxWidth().height(12.dp).clip(RoundedCornerShape(6.dp))
        .semantics { contentDescription = "Microphone level"; stateDescription = if (muted) "Muted" else "${(level * 100).roundToInt()} percent" },
        color = if (level > 0.9f) Color(0xFFD32F2F) else Brand.Orange)
    SwitchRow("Mute", muted) { a.setMuted(it) }
    SwitchRow("Noise suppression", ns, if (a.noiseSuppressionAvailable) "Reduces fans, traffic and keyboard noise" else "Not available on this phone", enabled = a.noiseSuppressionAvailable) { a.setNoiseSuppression(it) }
}

// ── Captions ─────────────────────────────────────────────────────────────────

@Composable
fun CaptionsPanel(studio: Studio) {
    val cs = studio.platform.captions
    SwitchRow("Live captions", studio.captionsOn, "Shown on screen; recognised on the phone") { studio.setCaptions(it) }
    SectionTitle("Engine")
    CaptionEngineKind.entries.forEach { k ->
        val reason = cs.unavailableReason(k)
        Row(Modifier.fillMaxWidth().heightIn(min = Brand.minTouch)
            .selectable(studio.captionEngine == k, enabled = reason == null, role = Role.RadioButton) { studio.captionEngine = k }, verticalAlignment = Alignment.CenterVertically) {
            RadioButton(studio.captionEngine == k, null, enabled = reason == null)
            Column { Text(k.label); Text(reason ?: k.detail, style = MaterialTheme.typography.bodySmall) }
        }
    }
    SectionTitle("Language")
    Choices(listOf("en-US", "en-GB", "en-IN", "hi-IN", "es-ES", "fr-FR", "de-DE"), studio.settings.captionLanguage, { it }) { l -> studio.updateSettings { it.copy(captionLanguage = l) } }
}

// ── Teleprompter ─────────────────────────────────────────────────────────────

@Composable
fun PrompterPanel(studio: Studio) {
    SwitchRow("Show teleprompter", studio.teleprompterOn, "Scrolls over the top of the picture, near the lens") { studio.teleprompterOn = it }
    OutlinedTextField(studio.settings.teleprompterText, { t -> studio.updateSettings { it.copy(teleprompterText = t) } },
        label = { Text("Script") }, modifier = Modifier.fillMaxWidth().heightIn(min = 160.dp))
    val wpm = studio.settings.teleprompterWpm
    LabeledSlider("Speed", wpm.toFloat(), 60f..260f, "$wpm words/min") { v -> studio.updateSettings { it.copy(teleprompterWpm = v.roundToInt()) } }
    val mins = app.kissik.studio.teleprompter.Teleprompter.minutes(studio.settings.teleprompterText, wpm)
    if (mins > 0f) Text("About ${(mins * 10).roundToInt() / 10f} min to read", style = MaterialTheme.typography.bodySmall)
}

// ── Tools: QR + time-lapse ───────────────────────────────────────────────────

@Composable
fun ToolsPanel(studio: Studio) {
    val cam = studio.platform.camera
    var scanning by remember { mutableStateOf(false) }
    val codes = remember { mutableStateListOf<String>() }
    DisposableEffect(scanning) { cam.setBarcodeScanning(scanning); onDispose { cam.setBarcodeScanning(false) } }
    LaunchedEffect(scanning) { if (scanning) cam.barcodes.collect { if (it !in codes) codes.add(0, it) } }
    SectionTitle("QR and barcodes")
    SwitchRow("Read codes", scanning, "Point the camera at a QR code or barcode") { scanning = it }
    OutlinedButton(onClick = { studio.platform.scanner.readCodesFromPicture { found ->
        if (found == null) return@readCodesFromPicture
        if (found.isEmpty()) studio.say("No code found in that picture")
        found.forEach { if (it !in codes) codes.add(0, it) }
    } }) { Text("Read a code from a picture") }
    codes.take(5).forEach { SelectableText(it) }
    // A KISSIK pairing code is picked up straight away for the PC webcam.
    LaunchedEffect(codes.size) { codes.firstOrNull()?.let { if (Pairing.parse(it) != null) { studio.updateSettings { s -> s.copy(pairingLink = it) }; studio.say("Paired with your PC. Open PC webcam to connect.") } } }

    val media = studio.platform.media
    val st by media.state.collectAsState()
    SectionTitle("Time-lapse")
    var interval by remember { mutableStateOf(1000L) }
    Choices(listOf(500L, 1000L, 2000L, 5000L), interval, { "Every ${it / 1000f}s".replace(".0s", "s") }) { interval = it }
    Text("Plays ${TimeLapse(interval).speedUp.roundToInt()}× faster", style = MaterialTheme.typography.bodySmall)
    Button(onClick = { if (st.timeLapse) media.stopTimeLapse() else media.startTimeLapse(TimeLapse(interval)) }) {
        Text(if (st.timeLapse) "Stop (${st.timeLapseFrames} frames)" else "Start time-lapse")
    }
}

@Composable
private fun SelectableText(text: String) {
    androidx.compose.foundation.text.selection.SelectionContainer {
        Text(text, Modifier.fillMaxWidth().padding(vertical = 4.dp).clip(RoundedCornerShape(8.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant).padding(10.dp))
    }
}

// ── PC webcam ────────────────────────────────────────────────────────────────

@Composable
fun WebcamPanel(studio: Studio) {
    val w = studio.platform.webcam
    val state by w.state.collectAsState(); val detail by w.detail.collectAsState()
    var link by remember { mutableStateOf(studio.settings.pairingLink) }
    var usb by remember { mutableStateOf(false) }
    Text("Use this phone as a camera for KISSIK on your PC. On the PC, open the Phone page and scan its code with Tools → Read codes, or paste the link here.",
        style = MaterialTheme.typography.bodyMedium)
    OutlinedTextField(link, { link = it }, label = { Text("Pairing link") }, modifier = Modifier.fillMaxWidth(),
        isError = link.isNotEmpty() && Pairing.parse(link) == null, supportingText = { if (link.isNotEmpty() && Pairing.parse(link) == null) Text("That's not a KISSIK pairing link") })
    SwitchRow("Over USB cable", usb, "Needs adb reverse from the PC") { usb = it }
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        if (state == WebcamState.Off || state == WebcamState.Failed) Button(enabled = Pairing.parse(link) != null, onClick = {
            studio.updateSettings { it.copy(pairingLink = link) }
            w.connect(Pairing.parse(link)!!, usb)
        }) { Text("Connect") } else OutlinedButton(onClick = { w.disconnect() }) { Text("Disconnect") }
        Text(when (state) { WebcamState.Off -> "Not connected"; WebcamState.Connecting -> "Connecting…"; WebcamState.Connected -> "Sending to PC"; WebcamState.Failed -> "Couldn't connect" },
            modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite })
    }
    if (detail.isNotEmpty()) Text(detail, style = MaterialTheme.typography.bodySmall)
}

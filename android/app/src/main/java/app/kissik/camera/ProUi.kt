package app.kissik.camera

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlin.math.exp
import kotlin.math.ln
import kotlin.math.roundToInt

@Composable
private fun Section(title: String, value: String? = null, content: @Composable () -> Unit) {
    Column(Modifier.fillMaxWidth().padding(vertical = 10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(title.uppercase(), color = K.muted, fontSize = 12.sp, letterSpacing = 1.5.sp, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
            if (value != null) Text(value, color = K.text, fontSize = 14.sp)
        }
        Spacer(Modifier.height(8.dp))
        content()
    }
}

@Composable
private fun Pill(text: String, selected: Boolean, onClick: () -> Unit) =
    Box(Modifier.padding(end = 8.dp).clip(CircleShape).background(if (selected) K.accent else K.raised).clickable(onClick = onClick)
        .padding(horizontal = 14.dp, vertical = 8.dp)) { Text(text, color = if (selected) Color.White else K.text, fontSize = 14.sp) }

@Composable
private fun Pills(options: List<Pair<String, String>>, selected: String, onPick: (String) -> Unit) =
    Row(Modifier.horizontalScroll(rememberScrollState())) { options.forEach { (k, label) -> Pill(label, k == selected) { onPick(k) } } }

@Composable
private fun AutoSlider(auto: Boolean, onAuto: (Boolean) -> Unit, value: Float, onValue: (Float) -> Unit) =
    Row(verticalAlignment = Alignment.CenterVertically) {
        Pill("Auto", auto) { onAuto(!auto) }
        Slider(value, onValue, Modifier.weight(1f), enabled = true,
            colors = SliderDefaults.colors(thumbColor = if (auto) K.muted else K.accent, activeTrackColor = if (auto) K.raised else K.accent))
    }

/** Log-scale position (0..1) for ranges like ISO and shutter speed that span several orders of magnitude. */
private fun toPos(v: Double, min: Double, max: Double) = ((ln(v) - ln(min)) / (ln(max) - ln(min))).toFloat().coerceIn(0f, 1f)
private fun fromPos(p: Float, min: Double, max: Double) = exp(ln(min) + p * (ln(max) - ln(min)))

private fun shutterLabel(ns: Long): String {
    val s = ns / 1e9
    return if (s >= 0.3) "%.1f s".format(s) else "1/${(1 / s).roundToInt()} s"
}

/** Every control the open lens supports. Anything it lacks is simply not shown. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProSheet(onClose: () -> Unit) {
    val caps by Pro.caps.collectAsState()
    val lenses by Pro.lenses.collectAsState()
    val lensId by Pro.lensId.collectAsState()
    val front by Live.front.collectAsState()
    val ev by Pro.ev.collectAsState()
    val iso by Pro.iso.collectAsState()
    val shutter by Pro.shutterNs.collectAsState()
    val focus by Pro.focus.collectAsState()
    val wb by Pro.wb.collectAsState()
    val fps by Pro.fps.collectAsState()
    val stab by Pro.stab.collectAsState()
    val hdr by Pro.hdr.collectAsState()
    val codec by Pro.codec.collectAsState()
    val zoom by Live.zoom.collectAsState()
    val effects by Pro.effects.collectAsState()
    val effect by Pro.effect.collectAsState()
    val mics by Mic.devices.collectAsState()
    val micDev by Mic.deviceId.collectAsState()
    val micMode by Mic.mode.collectAsState()
    val ns by Mic.ns.collectAsState()

    ModalBottomSheet(onDismissRequest = onClose, containerColor = K.card, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxWidth().padding(horizontal = 20.dp).padding(bottom = 32.dp).verticalScroll(rememberScrollState())) {
            Text("Camera controls", color = K.text, fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
            Text("KISSIK on your PC can change these too.", color = K.muted, fontSize = 13.sp)
            val c = caps
            if (c == null) {
                Spacer(Modifier.height(16.dp))
                Text("Start streaming to see what this camera can do.", color = K.text)
                return@Column
            }
            if (lenses.size > 1) Section("Lens") {
                val current = lensId ?: lenses.firstOrNull { it.facing == (if (front) "front" else "back") }?.id ?: ""
                Pills(lenses.map { it.id to it.label }, current) { id ->
                    Pro.lensId.value = id; Live.front.value = lenses.first { it.id == id }.facing == "front"
                }
            }
            Section("Zoom", "%.1f×".format(zoom)) {
                val zs = Live.zoomRange.collectAsState().value
                if (zs.second > zs.first) Slider(toPos(zoom.toDouble(), zs.first.toDouble(), zs.second.toDouble()),
                    { Live.zoom.value = fromPos(it, zs.first.toDouble(), zs.second.toDouble()).toFloat() })
                else Text("This lens has no zoom.", color = K.muted, fontSize = 13.sp)
            }
            if (c.evMax > c.evMin) Section("Brightness (EV)", "%+.1f".format(ev * c.evStep)) {
                Slider(ev.toFloat(), { Pro.ev.value = it.roundToInt() }, valueRange = c.evMin.toFloat()..c.evMax.toFloat(),
                    steps = (c.evMax - c.evMin - 1).coerceAtLeast(0), enabled = iso == null && shutter == null)
            }
            if (c.manual) {
                Section("ISO", iso?.toString() ?: "Auto") {
                    AutoSlider(iso == null, { a -> Pro.iso.value = if (a) null else 400.coerceIn(c.isoMin, c.isoMax) },
                        toPos((iso ?: 400).toDouble(), c.isoMin.toDouble(), c.isoMax.toDouble())) {
                        Pro.iso.value = fromPos(it, c.isoMin.toDouble(), c.isoMax.toDouble()).roundToInt()
                    }
                }
                // Longer than 1/4 s would stall the video, so the slider stops there.
                val maxShutter = minOf(c.shutterMax, 250_000_000L).toDouble()
                Section("Shutter", shutter?.let(::shutterLabel) ?: "Auto") {
                    AutoSlider(shutter == null, { a -> Pro.shutterNs.value = if (a) null else 16_666_667L.coerceIn(c.shutterMin, c.shutterMax) },
                        toPos((shutter ?: 16_666_667L).toDouble(), c.shutterMin.toDouble(), maxShutter)) {
                        Pro.shutterNs.value = fromPos(it, c.shutterMin.toDouble(), maxShutter).toLong()
                    }
                }
            }
            if (c.minFocus > 0f) Section("Focus", focus?.let { if (it < 0.02f) "Far" else "%.0f%%".format(it * 100) } ?: "Auto · tap the picture") {
                AutoSlider(focus == null, { a -> Pro.focus.value = if (a) null else 0.5f }, focus ?: 0.5f) { Pro.focus.value = it }
            }
            if (c.wbModes.size > 1) Section("White balance") {
                Pills(c.wbModes.map { it to it.replace('-', ' ').replaceFirstChar(Char::uppercase) }, wb) { Pro.wb.value = it }
            }
            if (c.fpsChoices.size > 1) Section("Frame rate") {
                Pills(listOf("0" to "Auto") + c.fpsChoices.map { "$it" to "$it fps" }, "$fps") { Pro.fps.value = it.toInt() }
            }
            if (c.videoStab || c.ois) Section("Stabilization") {
                Pills(listOf("on" to "On", "off" to "Off"), if (stab) "on" else "off") { Pro.stab.value = it == "on" }
            }
            if (c.hdr) Section("HDR") {
                Pills(listOf("on" to "On", "off" to "Off"), if (hdr) "on" else "off") { Pro.hdr.value = it == "on" }
            }
            if (effects.isNotEmpty()) Section("Effects", "From your phone's camera") {
                val names = mapOf("none" to "None", "bokeh" to "Background blur", "night" to "Night", "hdr" to "HDR", "face" to "Face retouch", "auto" to "Auto")
                Pills((listOf("none") + effects).map { it to (names[it] ?: it) }, effect) { Pro.effect.value = it }
            }
            Section("Microphone") {
                if (mics.size > 1) {
                    Pills(listOf("auto" to "Default") + mics.map { "${it.id}" to it.label }, micDev?.toString() ?: "auto") {
                        Mic.deviceId.value = it.toIntOrNull()
                    }
                    Spacer(Modifier.height(8.dp))
                }
                Pills(listOf("standard" to "Standard", "voice" to "Voice (cancels echo)", "camera" to "Camera mic", "raw" to "Raw"), micMode) { Mic.mode.value = it }
                if (Mic.nsAvailable) {
                    Spacer(Modifier.height(8.dp))
                    Pills(listOf("off" to "Noise reduction off", "on" to "Noise reduction on"), if (ns) "on" else "off") { Mic.ns.value = it == "on" }
                }
            }
            Section("Video sent to the PC", if (codec == "h264") "H.264 (hardware)" else "JPEG") {
                Text(if (codec == "h264") "Encoded by the phone's video chip: smooth and cool even at 4K." else
                    "KISSIK picks this. Hardware H.264 is used when your KISSIK version supports it.", color = K.muted, fontSize = 13.sp)
            }
            if (iso != null || shutter != null || focus != null || ev != 0 || wb != "auto" || fps != 0 || effect != "none") {
                Spacer(Modifier.height(8.dp))
                TextButton({ Pro.resetManual(); Pro.wb.value = "auto"; Pro.fps.value = 0; Pro.effect.value = "none" }) { Text("Reset to automatic") }
            }
        }
    }
}

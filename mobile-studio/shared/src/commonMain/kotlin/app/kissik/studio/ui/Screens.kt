package app.kissik.studio.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.kissik.studio.Studio
import app.kissik.studio.ThemeChoice
import app.kissik.studio.UiMode
import app.kissik.studio.crash.CrashConsent
import app.kissik.studio.platform.ScanResult

/** The framing-hands logo, drawn from the same geometry as docs/logo/kissik-logo.svg. */
@Composable
fun KissikLogo(modifier: Modifier = Modifier) {
    Canvas(modifier.semantics { contentDescription = "KISSIK logo" }) {
        val s = size.minDimension / 512f
        drawRoundRect(Brand.gradient, cornerRadius = androidx.compose.ui.geometry.CornerRadius(112 * s))
        val st = Stroke(40 * s, cap = StrokeCap.Round, join = StrokeJoin.Round)
        drawPath(Path().apply { moveTo(262 * s, 108 * s); lineTo(404 * s, 108 * s); lineTo(404 * s, 250 * s) }, Color.White, style = st)
        drawPath(Path().apply { moveTo(108 * s, 262 * s); lineTo(108 * s, 404 * s); lineTo(250 * s, 404 * s) }, Color.White, style = st)
        drawCircle(Color.White, 92.21f * s, Offset(256 * s, 256 * s))
        drawCircle(Color(0xFF2B1A14), 71.53f * s, Offset(256 * s, 256 * s))
        drawCircle(Color(0xFF5C2E1C), 31.85f * s, Offset(257.12f * s, 250.41f * s))
        drawCircle(Color.White, 12.29f * s, Offset(229.18f * s, 225.26f * s))
    }
}

@Composable
fun Onboarding(studio: Studio, done: () -> Unit) {
    var page by remember { mutableStateOf(0) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Spacer(Modifier.height(32.dp))
        KissikLogo(Modifier.size(120.dp))
        Spacer(Modifier.height(16.dp))
        when (page) {
            0 -> {
                Text("Welcome to KISSIK", style = MaterialTheme.typography.headlineMedium, modifier = Modifier.semantics { heading() })
                Spacer(Modifier.height(12.dp))
                Text("A camera studio in your pocket: backgrounds, looks, framing, scenes, recording, live streaming, captions and a teleprompter.", textAlign = TextAlign.Center)
                Spacer(Modifier.height(16.dp))
                Bullet(Icons.Filled.Lock, "Everything runs on this phone. Your picture and voice never leave it unless you stream or connect to your own PC.")
                Bullet(Icons.Filled.Accessibility, "Works with TalkBack and VoiceOver, larger text, high contrast and reduced motion.")
            }
            1 -> {
                Text("How much do you want to see?", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.semantics { heading() })
                Spacer(Modifier.height(12.dp))
                UiMode.entries.forEach { m ->
                    Card(onClick = { studio.updateSettings { it.copy(mode = m) } }, modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp)
                        .semantics { selected = studio.settings.mode == m },
                        colors = if (studio.settings.mode == m) CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer) else CardDefaults.cardColors()) {
                        Column(Modifier.padding(16.dp)) { Text(m.label, fontWeight = FontWeight.Bold); Text(m.detail) }
                    }
                }
                Text("You can switch any time with the Simple / Studio button.", style = MaterialTheme.typography.bodySmall)
            }
            else -> {
                Text("Crash reports", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.semantics { heading() })
                Spacer(Modifier.height(12.dp))
                Text("If KISSIK ever closes unexpectedly, may it send the error text to help fix it? Names, paths and e-mail addresses are removed. Never pictures, sound or files.", textAlign = TextAlign.Center)
                Spacer(Modifier.height(12.dp))
                listOf(CrashConsent.AskEachTime to "Ask me each time", CrashConsent.Always to "Send automatically", CrashConsent.Never to "Never send").forEach { (c, label) ->
                    Row(Modifier.fillMaxWidth().heightIn(min = Brand.minTouch)
                        .selectable(studio.settings.crashConsent == c, role = Role.RadioButton) { studio.updateSettings { it.copy(crashConsent = c) } }, verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(studio.settings.crashConsent == c, null); Text(label)
                    }
                }
            }
        }
        Spacer(Modifier.height(24.dp))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            if (page > 0) TextButton(onClick = { page-- }) { Text("Back") } else Spacer(Modifier)
            Button(onClick = {
                if (page < 2) page++ else {
                    studio.updateSettings { it.copy(onboarded = true, crashConsent = if (it.crashConsent == CrashConsent.NotAsked) CrashConsent.AskEachTime else it.crashConsent) }
                    done()
                }
            }) { Text(if (page < 2) "Next" else "Start") }
        }
        Text("Step ${page + 1} of 3", style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 8.dp))
    }
}

@Composable
private fun Bullet(icon: androidx.compose.ui.graphics.vector.ImageVector, text: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.Top) {
        Icon(icon, null, tint = MaterialTheme.colorScheme.primary); Spacer(Modifier.width(12.dp)); Text(text)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(studio: Studio, back: () -> Unit) {
    val s = studio.settings
    val sys = studio.platform.system
    Scaffold(topBar = {
        TopAppBar(title = { Text("Settings") }, navigationIcon = { IconButton(onClick = back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") } })
    }) { pad ->
        Column(Modifier.padding(pad).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp)) {
            SectionTitle("Mode")
            Choices(UiMode.entries, s.mode, { it.label }) { m -> studio.updateSettings { it.copy(mode = m) } }
            SectionTitle("Look")
            Choices(ThemeChoice.entries, s.theme, { it.label }) { t -> studio.updateSettings { it.copy(theme = t) } }
            SectionTitle("Accessibility")
            SwitchRow("High contrast", s.highContrast, if (sys.highContrast) "Also on because the phone asks for it" else null) { v -> studio.updateSettings { it.copy(highContrast = v) } }
            SwitchRow("Larger text", s.largeText, "On top of the phone's own text size") { v -> studio.updateSettings { it.copy(largeText = v) } }
            SwitchRow("Reduce motion", studio.reducedMotion, if (s.reduceMotion == null) "Following the phone's setting" else null) { v -> studio.updateSettings { it.copy(reduceMotion = v) } }
            if (s.reduceMotion != null) TextButton(onClick = { studio.updateSettings { it.copy(reduceMotion = null) } }) { Text("Follow the phone's setting") }
            SectionTitle("Privacy")
            Text("All picture and sound processing happens on this phone: backgrounds, looks, framing, captions, scanning and text recognition. KISSIK has no account and no analytics. Video only leaves the phone when you go live to a server you chose or connect to your own PC.",
                style = MaterialTheme.typography.bodyMedium)
            SectionTitle("Crash reports")
            listOf(CrashConsent.AskEachTime to "Ask me each time", CrashConsent.Always to "Send automatically", CrashConsent.Never to "Never send").forEach { (c, label) ->
                Row(Modifier.fillMaxWidth().heightIn(min = Brand.minTouch)
                    .selectable(s.crashConsent == c, role = Role.RadioButton) { studio.updateSettings { it.copy(crashConsent = c) } }, verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(s.crashConsent == c, null); Text(label)
                }
            }
            TextButton(onClick = { sys.openPrivacySettings() }) { Text("Camera and microphone permissions") }
            SectionTitle("About")
            Text("KISSIK ${sys.appVersion} on ${sys.osName} ${sys.osVersion}")
            Text("Open source. Built with permissively licensed components.", style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { studio.updateSettings { it.copy(onboarded = false) } }) { Text("Show the welcome again next time") }
            Spacer(Modifier.height(24.dp))
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ScannerScreen(studio: Studio, back: () -> Unit) {
    val scanner = studio.platform.scanner
    var result by remember { mutableStateOf<ScanResult?>(null) }
    var text by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    Scaffold(topBar = {
        TopAppBar(title = { Text("Document scanner") }, navigationIcon = { IconButton(onClick = back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") } })
    }) { pad ->
        Column(Modifier.padding(pad).verticalScroll(rememberScrollState()).padding(20.dp)) {
            Text("Finds the page edges, straightens it, cleans it up to crisp black-and-white or colour, and saves several pages as one PDF. Text recognition runs on the phone.")
            Spacer(Modifier.height(16.dp))
            Button(onClick = { busy = true; scanner.scan { r -> busy = false; if (r != null) { result = r; text = null } } }, enabled = !busy,
                modifier = Modifier.heightIn(min = Brand.minTouch)) {
                Icon(Icons.Filled.DocumentScanner, null); Spacer(Modifier.width(8.dp)); Text(if (result == null) "Scan pages" else "Scan again")
            }
            result?.let { r ->
                SectionTitle("${r.pages.size} page${if (r.pages.size == 1) "" else "s"}")
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    r.pdfPath?.let { pdf -> OutlinedButton(onClick = { scanner.share(pdf, "application/pdf") }) { Text("Share PDF") } }
                    OutlinedButton(onClick = {
                        busy = true
                        val all = StringBuilder(); var left = r.pages.size
                        r.pages.forEach { pg -> scanner.recognizeText(pg) { t -> if (t != null) all.append(t).append("\n\n"); if (--left == 0) { busy = false; text = all.toString().trim() } } }
                    }, enabled = !busy && r.pages.isNotEmpty()) { Text("Recognise text") }
                }
            }
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth().padding(vertical = 8.dp))
            text?.let { t ->
                SectionTitle("Text")
                androidx.compose.foundation.text.selection.SelectionContainer {
                    Text(t.ifBlank { "No text found." }, Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).background(MaterialTheme.colorScheme.surfaceVariant).padding(12.dp))
                }
                if (t.isNotBlank()) TextButton(onClick = {
                    studio.platform.files.writeAppFile("scan.txt", t)
                    scanner.share("scan.txt", "text/plain")
                }) { Text("Share text") }
            }
        }
    }
}

package app.kissik.studio.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.kissik.studio.Studio
import app.kissik.studio.UiMode
import app.kissik.studio.platform.StudioPlatform
import app.kissik.studio.recording.RecordingClock
import app.kissik.studio.platform.RecordOptions
import kotlinx.coroutines.delay

/** Entry point shared by Android (`setContent { App(platform) }`) and iOS (`ComposeUIViewController { App(platform) }`). */
@Composable
fun App(platform: StudioPlatform) {
    val studio = remember { Studio(platform) }
    val s = studio.settings
    KissikTheme(s.theme, studio.highContrast, s.largeText) {
        Surface(Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.background) {
            var screen by remember { mutableStateOf(if (s.onboarded) Screen.Studio else Screen.Onboarding) }
            when (screen) {
                Screen.Onboarding -> Onboarding(studio) { screen = Screen.Studio }
                Screen.Studio -> StudioScreen(studio, open = { screen = it })
                Screen.Settings -> SettingsScreen(studio) { screen = Screen.Studio }
                Screen.Scanner -> ScannerScreen(studio) { screen = Screen.Studio }
            }
            studio.pendingCrash?.let { CrashDialog(studio, it) }
        }
    }
}

enum class Screen { Onboarding, Studio, Settings, Scanner }

enum class Panel(val label: String, val icon: ImageVector, val simple: Boolean = false) {
    Camera("Camera", Icons.Filled.Camera),
    Background("Background", Icons.Filled.Wallpaper, simple = true),
    Framing("Framing", Icons.Filled.CenterFocusStrong, simple = true),
    Looks("Looks", Icons.Filled.AutoAwesome, simple = true),
    Scenes("Scenes", Icons.Filled.Layers),
    Record("Record", Icons.Filled.Videocam),
    Live("Go live", Icons.Filled.Podcasts),
    Audio("Audio", Icons.Filled.Mic),
    Captions("Captions", Icons.Filled.ClosedCaption),
    Prompter("Prompter", Icons.Filled.TextSnippet),
    Tools("Tools", Icons.Filled.QrCodeScanner),
    Webcam("PC webcam", Icons.Filled.Computer),
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun StudioScreen(studio: Studio, open: (Screen) -> Unit) {
    val p = studio.platform
    val cam by p.camera.state.collectAsState()
    val media by p.media.state.collectAsState()
    var panel by remember { mutableStateOf<Panel?>(null) }
    val snackbar = remember { SnackbarHostState() }
    val studioMode = studio.settings.mode == UiMode.Studio

    DisposableEffect(Unit) { p.camera.start(); p.camera.setEffects(studio.effects); p.camera.setScene(studio.scene); onDispose { p.camera.stop() } }
    LaunchedEffect(studio.message) { studio.message?.let { snackbar.showSnackbar(it); studio.messageShown() } }

    Scaffold(snackbarHost = { SnackbarHost(snackbar) }, containerColor = Color.Black) { pad ->
        Column(Modifier.fillMaxSize().padding(pad)) {
            // Top bar.
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = { open(Screen.Settings) }) { Icon(Icons.Filled.Settings, "Settings", tint = Color.White) }
                Text("KISSIK", color = Color.White, fontWeight = FontWeight.Bold, modifier = Modifier.semantics { heading() })
                Spacer(Modifier.weight(1f))
                if (media.recording) RecChip(media.paused, media.elapsedMs)
                if (media.replayOn) AssistChip(onClick = { p.media.saveReplay() }, label = { Text("Save ${media.replaySeconds}s", color = Color.White) },
                    leadingIcon = { Icon(Icons.Filled.Replay, null, tint = Color.White) },
                    modifier = Modifier.padding(horizontal = 4.dp).semantics { contentDescription = "Save the last ${media.replaySeconds} seconds" })
                FilterChip(selected = studioMode, onClick = {
                    studio.updateSettings { it.copy(mode = if (studioMode) UiMode.Simple else UiMode.Studio) }
                }, label = { Text(if (studioMode) "Studio" else "Simple", color = if (studioMode) Color.Unspecified else Color.White) },
                    modifier = Modifier.semantics { stateDescription = if (studioMode) "Studio mode" else "Simple mode" })
                if (cam.hasTorch) IconButton(onClick = { p.camera.setTorch(!cam.torch) }) {
                    Icon(if (cam.torch) Icons.Filled.FlashOn else Icons.Filled.FlashOff, if (cam.torch) "Turn light off" else "Turn light on", tint = Color.White)
                }
                IconButton(onClick = { p.camera.switchFacing() }) { Icon(Icons.Filled.Cameraswitch, "Switch camera", tint = Color.White) }
            }

            // Viewfinder with overlays.
            // The overlay box takes the picture's own shape, so scene layers sit where they land in recordings.
            val aspect = when {
                studio.effects.vertical -> 9f / 16f
                cam.width > 0 && cam.height > 0 -> cam.width.toFloat() / cam.height
                else -> 3f / 4f
            }
            Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
            Box(Modifier.aspectRatio(aspect, matchHeightConstraintsFirst = aspect < 1f).clip(RoundedCornerShape(12.dp)).background(Color.Black)) {
                p.camera.Viewfinder(Modifier.fillMaxSize().pointerInput(Unit) {
                    detectTapGestures { o -> p.camera.tapFocus(o.x / size.width, o.y / size.height) }
                }.semantics { contentDescription = "Camera picture. Tap to focus." })
                SceneOverlay(studio.scene, studio.previousScene, studio.transition, studio.transitionProgress, Modifier.fillMaxSize())
                if (studio.teleprompterOn) TeleprompterOverlay(studio, Modifier.align(Alignment.TopCenter))
                if (studio.captionsOn && studio.captionLines.isNotEmpty()) CaptionsOverlay(studio.captionLines, Modifier.align(Alignment.BottomCenter))
                if (!cam.ready) Text(cam.error ?: "Starting the camera…", color = Color.White, modifier = Modifier.align(Alignment.Center).padding(24.dp),
                    textAlign = TextAlign.Center)
                if (studioMode && cam.zoomMax > cam.zoomMin) ZoomPills(cam.zoom, cam.zoomMin, cam.zoomMax, { p.camera.setZoom(it) }, Modifier.align(Alignment.BottomCenter).padding(bottom = 64.dp))
            }
            }

            // Tools.
            val tools = Panel.entries.filter { studioMode || it.simple }
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(vertical = 6.dp, horizontal = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                tools.forEach { t ->
                    ToolButton(t.label, t.icon, selected = panel == t) { panel = t }
                }
                if (studioMode) ToolButton("Scan", Icons.Filled.DocumentScanner, false) { open(Screen.Scanner) }
            }
            // Capture row.
            Row(Modifier.fillMaxWidth().padding(bottom = 12.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                RoundAction(Icons.Filled.PhotoCamera, "Take photo") { p.media.snapshot() }
                RecordButton(media.recording) {
                    if (media.recording) p.media.stopRecording()
                    else p.media.startRecording(RecordOptions(vertical = studio.effects.vertical))
                }
                if (media.recording) RoundAction(if (media.paused) Icons.Filled.PlayArrow else Icons.Filled.Pause, if (media.paused) "Resume recording" else "Pause recording") {
                    if (media.paused) p.media.resume() else p.media.pause()
                } else RoundAction(Icons.Filled.StayCurrentPortrait, if (studio.effects.vertical) "Switch to landscape 16:9" else "Switch to vertical 9:16",
                    selected = studio.effects.vertical) { studio.updateEffects { it.copy(vertical = !it.vertical) } }
            }
        }
    }

    panel?.let { current ->
        ModalBottomSheet(onDismissRequest = { panel = null }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
            Column(Modifier.fillMaxWidth().heightIn(max = 560.dp).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp).padding(bottom = 24.dp)) {
                Text(current.label, style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() }.padding(bottom = 8.dp))
                when (current) {
                    Panel.Camera -> CameraPanel(studio)
                    Panel.Background -> BackgroundPanel(studio)
                    Panel.Framing -> FramingPanel(studio)
                    Panel.Looks -> LooksPanel(studio)
                    Panel.Scenes -> ScenesPanel(studio)
                    Panel.Record -> RecordPanel(studio)
                    Panel.Live -> LivePanel(studio)
                    Panel.Audio -> AudioPanel(studio)
                    Panel.Captions -> CaptionsPanel(studio)
                    Panel.Prompter -> PrompterPanel(studio)
                    Panel.Tools -> ToolsPanel(studio)
                    Panel.Webcam -> WebcamPanel(studio)
                }
            }
        }
    }
}

@Composable
private fun RecChip(paused: Boolean, elapsed: Long) {
    var now by remember { mutableStateOf(elapsed) }
    LaunchedEffect(elapsed) { now = elapsed }
    Row(Modifier.padding(horizontal = 6.dp).clip(RoundedCornerShape(50)).background(if (paused) Color.DarkGray else Color(0xFFD32F2F))
        .padding(horizontal = 10.dp, vertical = 4.dp).semantics(mergeDescendants = true) {
            liveRegion = LiveRegionMode.Polite
            contentDescription = if (paused) "Recording paused" else "Recording"
        }, verticalAlignment = Alignment.CenterVertically) {
        Text(if (paused) "PAUSED" else "REC", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 12.sp)
        Spacer(Modifier.width(6.dp))
        Text(RecordingClock.format(now), color = Color.White, fontSize = 12.sp)
    }
}

@Composable
fun ToolButton(label: String, icon: ImageVector, selected: Boolean, onClick: () -> Unit) {
    FilledTonalButton(onClick = onClick, modifier = Modifier.heightIn(min = Brand.minTouch).semantics { role = Role.Button },
        contentPadding = PaddingValues(horizontal = 12.dp),
        colors = if (selected) ButtonDefaults.filledTonalButtonColors(containerColor = MaterialTheme.colorScheme.primaryContainer) else ButtonDefaults.filledTonalButtonColors()) {
        Icon(icon, null, Modifier.size(18.dp)); Spacer(Modifier.width(6.dp)); Text(label)
    }
}

@Composable
private fun RoundAction(icon: ImageVector, label: String, selected: Boolean = false, onClick: () -> Unit) {
    FilledIconButton(onClick = onClick, modifier = Modifier.size(56.dp).semantics { if (selected) stateDescription = "On" },
        colors = IconButtonDefaults.filledIconButtonColors(containerColor = if (selected) Brand.Orange else Color(0x33FFFFFF))) {
        Icon(icon, label, tint = Color.White)
    }
}

@Composable
private fun RecordButton(recording: Boolean, onClick: () -> Unit) {
    Box(Modifier.size(76.dp).clip(CircleShape).background(Color.White).padding(5.dp).clip(CircleShape)
        .background(if (recording) Color(0xFFD32F2F) else Brand.Orange), contentAlignment = Alignment.Center) {
        IconButton(onClick = onClick, modifier = Modifier.fillMaxSize()) {
            Icon(if (recording) Icons.Filled.Stop else Icons.Filled.FiberManualRecord, if (recording) "Stop recording" else "Start recording", tint = Color.White, modifier = Modifier.size(36.dp))
        }
    }
}

@Composable
private fun ZoomPills(zoom: Float, min: Float, max: Float, set: (Float) -> Unit, modifier: Modifier) {
    val stops = listOf(0.5f, 1f, 2f, 5f).filter { it in min..max }
    Row(modifier.clip(RoundedCornerShape(50)).background(Color(0x66000000)).padding(4.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        stops.forEach { z ->
            val on = kotlin.math.abs(zoom - z) < 0.05f
            TextButton(onClick = { set(z) }, modifier = Modifier.sizeIn(minWidth = 48.dp, minHeight = 48.dp).semantics { contentDescription = "Zoom ${fmtZoom(z)}"; if (on) stateDescription = "Selected" }) {
                Text(fmtZoom(z), color = if (on) Brand.OrangeLight else Color.White, fontWeight = if (on) FontWeight.Bold else FontWeight.Normal)
            }
        }
    }
}

fun fmtZoom(z: Float) = if (z % 1f == 0f) "${z.toInt()}×" else "${(z * 10).toInt() / 10f}×"

@Composable
private fun CaptionsOverlay(lines: List<String>, modifier: Modifier) {
    Column(modifier.padding(16.dp).clip(RoundedCornerShape(8.dp)).background(Color(0xCC000000)).padding(horizontal = 12.dp, vertical = 8.dp)
        .semantics { liveRegion = LiveRegionMode.Polite }) {
        lines.forEach { Text(it, color = Color.White, fontSize = 18.sp, textAlign = TextAlign.Center) }
    }
}

@Composable
private fun TeleprompterOverlay(studio: Studio, modifier: Modifier) {
    val text = studio.settings.teleprompterText.ifBlank { "Write your script in Prompter. It scrolls here while you talk." }
    val prompter = remember { app.kissik.studio.teleprompter.Teleprompter() }
    prompter.wordsPerMinute = studio.settings.teleprompterWpm
    val scroll = rememberScrollState()
    var playing by remember { mutableStateOf(false) }
    LaunchedEffect(playing, text) {
        prompter.playing = playing
        val words = app.kissik.studio.teleprompter.Teleprompter.wordCount(text)
        while (playing) {
            delay(33)
            val off = prompter.tick(0.033f, scroll.maxValue.toFloat().coerceAtLeast(1f), words)
            scroll.scrollTo(off.toInt())
            if (!prompter.playing) playing = false
        }
    }
    Column(modifier.fillMaxWidth().height(170.dp).background(Color(0xB3000000))) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { playing = !playing }) { Icon(if (playing) Icons.Filled.Pause else Icons.Filled.PlayArrow, if (playing) "Pause script" else "Scroll script", tint = Color.White) }
            IconButton(onClick = { playing = false; prompter.reset() ; }) { Icon(Icons.Filled.Replay, "Back to the start", tint = Color.White) }
            Text("${studio.settings.teleprompterWpm} words/min", color = Color.White)
        }
        Text(text, color = Color.White, fontSize = 24.sp, lineHeight = 32.sp, textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth().verticalScroll(scroll).padding(horizontal = 24.dp, vertical = 8.dp))
        LaunchedEffect(prompter.offsetPx) { if (prompter.offsetPx == 0f) scroll.scrollTo(0) }
    }
}

@Composable
private fun CrashDialog(studio: Studio, report: String) {
    var remember by remember { mutableStateOf(true) }
    AlertDialog(
        onDismissRequest = { studio.answerCrash(send = false, remember = false) },
        title = { Text("KISSIK closed unexpectedly") },
        text = {
            Column {
                Text("Send a crash report to help fix it? It holds only the error text below (names, paths and e-mail addresses removed), the app version and the system version. Never pictures, sound or files.")
                Spacer(Modifier.height(8.dp))
                Text(report.take(600), style = MaterialTheme.typography.bodySmall, modifier = Modifier.heightIn(max = 160.dp).verticalScroll(rememberScrollState()))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(remember, { remember = it }); Text("Remember my answer")
                }
            }
        },
        confirmButton = { TextButton(onClick = { studio.answerCrash(true, remember) }) { Text("Send") } },
        dismissButton = { TextButton(onClick = { studio.answerCrash(false, remember) }) { Text("Don't send") } },
    )
}

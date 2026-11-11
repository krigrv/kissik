package app.kissik.camera

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.stateDescription
import androidx.core.content.ContextCompat
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import java.util.concurrent.Executors

private enum class Screen { Intro, Home, Scan, Settings }

class MainActivity : ComponentActivity() {
    /** A pairing link handed in from outside (adb `--es link …`, or a shared/opened URL). */
    private val incoming = mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Live.load(this)
        // A rebuilt activity gets its old launch intent back: only a fresh launch carries a new link.
        if (savedInstanceState == null) takeLink(intent)
        setContent { MaterialTheme(colorScheme = KColors) { App(incoming) } }
        // Screen dimming: 15 s after the last touch while streaming, the screen goes nearly dark (less heat and
        // battery); any touch lights it again. Off when not streaming or when the PC turns it off.
        lifecycleScope.launch {
            while (true) {
                kotlinx.coroutines.delay(1000)
                val streaming = Live.link.value == Link.Wifi || Live.link.value == Link.Usb
                val dark = streaming && Live.dim.value && System.currentTimeMillis() - lastTouch > 15_000
                val want = if (dark) 0.01f else android.view.WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
                if (window.attributes.screenBrightness != want) window.attributes = window.attributes.also { it.screenBrightness = want }
            }
        }
    }

    private var lastTouch = System.currentTimeMillis()
    override fun onUserInteraction() { super.onUserInteraction(); lastTouch = System.currentTimeMillis() }

    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); takeLink(intent) }

    private fun takeLink(i: Intent?) {
        incoming.value = i?.getStringExtra("link") ?: i?.getStringExtra(Intent.EXTRA_TEXT) ?: i?.dataString ?: return
        setIntent(Intent(this, MainActivity::class.java)) // used once: never replayed later
    }
}

@Composable
private fun App(incoming: MutableState<String?>) {
    val ctx = LocalContext.current
    val prefs = remember { Prefs.of(ctx) }
    var pairing by remember { mutableStateOf(Pairing.load(ctx)) }
    var screen by remember { mutableStateOf(if (prefs.getBoolean("onboarded", false)) Screen.Home else Screen.Intro) }
    var error by remember { mutableStateOf<String?>(null) }

    fun pairWith(link: String) {
        val p = Pairing.parse(link) ?: run { error = "That isn't a KISSIK pairing code. Open Phone in KISSIK on your PC and scan the code it shows."; return }
        // A scan means "trust this PC now": its certificate is pinned afresh on the next connect.
        p.copy(pin = null).save(ctx)
        pairing = Pairing.load(ctx)
        prefs.edit().putBoolean("onboarded", true).apply()
        StreamService.reconnect(ctx)
        screen = Screen.Home
    }

    // A link handed over by another app could point anywhere, so it needs the user's yes before this phone streams
    // its camera and mic there. Codes the user scans or pastes in this app are already their choice.
    var asked by remember { mutableStateOf<Pairing?>(null) }
    LaunchedEffect(incoming.value) {
        incoming.value?.let { link ->
            if (incoming.value == link) incoming.value = null
            val p = Pairing.parse(link)
            when {
                p == null -> error = "That isn't a KISSIK pairing code. Open Phone in KISSIK on your PC and scan the code it shows."
                !isLocalNetworkHost(p.host) -> error = "That link points to ${p.host}, which isn't a PC on your Wi-Fi. To pair, scan the code KISSIK shows on your PC."
                else -> asked = p
            }
        }
    }
    // Connect every time: a paired phone starts streaming as soon as the app opens.
    LaunchedEffect(Unit) {
        if (incoming.value == null && pairing != null && hasCamera(ctx) && !Live.running.value && prefs.getBoolean("onboarded", false)) StreamService.start(ctx)
    }
    // The service saves the certificate pin on first connect; re-read so screens show the current pairing.
    LaunchedEffect(screen) { pairing = Pairing.load(ctx) }
    BackHandler(screen == Screen.Scan || screen == Screen.Settings) { screen = Screen.Home }

    when (screen) {
        Screen.Intro -> Intro { prefs.edit().putBoolean("onboarded", true).apply(); screen = Screen.Scan }
        Screen.Home -> Home(pairing, onSettings = { screen = Screen.Settings }, onPair = { screen = Screen.Scan })
        Screen.Scan -> Scan(onBack = { screen = Screen.Home }, onCode = ::pairWith)
        Screen.Settings -> Settings(pairing, onClose = { screen = Screen.Home }, onPair = { screen = Screen.Scan }) {
            StreamService.stop(ctx); forgetPairing(ctx); pairing = null
        }
    }

    asked?.let { p ->
        AlertDialog(onDismissRequest = { asked = null }, containerColor = K.card,
            title = { Text("Pair with ${p.host}?") },
            text = { Text("Another app opened a pairing link. If you pair, this phone sends its camera and microphone to ${p.host}. Only pair if that's the PC running KISSIK.") },
            confirmButton = { TextButton({ asked = null; pairWith("https://${p.host}:${p.port}/?t=${p.token}") }) { Text("Pair") } },
            dismissButton = { TextButton({ asked = null }) { Text("Cancel") } })
    }

    error?.let { msg ->
        AlertDialog(onDismissRequest = { error = null }, confirmButton = { TextButton({ error = null }) { Text("OK") } },
            title = { Text("Couldn't pair") }, text = { Text(msg) }, containerColor = K.card)
    }
}

private fun hasCamera(ctx: android.content.Context) =
    ContextCompat.checkSelfPermission(ctx, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

/**
 * After a "Don't allow", Android stops showing its question, so asking again does nothing. When that has happened,
 * open this app's page in the phone's Settings, where the permission can be switched on, and say what to do there.
 */
private fun openSettingsIfBlocked(ctx: android.content.Context, permission: String, what: String) {
    val activity = ctx as? android.app.Activity ?: return
    if (androidx.core.app.ActivityCompat.shouldShowRequestPermissionRationale(activity, permission)) return // Android will ask again
    android.widget.Toast.makeText(ctx, "Turn on $what for KISSIK Camera under Permissions.", android.widget.Toast.LENGTH_LONG).show()
    runCatching {
        ctx.startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.fromParts("package", ctx.packageName, null)))
    }
}

/** Runs [onResume] each time the app comes back to the front (for example from the phone's Settings). */
@Composable
private fun OnResume(onResume: () -> Unit) {
    val owner = LocalLifecycleOwner.current
    DisposableEffect(owner) {
        val observer = androidx.lifecycle.LifecycleEventObserver { _, e -> if (e == androidx.lifecycle.Lifecycle.Event.ON_RESUME) onResume() }
        owner.lifecycle.addObserver(observer)
        onDispose { owner.lifecycle.removeObserver(observer) }
    }
}

/** The KISSIK logo tile, used as the onboarding hero. */
@Composable
private fun LogoHero(modifier: Modifier = Modifier) =
    Image(painterResource(R.drawable.kissik_logo), "KISSIK", modifier.size(168.dp).clip(RoundedCornerShape(38.dp)))

private data class IntroPage(val accent: Color, val kicker: String, val title: String, val body: String, val points: List<Pair<ImageVector, String>>)

/** Three short pages, swipe-free: one tap each. The last one asks for permissions, then opens the scanner. */
@Composable
private fun Intro(done: () -> Unit) {
    val pages = remember {
        listOf(
            IntroPage(Color(0xFFFF9A3C), "KISSIK CAMERA", "Your phone is the better webcam",
                "Use its camera and microphone in KISSIK on your Windows PC, at full resolution.",
                listOf(Icons.Outlined.HighQuality to "Your camera's full resolution — up to 4K or whatever your phone supports", Icons.Outlined.ScreenLockPortrait to "Keeps streaming with the screen off")),
            IntroPage(Color(0xFFFFB547), "SETUP · UNDER 2 MINUTES", "Scan once. Then forget about it",
                "Open KISSIK on your PC, choose Phone, and scan the code. After that it reconnects by itself.",
                listOf(Icons.Outlined.Wifi to "Same Wi-Fi as your PC", Icons.Outlined.Usb to "Or a USB cable — no Wi-Fi needed")),
            IntroPage(Color(0xFF3DDC97), "PRIVATE · FREE", "Nothing leaves your desk",
                "Video and sound go straight to your PC. No account, no ads, no watermark.",
                listOf(Icons.Outlined.PhotoCamera to "Camera — to send your picture", Icons.Outlined.Mic to "Mic — optional, off until you turn it on")),
        )
    }
    var i by remember { mutableIntStateOf(0) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { done() }
    BackHandler(i > 0) { i-- }
    val p = pages[i]
    Column(Modifier.fillMaxSize().background(K.bg).systemBarsPadding().padding(24.dp)) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                pages.indices.forEach { n ->
                    val w by animateFloatAsState(if (n == i) 28f else 8f, label = "dot")
                    Box(Modifier.height(8.dp).width(w.dp).clip(CircleShape).background(if (n == i) p.accent else K.raised))
                }
            }
            if (i < pages.lastIndex) Text("Skip", color = K.muted, modifier = Modifier.clickable { i = pages.lastIndex }.padding(8.dp))
        }
        AnimatedContent(p, Modifier.weight(1f), label = "page") { page ->
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.Center) {
                LogoHero(Modifier.align(Alignment.CenterHorizontally))
                Spacer(Modifier.height(28.dp))
                Text(page.kicker, color = page.accent, fontSize = 13.sp, fontWeight = FontWeight.Bold, letterSpacing = 2.sp)
                Spacer(Modifier.height(10.dp))
                Text(page.title, color = K.text, fontSize = 32.sp, lineHeight = 38.sp, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(12.dp))
                Text(page.body, color = K.muted, fontSize = 17.sp, lineHeight = 24.sp)
                Spacer(Modifier.height(24.dp))
                page.points.forEach { (icon, text) ->
                    Row(Modifier.padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(36.dp).clip(RoundedCornerShape(10.dp)).background(page.accent.copy(alpha = 0.16f)), contentAlignment = Alignment.Center) {
                            Icon(icon, null, tint = page.accent, modifier = Modifier.size(20.dp))
                        }
                        Spacer(Modifier.width(14.dp))
                        Text(text, color = K.text, fontSize = 16.sp)
                    }
                }
            }
        }
        Button({
            if (i < pages.lastIndex) i++ else {
                val wanted = mutableListOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO)
                if (Build.VERSION.SDK_INT >= 33) wanted += Manifest.permission.POST_NOTIFICATIONS
                launcher.launch(wanted.toTypedArray())
            }
        }, Modifier.fillMaxWidth().height(58.dp), shape = RoundedCornerShape(18.dp),
            colors = ButtonDefaults.buttonColors(containerColor = p.accent, contentColor = Color(0xFF0B0B0E))) {
            Text(if (i < pages.lastIndex) "Next" else "Allow & scan the code", fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        }
    }
}

@Composable
private fun DockButton(icon: ImageVector, label: String, on: Boolean = false, enabled: Boolean = true, spoken: String = label,
                       state: String? = null, onClick: () -> Unit) {
    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.graphicsLayer(alpha = if (enabled) 1f else 0.4f)
        .clip(RoundedCornerShape(16.dp))
        .clickable(onClickLabel = spoken, role = androidx.compose.ui.semantics.Role.Button, onClick = onClick)
        // A screen reader hears one button with a name and a state, not an icon and a word.
        .clearAndSetSemantics { contentDescription = spoken; if (state != null) stateDescription = state }
        .padding(6.dp)) {
        Box(Modifier.size(48.dp).clip(RoundedCornerShape(16.dp)).background(if (on) K.accent else K.raised), contentAlignment = Alignment.Center) {
            Icon(icon, null, tint = if (on) Color.White else K.text)
        }
        Spacer(Modifier.height(4.dp))
        Text(label, color = K.muted, fontSize = 12.sp)
    }
}

@Composable
private fun Home(pairing: Pairing?, onSettings: () -> Unit, onPair: () -> Unit) {
    val ctx = LocalContext.current
    val link by Live.link.collectAsState()
    val running by Live.running.collectAsState()
    val message by Live.message.collectAsState()
    val front by Live.front.collectAsState()
    val torch by Live.torch.collectAsState()
    val mic by Live.mic.collectAsState()
    val quality by Live.quality.collectAsState()
    val maxSize by Live.maxSize.collectAsState()
    val hasTorch by Live.hasTorch.collectAsState()
    val saving by Live.saving.collectAsState()
    val micLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok ->
        if (ok) { Live.mic.value = true; Live.save(ctx) } else openSettingsIfBlocked(ctx, Manifest.permission.RECORD_AUDIO, "Microphone")
    }
    val camLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok ->
        if (ok) StreamService.start(ctx) else openSettingsIfBlocked(ctx, Manifest.permission.CAMERA, "Camera")
    }
    val landscape = androidx.compose.ui.platform.LocalConfiguration.current.let { it.screenWidthDp > it.screenHeightDp }
    val live = link == Link.Wifi || link == Link.Usb
    var showPro by remember { mutableStateOf(false) }
    if (showPro) ProSheet { showPro = false }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (running) LivePreview()
        // Soft shades so the controls stay readable over any picture.
        Box(Modifier.fillMaxWidth().height(140.dp).background(Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.7f), Color.Transparent))))
        Box(Modifier.fillMaxWidth().height(260.dp).align(Alignment.BottomCenter).background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.85f)))))

        Column(Modifier.fillMaxSize().systemBarsPadding().padding(16.dp), horizontalAlignment = Alignment.End) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text("KISSIK", color = K.text, fontSize = 18.sp, fontWeight = FontWeight.Black, letterSpacing = 3.sp)
                Spacer(Modifier.width(12.dp))
                when {
                    link == Link.Wifi -> Chip("Live · Wi-Fi", K.live)
                    link == Link.Usb -> Chip("Live · USB", K.live)
                    link == Link.Rejected -> Chip("Scan again", K.warn)
                    running -> Chip("Connecting", K.warn)
                    else -> Chip("Idle", K.muted)
                }
                Spacer(Modifier.weight(1f))
                IconButton(onSettings) { Icon(Icons.Outlined.Settings, "Settings", tint = K.text) }
            }
            if (saving) Text("Saver on · sending 480p to keep the phone cool", color = K.warn, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))

            Spacer(Modifier.weight(1f))
            if (pairing == null) {
                // Not paired yet: one clear card instead of a blank screen.
                Column((if (landscape) Modifier.widthIn(max = 420.dp) else Modifier.fillMaxWidth()).clip(RoundedCornerShape(28.dp)).background(K.card)
                    .verticalScroll(rememberScrollState()).padding(24.dp)) {
                    Icon(Icons.Outlined.QrCodeScanner, null, tint = K.accent, modifier = Modifier.size(40.dp))
                    Spacer(Modifier.height(14.dp))
                    Text("Pair with your PC", color = K.text, fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.height(6.dp))
                    Text("In KISSIK on your PC choose Phone, then scan the code it shows. Plugged in by USB? Press Connect by USB in KISSIK instead.",
                        color = K.muted, fontSize = 15.sp, lineHeight = 21.sp)
                    Spacer(Modifier.height(18.dp))
                    Button(onPair, Modifier.fillMaxWidth().height(54.dp), shape = RoundedCornerShape(16.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = K.accent, contentColor = Color.White)) { Text("Scan the code", fontSize = 16.sp) }
                }
            } else {
                if (!live && message.isNotEmpty()) Text(message, color = K.text, fontSize = 14.sp, textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp))
                if (link == Link.Rejected) Button(onPair, Modifier.fillMaxWidth().padding(bottom = 12.dp).height(54.dp), shape = RoundedCornerShape(16.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = K.accent, contentColor = Color.White)) { Text("Scan the code", fontSize = 16.sp) }
                if (!running) Text("Paired with ${pairing.host}", color = K.muted, fontSize = 15.sp,
                    textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp))
                Column((if (landscape) Modifier.widthIn(max = 420.dp) else Modifier.fillMaxWidth()).clip(RoundedCornerShape(28.dp)).background(K.card.copy(alpha = 0.92f)).padding(12.dp)) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly) {
                        // "Rear", not "Back": under a button, "Back" reads as "go back".
                        DockButton(Icons.Filled.Cameraswitch, if (front) "Front" else "Rear", spoken = "Switch camera",
                            state = if (front) "Front camera" else "Rear camera") { Live.front.value = !front; Pro.lensId.value = null; Live.torch.value = false; Live.save(ctx) }
                        // The torch is the phone's flash LED; most front cameras have none.
                        DockButton(if (torch) Icons.Filled.FlashOn else Icons.Filled.FlashOff, "Torch", torch, enabled = hasTorch == true,
                            state = if (hasTorch != true) "Not available" else if (torch) "On" else "Off") {
                            when (hasTorch) {
                                true -> Live.torch.value = !torch
                                false -> android.widget.Toast.makeText(ctx, if (front) "The front camera has no torch. Switch to the back camera." else "This camera has no torch.", android.widget.Toast.LENGTH_SHORT).show()
                                null -> android.widget.Toast.makeText(ctx, "Start streaming to use the torch.", android.widget.Toast.LENGTH_SHORT).show()
                            }
                        }
                        DockButton(if (mic) Icons.Filled.Mic else Icons.Filled.MicOff, "Mic", mic, spoken = "Microphone", state = if (mic) "On" else "Off") {
                            if (mic) { Live.mic.value = false; Live.save(ctx) }
                            else if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) { Live.mic.value = true; Live.save(ctx) }
                            else micLauncher.launch(Manifest.permission.RECORD_AUDIO)
                        }
                        DockButton(Icons.Outlined.HighQuality, "${quality}p", spoken = "Picture size", state = "${quality}p") {
                            // Cycle through the common sizes this lens can actually do, up to its maximum.
                            val top = maxSize.second.takeIf { it > 0 } ?: 1080
                            val steps = (listOf(480, 720, 1080, 1440, 2160, 2880, 4320).filter { it <= top } + top).distinct().sorted()
                            Live.quality.value = steps.firstOrNull { it > quality } ?: steps.first(); Live.save(ctx)
                        }
                        DockButton(Icons.Outlined.Tune, "Pro", spoken = "Camera controls") { showPro = true }
                    }
                    Spacer(Modifier.height(10.dp))
                    val stopColor = if (running) Color(0xFFFF5A5F) else K.accent
                    Button({ if (running) StreamService.stop(ctx) else if (hasCamera(ctx)) StreamService.start(ctx) else camLauncher.launch(Manifest.permission.CAMERA) },
                        Modifier.fillMaxWidth().height(54.dp), shape = RoundedCornerShape(16.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = stopColor, contentColor = Color.White)) {
                        Icon(if (running) Icons.Filled.Stop else Icons.Filled.PlayArrow, null)
                        Spacer(Modifier.width(8.dp))
                        Text(if (running) "Stop streaming" else "Start streaming", fontSize = 16.sp)
                    }
                }
            }
        }
    }
}

/**
 * The camera on the phone's own screen: a hardware preview drawn by the camera itself, so it's smooth and costs the
 * processor nothing. The stream to the PC carries on with or without it. Tap to focus and meter there.
 */
@Composable
private fun LivePreview() {
    val view = remember { mutableStateOf<PreviewView?>(null) }
    DisposableEffect(Unit) { onDispose { Live.previewSurface.value = null } }
    AndroidView(factory = { c ->
        PreviewView(c).apply {
            implementationMode = PreviewView.ImplementationMode.COMPATIBLE
            scaleType = PreviewView.ScaleType.FILL_CENTER
            view.value = this
            Live.previewSurface.value = surfaceProvider
        }
    }, modifier = Modifier.fillMaxSize().pointerInput(Unit) {
        detectTapGestures { o -> view.value?.meteringPointFactory?.createPoint(o.x, o.y)?.let { Live.meteringTap.tryEmit(it) } }
    })
}

@Composable
private fun Scan(onBack: () -> Unit, onCode: (String) -> Unit) {
    val ctx = LocalContext.current
    var granted by remember { mutableStateOf(hasCamera(ctx)) }
    var asked by remember { mutableStateOf(false) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        granted = it
        // Only after the person pressed the button: the first, automatic question must not jump to Settings.
        if (!it && asked) openSettingsIfBlocked(ctx, Manifest.permission.CAMERA, "Camera")
    }
    OnResume { granted = hasCamera(ctx) } // back from the phone's Settings with the camera switched on
    var paste by remember { mutableStateOf(false) }
    // The scanner needs the camera: stop streaming and wait until the stream has really let go of it.
    val streaming by Live.running.collectAsState()
    LaunchedEffect(Unit) {
        if (Live.running.value) StreamService.stop(ctx)
        if (!granted) launcher.launch(Manifest.permission.CAMERA)
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (granted && !streaming) QrCamera(onCode)
        ScanMask()
        Column(Modifier.fillMaxSize().systemBarsPadding().padding(20.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                IconButton(onBack) { Icon(Icons.Filled.Close, "Close", tint = Color.White) }
                Text("Scan the code in KISSIK", color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
            }
            Spacer(Modifier.weight(1f))
            Text(if (granted) "Point at the QR code on your PC screen" else "KISSIK Camera needs the camera to scan the code",
                color = Color.White, fontSize = 15.sp, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
            if (!granted) {
                Spacer(Modifier.height(12.dp))
                Button({ asked = true; launcher.launch(Manifest.permission.CAMERA) }, Modifier.fillMaxWidth().height(54.dp), shape = RoundedCornerShape(16.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = K.accent, contentColor = Color.White)) { Text("Allow the camera", fontSize = 16.sp) }
            }
            Spacer(Modifier.height(8.dp))
            Text("Phone and PC on the same Wi-Fi · guest networks usually block it", color = Color.White.copy(alpha = 0.6f),
                fontSize = 13.sp, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
            Spacer(Modifier.height(20.dp))
            Row(Modifier.align(Alignment.CenterHorizontally).clip(CircleShape).border(1.dp, Color.White.copy(alpha = 0.4f), CircleShape)
                .clickable { paste = true }.padding(horizontal = 20.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.Link, null, tint = Color.White)
                Spacer(Modifier.width(8.dp))
                Text("Paste the link instead", color = Color.White)
            }
        }
    }

    if (paste) {
        var text by remember { mutableStateOf("") }
        AlertDialog(onDismissRequest = { paste = false }, containerColor = K.card,
            title = { Text("Paste the pairing link") },
            text = { OutlinedTextField(text, { text = it }, placeholder = { Text("https://192.168.1.20:47820/?t=…") }, singleLine = true) },
            confirmButton = { TextButton({ paste = false; onCode(text) }) { Text("Pair") } },
            dismissButton = { TextButton({ paste = false }) { Text("Cancel") } })
    }
}

/** Darkens everything except a rounded square window, with an accent outline. */
@Composable
private fun ScanMask() = Canvas(Modifier.fillMaxSize().graphicsLayer(compositingStrategy = CompositingStrategy.Offscreen)) {
    val side = size.width * 0.72f
    val tl = Offset((size.width - side) / 2, (size.height - side) / 2.3f)
    val radius = CornerRadius(36.dp.toPx())
    drawRect(Color.Black.copy(alpha = 0.6f))
    drawRoundRect(Color.Transparent, tl, Size(side, side), radius, blendMode = BlendMode.Clear)
    drawRoundRect(K.accent, tl, Size(side, side), radius, style = Stroke(3.dp.toPx(), cap = StrokeCap.Round))
}

@Composable
private fun QrCamera(onCode: (String) -> Unit) {
    val ctx = LocalContext.current
    val owner = LocalLifecycleOwner.current
    val executor = remember { Executors.newSingleThreadExecutor() }
    DisposableEffect(Unit) { onDispose { executor.shutdown() } }
    AndroidView(factory = { c ->
        val view = PreviewView(c).apply { scaleType = PreviewView.ScaleType.FILL_CENTER }
        val providerFuture = ProcessCameraProvider.getInstance(c)
        providerFuture.addListener({
            val provider = providerFuture.get()
            val preview = Preview.Builder().build().also { it.surfaceProvider = view.surfaceProvider }
            val reader = MultiFormatReader().apply { setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE), DecodeHintType.TRY_HARDER to true)) }
            val analysis = ImageAnalysis.Builder().setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
            var found = false
            analysis.setAnalyzer(executor) { img ->
                img.use {
                    if (found) return@use
                    val plane = it.planes[0]; val buf = plane.buffer
                    val data = ByteArray(buf.remaining()).also(buf::get)
                    val src = PlanarYUVLuminanceSource(data, plane.rowStride, it.height, 0, 0, it.width, it.height, false)
                    val text = runCatching { reader.decodeWithState(BinaryBitmap(HybridBinarizer(src))).text }.getOrNull()
                    reader.reset()
                    if (text != null && Pairing.parse(text) != null) {
                        found = true
                        ContextCompat.getMainExecutor(ctx).execute { provider.unbindAll(); onCode(text) }
                    }
                }
            }
            provider.unbindAll()
            runCatching { provider.bindToLifecycle(owner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis) }
                .recoverCatching { provider.bindToLifecycle(owner, CameraSelector.DEFAULT_FRONT_CAMERA, preview, analysis) }
                .onFailure { android.widget.Toast.makeText(ctx, "Couldn't open the camera. Close other camera apps and try again.", android.widget.Toast.LENGTH_LONG).show() }
        }, ContextCompat.getMainExecutor(c))
        view
    }, modifier = Modifier.fillMaxSize())
}

@Composable
private fun SettingsCard(content: @Composable ColumnScope.() -> Unit) =
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(24.dp)).background(K.card).padding(20.dp), content = content)

@Composable
private fun Settings(pairing: Pairing?, onClose: () -> Unit, onPair: () -> Unit, onForget: () -> Unit) {
    val ctx = LocalContext.current
    val saver by Live.saver.collectAsState()
    val version = remember { runCatching { ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName }.getOrNull() ?: "?" }
    var confirmForget by remember { mutableStateOf(false) }
    if (confirmForget) AlertDialog(onDismissRequest = { confirmForget = false }, containerColor = K.card,
        title = { Text("Forget ${pairing?.host ?: "this PC"}?") },
        text = { Text("Streaming stops, and this phone can't connect to that PC again until you scan its code in KISSIK.") },
        confirmButton = { TextButton({ confirmForget = false; onForget() }) { Text("Forget this PC", color = Color(0xFFFF5A5F)) } },
        dismissButton = { TextButton({ confirmForget = false }) { Text("Keep it") } })
    Column(Modifier.fillMaxSize().background(K.bg).systemBarsPadding().padding(horizontal = 20.dp).verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Settings", color = K.text, fontSize = 30.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
            IconButton(onClose) { Icon(Icons.Filled.Close, "Close", tint = K.text) }
        }

        SettingsCard {
            Text("YOUR PC", color = K.muted, fontSize = 12.sp, letterSpacing = 2.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(12.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.Computer, null, tint = K.accent, modifier = Modifier.size(32.dp))
                Spacer(Modifier.width(14.dp))
                Column(Modifier.weight(1f)) {
                    Text(pairing?.host ?: "Not paired", color = K.text, fontSize = 18.sp)
                    Text(if (pairing == null) "Scan the code in KISSIK to pair" else if (pairing.pin != null) "Certificate pinned · reconnects automatically" else "Pairs on first connect",
                        color = K.muted, fontSize = 13.sp)
                }
            }
            Spacer(Modifier.height(14.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedButton(onPair, shape = RoundedCornerShape(14.dp)) { Text(if (pairing == null) "Scan the code" else "Pair another PC") }
                if (pairing != null) TextButton({ confirmForget = true }) { Text("Forget", color = Color(0xFFFF5A5F)) }
            }
        }

        SettingsCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Battery & heat saver", color = K.text, fontSize = 17.sp)
                    Text("Sends 480p while the phone is hot, or under 20% and not charging.", color = K.muted, fontSize = 13.sp, lineHeight = 18.sp)
                }
                Switch(saver, { Live.saver.value = it; Live.save(ctx) }, colors = SwitchDefaults.colors(checkedTrackColor = K.accent, checkedThumbColor = Color.White))
            }
        }

        SettingsCard {
            Text("WON'T CONNECT?", color = K.muted, fontSize = 12.sp, letterSpacing = 2.sp, fontWeight = FontWeight.Bold)
            listOf(
                "Phone and PC on the same Wi-Fi — not mobile data, not a guest network.",
                "In KISSIK, press “Allow phones on this network” once (Windows Firewall).",
                "Unpaired in KISSIK? Scan its new code.",
                "A USB cable always works: turn on USB debugging, plug in, press Connect by USB in KISSIK.",
            ).forEachIndexed { n, tip ->
                Row(Modifier.padding(top = 12.dp)) {
                    Text("${n + 1}", color = K.accent, fontWeight = FontWeight.Bold, modifier = Modifier.width(22.dp))
                    Text(tip, color = K.text, fontSize = 14.sp, lineHeight = 20.sp)
                }
            }
        }

        SettingsCard {
            Text("KISSIK Camera $version", color = K.text, fontSize = 17.sp)
            Spacer(Modifier.height(6.dp))
            Text("Streams at your phone camera's full resolution, up to 4K or whatever your phone supports. Free and open source. No account, no ads, no watermark, no tracking. Video and sound go only to the PC you paired, over your own Wi-Fi or cable.",
                color = K.muted, fontSize = 13.sp, lineHeight = 19.sp)
        }
        Spacer(Modifier.height(12.dp))
    }
}

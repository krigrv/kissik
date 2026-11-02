package app.kissik.studio

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.kissik.studio.captions.CaptionEngineKind
import app.kissik.studio.captions.CaptionTrack
import app.kissik.studio.crash.CrashConsent
import app.kissik.studio.crash.CrashReports
import app.kissik.studio.effects.BackgroundMode
import app.kissik.studio.effects.Effects
import app.kissik.studio.image.Lut3D
import app.kissik.studio.platform.BackgroundSource
import app.kissik.studio.platform.CaptionEvent
import app.kissik.studio.platform.StudioPlatform
import app.kissik.studio.resources.Res
import app.kissik.studio.scenes.SceneDocument
import app.kissik.studio.scenes.SceneTemplates
import app.kissik.studio.scenes.Transition
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.jetbrains.compose.resources.ExperimentalResourceApi

/**
 * The app's shared state and actions. The UI reads these Compose states; the platform services do the device work.
 */
class Studio(val platform: StudioPlatform) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    var settings by mutableStateOf(Settings.fromJson(platform.store.get("settings")))
        private set

    var effects by mutableStateOf(Effects())
        private set
    var backgroundSource: BackgroundSource? = null
        private set
    var lutName by mutableStateOf<String?>(null)
        private set

    // Scenes.
    val scenes = mutableStateListOf<SceneDocument>()
    var scene by mutableStateOf<SceneDocument?>(null)
        private set
    var previousScene by mutableStateOf<SceneDocument?>(null)
        private set
    var transition by mutableStateOf(Transition.Fade)
    /** 0..1 while a scene transition plays; 1 when settled. */
    var transitionProgress by mutableStateOf(1f)
        private set

    // Captions.
    var captionsOn by mutableStateOf(false)
        private set
    var captionEngine by mutableStateOf(CaptionEngineKind.Fast)
    var captionLines by mutableStateOf<List<String>>(emptyList())
        private set
    private val track = CaptionTrack()

    // Teleprompter.
    var teleprompterOn by mutableStateOf(false)

    // Crash report waiting for an answer.
    var pendingCrash by mutableStateOf<String?>(null)
        private set

    val reducedMotion: Boolean get() = settings.reduceMotion ?: platform.system.reducedMotion
    val highContrast: Boolean get() = settings.highContrast || platform.system.highContrast

    private val messagesList = mutableStateListOf<String>()
    var message by mutableStateOf<String?>(null)
        private set

    init {
        platform.camera.setVisionEngine(settings.visionEngine)
        scenes += SceneTemplates.builtIn.map { applyOverride(it) }
        scene = scenes.firstOrNull { it.id == settings.sceneId }?.takeIf { it.id != "blank" }
        loadPack()
        scope.launch { platform.captions.events.collect(::onCaption) }
        scope.launch { platform.media.messages.collect { say(it) } }
        checkCrashes()
    }

    @OptIn(ExperimentalResourceApi::class)
    private fun loadPack() = scope.launch {
        runCatching {
            val pack = SceneDocument.listFromJson(Res.readBytes("files/ScenePack.json").decodeToString())
            scenes += pack.map { applyOverride(it) }
            if (scene == null && settings.sceneId != "blank") scene = scenes.firstOrNull { it.id == settings.sceneId }
        }
    }

    private fun applyOverride(s: SceneDocument) =
        settings.sceneOverrides[s.id]?.let { runCatching { SceneDocument.fromJson(it) }.getOrNull()?.copy(id = s.id) } ?: s

    fun updateSettings(change: (Settings) -> Settings) {
        settings = change(settings)
        platform.store.put("settings", settings.toJson())
    }

    fun say(text: String) { message = text }
    fun messageShown() { message = null }

    // ── Effects ────────────────────────────────────────────────────────────
    fun updateEffects(change: (Effects) -> Effects) {
        effects = change(effects)
        platform.camera.setEffects(effects)
    }

    fun setBackgroundSource(src: BackgroundSource?) {
        backgroundSource?.close()
        backgroundSource = src
        backgroundJob?.cancel()
        if (src == null) { updateEffects { it.copy(backgroundImage = null) }; return }
        updateEffects { it.copy(background = if (it.background == BackgroundMode.GreenScreen) it.background else BackgroundMode.Replace, backgroundImage = src.frameAt(0)) }
        // Looping videos: refresh the background frame ~12 times a second.
        backgroundJob = scope.launch {
            val start = platform.system.nowMs()
            while (isActive) {
                delay(83)
                val f = src.frameAt(platform.system.nowMs() - start) ?: continue
                if (f !== effects.backgroundImage) updateEffects { it.copy(backgroundImage = f) }
            }
        }
    }
    private var backgroundJob: Job? = null

    fun loadLut(name: String, text: String) {
        runCatching { Lut3D.parseCube(text) }
            .onSuccess { lut -> lutName = name; updateEffects { it.copy(lut = lut) } }
            .onFailure { say("That LUT couldn't be read: ${it.message}") }
    }
    fun clearLut() { lutName = null; updateEffects { it.copy(lut = null) } }

    // ── Scenes ─────────────────────────────────────────────────────────────
    fun selectScene(next: SceneDocument?) {
        if (next?.id == scene?.id) return
        previousScene = scene
        scene = next?.takeIf { it.id != "blank" }
        updateSettings { it.copy(sceneId = next?.id ?: "blank") }
        platform.camera.setScene(scene)
        if (transition == Transition.Cut || reducedMotion) { transitionProgress = 1f; return }
        scope.launch {
            val steps = 20
            for (i in 1..steps) { transitionProgress = i / steps.toFloat(); delay(20) }
            previousScene = null
        }
        transitionProgress = 0f
    }

    fun editScene(updated: SceneDocument) {
        val i = scenes.indexOfFirst { it.id == updated.id }
        if (i >= 0) scenes[i] = updated
        if (scene?.id == updated.id) { scene = updated; platform.camera.setScene(updated) }
        updateSettings { it.copy(sceneOverrides = it.sceneOverrides + (updated.id to updated.toJson())) }
    }

    fun importScene(json: String): Boolean = runCatching {
        val s = SceneDocument.fromJson(json).let { if (scenes.any { e -> e.id == it.id }) it.copy(id = it.id + "-copy") else it }
        scenes += s; editScene(s); true
    }.getOrDefault(false)

    // ── Captions ───────────────────────────────────────────────────────────
    fun setCaptions(on: Boolean) {
        if (on) {
            val reason = platform.captions.unavailableReason(captionEngine)
            if (reason != null) { say(reason); return }
            track.clear(); captionLines = emptyList()
            platform.captions.start(captionEngine, settings.captionLanguage)
        } else platform.captions.stop()
        captionsOn = on
    }

    private fun onCaption(e: CaptionEvent) {
        val now = platform.system.nowMs()
        when (e) {
            is CaptionEvent.Partial -> track.onPartial(e.text)
            is CaptionEvent.Final -> track.onFinal(e.text, now)
            is CaptionEvent.Error -> { say(e.message); captionsOn = false; return }
        }
        captionLines = track.lines(now)
    }

    // ── Crash reports ──────────────────────────────────────────────────────
    private fun checkCrashes() {
        val log = platform.system.readCrashLog()?.takeIf { it.isNotBlank() } ?: return
        when (settings.crashConsent) {
            CrashConsent.Never -> platform.system.clearCrashLog()
            CrashConsent.Always -> sendCrashes(log)
            else -> pendingCrash = CrashReports.scrub(log, platform.system.deviceName)
        }
    }

    fun answerCrash(send: Boolean, remember: Boolean) {
        val log = platform.system.readCrashLog()
        pendingCrash = null
        if (remember) updateSettings { it.copy(crashConsent = if (send) CrashConsent.Always else CrashConsent.Never) }
        else if (settings.crashConsent == CrashConsent.NotAsked) updateSettings { it.copy(crashConsent = CrashConsent.AskEachTime) }
        if (send && log != null) sendCrashes(log) else platform.system.clearCrashLog()
    }

    private fun sendCrashes(log: String) {
        val ep = CrashReports.parseDsn(CrashReports.DSN) ?: return
        val sys = platform.system
        val entries = CrashReports.splitEntries(log)
        var left = entries.size
        var ok = true
        entries.forEach { report ->
            val env = CrashReports.buildEnvelope(CrashReports.DSN, report, sys.appVersion, sys.osName, sys.osVersion, sys.nowIso(), sys.deviceName)
            sys.postCrash(ep.url, CrashReports.authHeader(ep.key, sys.appVersion), env) { sent ->
                ok = ok && sent
                if (--left == 0 && ok) sys.clearCrashLog()
            }
        }
    }
}

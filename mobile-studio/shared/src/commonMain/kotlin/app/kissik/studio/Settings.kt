package app.kissik.studio

import app.kissik.studio.crash.CrashConsent
import app.kissik.studio.stream.Destination
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

enum class UiMode(val label: String, val detail: String) {
    Simple("Simple", "Big buttons for the essentials: record, photo, background and looks"),
    Studio("Studio", "Everything: pro camera controls, scenes, streaming, captions and more"),
}

enum class ThemeChoice(val label: String) { System("Match the phone"), Light("Light"), Dark("Dark") }

@Serializable
data class Settings(
    val onboarded: Boolean = false,
    val mode: UiMode = UiMode.Simple,
    val theme: ThemeChoice = ThemeChoice.System,
    val highContrast: Boolean = false,
    /** null = follow the system's reduce-motion setting. */
    val reduceMotion: Boolean? = null,
    val largeText: Boolean = false,
    val crashConsent: CrashConsent = CrashConsent.NotAsked,
    val captionLanguage: String = "en-US",
    val replaySeconds: Int = 30,
    val destinations: List<Destination> = emptyList(),
    val pairingLink: String = "",
    val teleprompterText: String = "",
    val teleprompterWpm: Int = 140,
    val sceneId: String = "blank",
    val sceneOverrides: Map<String, String> = emptyMap(), // scene id → edited scene JSON
) {
    fun toJson() = SettingsJson.encodeToString(serializer(), this)
    companion object {
        fun fromJson(text: String?): Settings =
            text?.let { runCatching { SettingsJson.decodeFromString(serializer(), it) }.getOrNull() } ?: Settings()
    }
}

private val SettingsJson = Json { ignoreUnknownKeys = true; encodeDefaults = true; coerceInputValues = true }

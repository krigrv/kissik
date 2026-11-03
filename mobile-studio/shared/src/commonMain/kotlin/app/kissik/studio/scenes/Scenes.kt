package app.kissik.studio.scenes

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import kotlin.random.Random

/**
 * One layer of a scene — the same JSON as the desktop app's `SceneLayer` (src/OpenKamera.Core/Scenes.cs), so a scene
 * made on the PC opens on the phone and back. Geometry is normalised to the canvas (0..1); sizes are fractions of the
 * canvas height.
 */
@Serializable
data class SceneLayer(
    @SerialName("Id") val id: String = newId(8),
    @SerialName("Kind") val kind: String = "text", // text | image | shape | camera
    @SerialName("Name") val name: String = "",
    @SerialName("Visible") val visible: Boolean = true,
    @SerialName("Behind") val behind: Boolean = false,
    @SerialName("X") val x: Double = 0.05,
    @SerialName("Y") val y: Double = 0.05,
    @SerialName("W") val w: Double = 0.4,
    @SerialName("H") val h: Double = 0.1,
    @SerialName("Opacity") val opacity: Double = 1.0,
    @SerialName("Text") val text: String = "",
    @SerialName("FontSize") val fontSize: Double = 0.06,
    @SerialName("Bold") val bold: Boolean = false,
    @SerialName("Align") val align: String = "left",
    @SerialName("Color") val color: String = "#FFFFFF",
    @SerialName("Fill") val fill: String = "",
    @SerialName("Stroke") val stroke: String = "",
    @SerialName("StrokeWidth") val strokeWidth: Double = 0.0,
    @SerialName("CornerRadius") val cornerRadius: Double = 0.0,
    @SerialName("Shape") val shape: String = "rect",
    @SerialName("Source") val source: String = "",
) {
    /** Same rule as the desktop: text layers are listed by what they say. */
    val displayName: String get() = when {
        kind == "text" && text.isNotBlank() -> text.lineSequence().first().trim()
        name.isNotBlank() -> name
        kind == "image" -> "Image"
        kind == "camera" -> "Video source"
        shape == "ellipse" -> "Circle"
        kind == "text" -> "Text"
        else -> "Shape"
    }
}

@Serializable
data class SceneDocument(
    @SerialName("Id") val id: String = "user-" + newId(10),
    @SerialName("Name") val name: String = "Untitled",
    @SerialName("Category") val category: String = "Utility",
    @SerialName("Background") val background: String = "#101418",
    @SerialName("Background2") val background2: String = "",
    @SerialName("Layers") val layers: List<SceneLayer> = emptyList(),
) {
    /** The camera placement: an explicit camera layer, or the full canvas. */
    val camera: SceneLayer get() = layers.firstOrNull { it.kind == "camera" } ?: SceneLayer(kind = "camera", x = 0.0, y = 0.0, w = 1.0, h = 1.0)
    val overlays: List<SceneLayer> get() = layers.filter { it.kind != "camera" }
    /** What screen readers announce for a scene card (same as the desktop). */
    override fun toString() = if (category.isBlank()) name else "$name, $category"

    fun toJson(): String = SceneJson.encodeToString(serializer(), this)

    companion object {
        fun fromJson(json: String): SceneDocument = SceneJson.decodeFromString(serializer(), json.removePrefix("﻿"))
        fun listFromJson(json: String): List<SceneDocument> =
            SceneJson.decodeFromString(ListSerializer(serializer()), json.removePrefix("﻿"))
    }
}

/** Matches the desktop's options: indented, unknown fields ignored, nulls left out. */
val SceneJson = Json {
    prettyPrint = true
    ignoreUnknownKeys = true
    explicitNulls = false
    coerceInputValues = true
    isLenient = true
    encodeDefaults = true
}

private fun newId(n: Int): String {
    val hex = "0123456789abcdef"
    return buildString { repeat(n) { append(hex[Random.nextInt(16)]) } }
}

/** Built-in templates — the desktop's own designs (placeholder text only); the 100-design pack is loaded from resources. */
object SceneTemplates {
    private fun t(text: String, x: Double, y: Double, w: Double, h: Double, size: Double, bold: Boolean = false,
                  color: String = "#FFFFFF", align: String = "left", fill: String = "", name: String = "") =
        SceneLayer(kind = "text", text = text, x = x, y = y, w = w, h = h, fontSize = size, bold = bold, color = color, align = align, fill = fill, name = name)

    val builtIn: List<SceneDocument> = listOf(
        SceneDocument(id = "blank", name = "Blank", category = "Utility"),
        SceneDocument(id = "just-my-name", name = "Just my name", category = "Professional", layers = listOf(
            SceneLayer(kind = "shape", name = "Plate", x = 0.03, y = 0.865, w = 0.26, h = 0.075, color = "#B3101214", cornerRadius = 0.012),
            t("Your Name", 0.045, 0.875, 0.24, 0.055, 0.04, bold = true, name = "Name"),
        )),
        SceneDocument(id = "lower-third", name = "Lower Third", category = "Professional", layers = listOf(
            SceneLayer(kind = "shape", name = "Bar", x = 0.0, y = 0.80, w = 0.62, h = 0.16, color = "#E6141618"),
            SceneLayer(kind = "shape", name = "Accent", x = 0.035, y = 0.83, w = 0.006, h = 0.10, color = "#FF2EA3F2"),
            t("Your Name", 0.055, 0.815, 0.55, 0.07, 0.055, bold = true, name = "Name"),
            t("Title or topic", 0.055, 0.885, 0.55, 0.05, 0.034, color = "#FFC9CDD0", name = "Title"),
        )),
        SceneDocument(id = "name-card", name = "Name Card", category = "Professional", layers = listOf(
            SceneLayer(kind = "shape", name = "Badge", x = 0.03, y = 0.05, w = 0.07, h = 0.124, shape = "ellipse", color = "#FF2EA3F2"),
            t("YN", 0.03, 0.078, 0.07, 0.07, 0.05, bold = true, align = "center", name = "Initials"),
            t("Your Name", 0.115, 0.045, 0.6, 0.09, 0.075, bold = true, name = "Name"),
            t("@yourhandle", 0.115, 0.125, 0.6, 0.06, 0.042, color = "#FFDDE3E8", name = "Handle"),
            t("you@example.com", 0.03, 0.22, 0.6, 0.05, 0.034, name = "Email"),
            t("City, Country", 0.03, 0.275, 0.6, 0.05, 0.034, name = "Location"),
        )),
        SceneDocument(id = "framed", name = "Framed", category = "Social", background = "#FF23262D", background2 = "#FF0C0D10", layers = listOf(
            SceneLayer(kind = "camera", name = "Video source", x = 0.045, y = 0.07, w = 0.91, h = 0.86, cornerRadius = 0.03, stroke = "#59FFFFFF", strokeWidth = 0.004),
        )),
        SceneDocument(id = "bubble", name = "Bubble Cam", category = "Streaming", background = "#FF1B1035", background2 = "#FF0B0716", layers = listOf(
            SceneLayer(kind = "camera", name = "Video source", x = 0.34, y = 0.12, w = 0.3375, h = 0.6, shape = "ellipse", stroke = "#FFFF4FA3", strokeWidth = 0.01),
            t("Your Name", 0.1, 0.76, 0.8, 0.08, 0.06, bold = true, align = "center", name = "Name"),
            t("Live now", 0.1, 0.845, 0.8, 0.05, 0.036, color = "#FFFF8BC6", align = "center", name = "Tagline"),
        )),
        SceneDocument(id = "presentation", name = "Presentation", category = "Professional", background = "#FF0E2A1E", background2 = "#FF06140E", layers = listOf(
            SceneLayer(kind = "shape", name = "Slide", x = 0.04, y = 0.07, w = 0.66, h = 0.86, color = "#FFFFFFFF", cornerRadius = 0.02),
            t("Today's topic", 0.08, 0.12, 0.58, 0.09, 0.06, bold = true, color = "#FF101418", name = "Heading"),
            t("• First point\n• Second point\n• Third point", 0.08, 0.25, 0.58, 0.4, 0.045, color = "#FF2B3036", name = "Bullets"),
            SceneLayer(kind = "camera", name = "Video source", x = 0.73, y = 0.5, w = 0.2475, h = 0.44, shape = "ellipse", stroke = "#FFB8F23A", strokeWidth = 0.008),
        )),
        SceneDocument(id = "corner-logo", name = "Corner Logo", category = "Utility", layers = listOf(
            t("YOUR LOGO", 0.73, 0.045, 0.235, 0.085, 0.042, bold = true, align = "center", fill = "#CC000000", name = "Logo"),
        )),
    )
}

/** Scene transitions, as on the desktop. */
enum class Transition(val label: String) { Cut("Cut"), Fade("Fade"), Slide("Slide"), Zoom("Zoom") }

/**
 * Transition progress between two scenes. [progress] 0 → old scene, 1 → new scene. Returns per-scene alpha, x offset
 * (fraction of width) and scale so the renderer can draw both.
 */
data class TransitionFrame(val oldAlpha: Float, val newAlpha: Float, val oldOffset: Float, val newOffset: Float, val oldScale: Float, val newScale: Float)

fun transitionFrame(kind: Transition, progress: Float): TransitionFrame {
    val p = progress.coerceIn(0f, 1f)
    val e = p * p * (3 - 2 * p) // ease in-out
    return when (kind) {
        Transition.Cut -> if (p < 1f) TransitionFrame(1f, 0f, 0f, 0f, 1f, 1f) else TransitionFrame(0f, 1f, 0f, 0f, 1f, 1f)
        Transition.Fade -> TransitionFrame(1 - e, e, 0f, 0f, 1f, 1f)
        Transition.Slide -> TransitionFrame(1f, 1f, -e, 1 - e, 1f, 1f)
        Transition.Zoom -> TransitionFrame(1 - e, e, 0f, 0f, 1f + e * 0.25f, 0.8f + e * 0.2f)
    }
}

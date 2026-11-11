package app.kissik.studio.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import app.kissik.studio.ThemeChoice

object Brand {
    val OrangeLight = Color(0xFFFF9A3C)
    val Orange = Color(0xFFE8551A)
    val gradient = Brush.verticalGradient(listOf(OrangeLight, Orange))
    /** Smallest touch target (WCAG / Material: 48 dp). */
    val minTouch = 48.dp
}

private val Light = lightColorScheme(
    primary = Color(0xFFB8400D), onPrimary = Color.White, primaryContainer = Color(0xFFFFDBCB), onPrimaryContainer = Color(0xFF390C00),
    secondary = Color(0xFF77574A), tertiary = Color(0xFF6A5E2F),
    // Chips and tool buttons take these; left unset they fall back to Material's purple, which isn't KISSIK's.
    secondaryContainer = Color(0xFFF5DED5), onSecondaryContainer = Color(0xFF2B160E),
    background = Color(0xFFFFFBFF), surface = Color(0xFFFFFBFF), error = Color(0xFFBA1A1A),
)
private val Dark = darkColorScheme(
    primary = Brand.OrangeLight, onPrimary = Color(0xFF3B1300), primaryContainer = Color(0xFF8A2F06), onPrimaryContainer = Color(0xFFFFDBCB),
    secondary = Color(0xFFE7BDAD), tertiary = Color(0xFFD7C68D),
    secondaryContainer = Color(0xFF4A332A), onSecondaryContainer = Color(0xFFFFDBCB),
    background = Color(0xFF1A110E), surface = Color(0xFF1A110E), error = Color(0xFFFFB4AB),
)
// High contrast: pure black/white with the brand orange only where it still clears 7:1 against the background.
private val LightHc = Light.copy(primary = Color(0xFF7A2800), onSurface = Color.Black, onBackground = Color.Black,
    onSurfaceVariant = Color.Black, outline = Color.Black, background = Color.White, surface = Color.White)
private val DarkHc = Dark.copy(primary = Color(0xFFFFC59E), onSurface = Color.White, onBackground = Color.White,
    onSurfaceVariant = Color.White, outline = Color.White, background = Color.Black, surface = Color.Black)

@Composable
fun KissikTheme(theme: ThemeChoice, highContrast: Boolean, largeText: Boolean, content: @Composable () -> Unit) {
    val dark = when (theme) { ThemeChoice.System -> isSystemInDarkTheme(); ThemeChoice.Light -> false; ThemeChoice.Dark -> true }
    val scheme = when { dark && highContrast -> DarkHc; dark -> Dark; highContrast -> LightHc; else -> Light }
    // The system font size is honoured automatically (sp); "Larger text" adds a further 30 % for people who want more.
    val d = LocalDensity.current
    val density = if (largeText) Density(d.density, d.fontScale * 1.3f) else d
    CompositionLocalProvider(LocalDensity provides density) {
        MaterialTheme(colorScheme = scheme, content = content)
    }
}

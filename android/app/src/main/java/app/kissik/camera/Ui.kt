package app.kissik.camera

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** KISSIK look: near-black ground, soft dark cards, the logo's orange as the one accent, status colours for live/connecting. */
object K {
    val bg = Color(0xFF0B0B0E)
    val card = Color(0xFF17171C)
    val raised = Color(0xFF26262E)
    val text = Color(0xFFF2F2F5)
    val muted = Color(0xFF9E9EA8)
    val accent = Color(0xFFFF7A2E)
    val live = Color(0xFF3DDC97)
    val warn = Color(0xFFFFB020)
}

val KColors = darkColorScheme(primary = K.accent, background = K.bg, surface = K.card, onSurface = K.text, onBackground = K.text)

/** Small status pill with a coloured dot. */
@Composable
fun Chip(text: String, color: Color) {
    Row(Modifier.clip(CircleShape).background(Color.Black.copy(alpha = 0.45f)).border(1.dp, color.copy(alpha = 0.5f), CircleShape)
        .padding(horizontal = 12.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(8.dp).clip(CircleShape).background(color))
        Spacer(Modifier.width(8.dp))
        Text(text.uppercase(), color = K.text, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, letterSpacing = 1.sp)
    }
}

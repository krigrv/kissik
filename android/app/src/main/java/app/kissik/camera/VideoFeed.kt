@file:SuppressLint("RestrictedApi")

package app.kissik.camera

import android.annotation.SuppressLint
import androidx.camera.core.CameraInfo
import androidx.camera.core.SurfaceRequest
import androidx.camera.core.impl.ConstantObservable
import androidx.camera.core.impl.Observable
import androidx.camera.video.FallbackStrategy
import androidx.camera.video.MediaSpec
import androidx.camera.video.Quality
import androidx.camera.video.QualitySelector
import androidx.camera.video.Recorder
import androidx.camera.video.VideoCapabilities
import androidx.camera.video.VideoOutput

/**
 * Feeds CameraX's video stream into our own encoder instead of a file. The video stream runs at true video sizes
 * (up to 4K), unlike the preview stream, which is capped near the screen's size. CameraX asks for a surface,
 * and [onSurface] hands it the encoder's input surface, so the camera draws straight into the video chip.
 */
class VideoFeed(shortSide: Int, private val onSurface: (SurfaceRequest) -> Unit) : VideoOutput {
    private val selector = QualitySelector.from(
        when {
            shortSide >= 2160 -> Quality.UHD
            shortSide >= 1080 -> Quality.FHD
            shortSide >= 720 -> Quality.HD
            else -> Quality.SD
        },
        // If the lens can't do that size, take the closest smaller one (or the next larger as a last resort).
        FallbackStrategy.lowerQualityOrHigherThan(Quality.SD),
    )

    override fun onSurfaceRequested(request: SurfaceRequest) = onSurface(request)

    override fun getMediaSpec(): Observable<MediaSpec> =
        ConstantObservable.withValue(MediaSpec.builder().configureVideo { it.setQualitySelector(selector) }.build())

    // Which sizes the camera can record at: the same table CameraX's own recorder uses.
    override fun getMediaCapabilities(cameraInfo: CameraInfo): VideoCapabilities = Recorder.getVideoCapabilities(cameraInfo)
}

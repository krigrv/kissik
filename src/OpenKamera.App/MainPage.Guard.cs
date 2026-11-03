using Microsoft.UI.Xaml;
using OpenKamera.Core;

namespace OpenKamera_App;

// Privacy guard (blur when a second face appears) and smart mirror (mirror only what you see).
public sealed partial class MainPage
{
    private readonly PrivacyGuard _guard = new();
    private volatile bool _guardOn, _guardHiding;

    private void InitGuardAndMirror()
    {
        GuardToggle.IsChecked = _guardOn = _settings.PrivacyGuard;
        SmartMirrorToggle.IsChecked = _settings.SmartMirror;
        PreviewFlip.ScaleX = _settings.SmartMirror ? -1 : 1;
    }

    private void GuardToggle_Click(object sender, RoutedEventArgs e)
    {
        _guardOn = _settings.PrivacyGuard = GuardToggle.IsChecked;
        _settings.Save();
        if (!_guardOn) { _guard.Reset(); _guardHiding = false; }
        Status(_guardOn ? "Privacy guard on: if someone walks into shot, the meeting sees a blur until you're alone again."
                        : "Privacy guard off.");
    }

    /// <summary>Detection thread: feeds the face count and flips the blur on or off.</summary>
    private void GuardUpdate(int faces)
    {
        if (!_guard.Update(faces, DateTime.UtcNow)) return;
        _guardHiding = _guard.Hiding;
        bool hiding = _guardHiding;
        DispatcherQueue.TryEnqueue(() => Status(hiding ? "Privacy guard: someone else is in shot, so the meeting sees a blur." : "Privacy guard: you're alone again, back to normal."));
    }

    private void SmartMirror_Click(object sender, RoutedEventArgs e)
    {
        _settings.SmartMirror = SmartMirrorToggle.IsChecked;
        // Smart mirror means the outgoing picture is the true view, so turn off the output mirror.
        if (_settings.SmartMirror && MirrorToggle.IsChecked == true) { MirrorToggle.IsChecked = false; UpdateTransform(); }
        _settings.Save();
        PreviewFlip.ScaleX = _settings.SmartMirror ? -1 : 1;
        Status(_settings.SmartMirror ? "Your preview is mirrored like a mirror; meetings and recordings see the true view (text reads correctly)."
                                     : "Preview shows exactly what meetings see.");
    }
}

using OpenKamera.Core;
using Microsoft.UI.Xaml;

namespace OpenKamera_App;

public sealed partial class MainPage
{
    private RecordingsWindow? _recordings;

    private void RecordingsBtn_Click(object sender, RoutedEventArgs e) => OpenRecordings();

    private void OpenRecordings()
    {
        string pictures = string.IsNullOrWhiteSpace(_settings.ImageFolder)
            ? AppPaths.Pictures : _settings.ImageFolder;
        if (_recordings is null)
        {
            _recordings = new RecordingsWindow(RecordingFolder(), pictures);
            _recordings.Closed += (_, _) => _recordings = null;
        }
        _recordings.Activate();
    }
}

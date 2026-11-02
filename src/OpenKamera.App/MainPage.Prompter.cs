using Microsoft.UI.Xaml;

namespace OpenKamera_App;

public sealed partial class MainPage
{
    private PrompterWindow? _prompter;

    private void PrompterBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_prompter is not null) { _prompter.Activate(); return; }
        _prompter = new PrompterWindow(_settings.Prompter, () => _settings.Save());
        _prompter.Closed += (_, _) => _prompter = null;
        _prompter.Activate();
        Status("Prompter open at the top of your screen. It is hidden from shares and recordings.");
    }
}

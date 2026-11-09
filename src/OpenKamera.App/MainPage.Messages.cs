using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace OpenKamera_App;

// Messages: what KISSIK just did or what went wrong. The latest shows bottom-right; a click opens it in full with
// the earlier ones; a message that has an obvious next step carries a button for it.
public sealed partial class MainPage
{
    private readonly List<(DateTime At, string Text, bool Problem)> _messages = [];
    private Action? _messageAction;

    private static readonly string[] ProblemWords =
        ["failed", "couldn't", "could not", "can't", "stopped working", "problem", "no sound", "is off for kissik", "isn't connected", "not found", "damaged", "unavailable", "didn't work"];

    /// <summary>True for messages that report something going wrong (they get a warning mark).</summary>
    internal static bool IsProblem(string msg) => ProblemWords.Any(w => msg.Contains(w, StringComparison.OrdinalIgnoreCase));

    private void Status(string msg) => Status(msg, null, null);

    /// <param name="action">Words for a button under the message ("Show in folder"), or null.</param>
    private void Status(string msg, string? action, Action? onAction)
    {
        bool problem = IsProblem(msg);
        StatusText.Text = msg;
        MessageIcon.Glyph = problem ? "\uE7BA" : "\uE946"; // warning, info
        MessageIcon.Foreground = Token(problem ? "SystemFillColorCautionBrush" : "DgDim");
        _messageAction = onAction;
        MessageAction.Content = action;
        MessageAction.Visibility = action is not null && onAction is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_messages.Count == 0 || _messages[^1].Text != msg)
        {
            _messages.Add((DateTime.Now, msg, problem));
            if (_messages.Count > 40) _messages.RemoveAt(0);
        }
        Announce(msg);
        SyncSimple();
    }

    private void MessageAction_Click(object sender, RoutedEventArgs e)
    {
        try { _messageAction?.Invoke(); }
        catch (Exception ex) { Status("That didn't work: " + ex.Message); }
    }

    private void MessageBtn_Click(object sender, RoutedEventArgs e)
    {
        MessageFull.Text = StatusText.Text;
        MessageHistory.Children.Clear();
        // Newest first, without the one already shown in full above.
        foreach (var (at, text, problem) in _messages.AsEnumerable().Reverse().Skip(1))
        {
            var line = new StackPanel { Spacing = 2 };
            line.Children.Add(new TextBlock { Text = (problem ? "Problem · " : "") + at.ToString("t"), FontSize = 12, Foreground = Token("DgDim") });
            line.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 });
            MessageHistory.Children.Add(line);
        }
        if (MessageHistory.Children.Count == 0)
            MessageHistory.Children.Add(new TextBlock { Text = "No earlier messages.", FontSize = 12, Foreground = Token("DgDim") });
        FlyoutBase.ShowAttachedFlyout(MessageBtn);
    }

    /// <summary>Opens File Explorer with this file selected (or the folder, when it is one).</summary>
    private static void ShowInFolder(string path)
    {
        if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        else System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}

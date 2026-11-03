using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using OpenKamera.Core;

namespace OpenKamera_App;

// The native phone app's camera controls (lens, exposure, ISO/shutter, focus, white balance, frame rate) and its
// H.264 stream. The browser page reports none of these, so for it the panel stays hidden.
public sealed partial class MainPage
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private bool _proLoading;
    private DateTime _proTouched; // while the user drags, the phone's next status mustn't yank the slider back
    private PhoneStatus? _proStatus;

    private bool PhoneIsSource => DeviceBox.SelectedItem == PhoneDevice && _session is not null;

    private void UpdatePhonePro(PhoneStatus st)
    {
        _proStatus = st;
        bool native = st.Codecs.Count > 0 || st.Lenses.Count > 0;
        PhonePro.Visibility = PhoneIsSource && native ? Visibility.Visible : Visibility.Collapsed;
        if (PhonePro.Visibility != Visibility.Visible) return;
        bool fresh = DateTime.UtcNow - _proTouched > TimeSpan.FromSeconds(2);
        _proLoading = true;
        try
        {
            Show(PhoneLens, st.Lenses.Count > 1);
            if (st.Lenses.Count > 1)
            {
                var ids = PhoneLens.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag);
                if (!ids.SequenceEqual(st.Lenses.Select(l => l.Id)))
                {
                    PhoneLens.Items.Clear();
                    foreach (var l in st.Lenses) PhoneLens.Items.Add(new ComboBoxItem { Content = l.Label, Tag = l.Id });
                }
                PhoneLens.SelectedItem = PhoneLens.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == st.Lens);
                PhoneLens.PlaceholderText = st.Facing == "front" ? "Front" : "Back";
            }

            Show(PhoneEv, st.HasEv);
            if (st.HasEv)
            {
                PhoneEv.Minimum = st.EvMin!.Value; PhoneEv.Maximum = st.EvMax!.Value;
                if (fresh) PhoneEv.Value = st.Ev ?? 0;
                PhoneEv.ThumbToolTipValueConverter = new EvLabel(st.EvStep!.Value);
                PhoneEv.Header = "Exposure " + EvLabel.Format(PhoneEv.Value * st.EvStep.Value);
            }

            Show(PhoneIsoRow, st.HasIso);
            if (st.HasIso)
            {
                PhoneIso.Minimum = st.IsoMin!.Value; PhoneIso.Maximum = st.IsoMax!.Value;
                if (fresh) { PhoneIsoAuto.IsChecked = st.Iso is null; if (st.Iso is { } iso) PhoneIso.Value = iso; }
                PhoneIso.IsEnabled = PhoneIsoAuto.IsChecked != true;
                PhoneIso.Header = PhoneIsoAuto.IsChecked == true ? "ISO (auto)" : $"ISO {PhoneIso.Value:0}";
            }

            Show(PhoneShutterRow, st.HasShutter);
            if (st.HasShutter)
            {
                if (fresh) { PhoneShutterAuto.IsChecked = st.Shutter is null; if (st.Shutter is { } ns) PhoneShutter.Value = ShutterToSlider(ns, st); }
                PhoneShutter.IsEnabled = PhoneShutterAuto.IsChecked != true;
                PhoneShutter.Header = PhoneShutterAuto.IsChecked == true ? "Shutter (auto)" : "Shutter " + ShutterLabel(SliderToShutter(PhoneShutter.Value, st));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(PhoneShutter, ShutterLabel(SliderToShutter(PhoneShutter.Value, st)));
            }

            Show(PhoneFocusRow, st.FocusManual);
            if (st.FocusManual && fresh)
            {
                PhoneFocusAuto.IsChecked = st.Focus is null;
                if (st.Focus is { } f) PhoneFocus.Value = f * 100;
            }
            PhoneFocus.IsEnabled = PhoneFocusAuto.IsChecked != true;

            Show(PhoneWb, st.WbModes.Count > 1);
            if (st.WbModes.Count > 1)
            {
                if (!PhoneWb.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).SequenceEqual(st.WbModes))
                {
                    PhoneWb.Items.Clear();
                    foreach (var m in st.WbModes) PhoneWb.Items.Add(new ComboBoxItem { Content = Nice(m), Tag = m });
                }
                PhoneWb.SelectedItem = PhoneWb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == st.Wb);
            }

            Show(PhoneFps, st.FpsList.Count > 0);
            if (st.FpsList.Count > 0)
            {
                var want = new[] { 0 }.Concat(st.FpsList).ToList();
                if (!PhoneFps.Items.OfType<ComboBoxItem>().Select(i => (int)i.Tag).SequenceEqual(want))
                {
                    PhoneFps.Items.Clear();
                    foreach (var f in want) PhoneFps.Items.Add(new ComboBoxItem { Content = f == 0 ? "Auto" : $"{f} fps", Tag = f });
                }
                PhoneFps.SelectedItem = PhoneFps.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == (st.Fps ?? 0));
            }

            Show(PhoneStabBtn, st.Stab is not null); PhoneStabBtn.IsChecked = st.Stab == true;
            Show(PhoneHdrBtn, st.Hdr is not null); PhoneHdrBtn.IsChecked = st.Hdr == true;
            Show(PhoneH264Box, st.CanH264); PhoneH264Box.IsChecked = _settings.PhoneH264;
        }
        finally { _proLoading = false; }

        static void Show(UIElement e, bool on) => e.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Nice(string mode)
    {
        var s = mode.Replace('-', ' ').Replace('_', ' ').ToLowerInvariant();
        return s.Length == 0 ? mode : char.ToUpperInvariant(s[0]) + s[1..];
    }

    // Shutter slider runs on a log scale: equal steps feel like equal changes in brightness.
    private static double ShutterToSlider(long ns, PhoneStatus st)
    {
        double lo = Math.Log(st.ShutterMin!.Value), hi = Math.Log(st.ShutterMax!.Value);
        return Math.Clamp((Math.Log(Math.Max(1, ns)) - lo) / (hi - lo) * 1000, 0, 1000);
    }
    private static long SliderToShutter(double v, PhoneStatus st)
    {
        double lo = Math.Log(st.ShutterMin!.Value), hi = Math.Log(st.ShutterMax!.Value);
        return (long)Math.Round(Math.Exp(lo + (hi - lo) * v / 1000));
    }
    internal static string ShutterLabel(long ns)
    {
        double s = ns / 1e9;
        return s >= 0.3 ? $"{s.ToString("0.#", Inv)} s" : $"1/{Math.Round(1 / s):0} s";
    }

    private sealed partial class EvLabel(double step) : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public static string Format(double ev) => ev == 0 ? "0" : (ev > 0 ? "+" : "−") + Math.Abs(ev).ToString("0.0", Inv);
        public object Convert(object value, Type targetType, object parameter, string language) => Format(System.Convert.ToDouble(value) * step);
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }

    private void SendPhone(string command) { if (_phone is { } p) _ = p.SendAsync(command); }

    private void PhoneLens_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_proLoading || PhoneLens.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        SendPhone("lens:" + id);
    }

    private void PhoneEv_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_proStatus?.EvStep is { } step) PhoneEv.Header = "Exposure " + EvLabel.Format(e.NewValue * step);
        if (_proLoading) return;
        _proTouched = DateTime.UtcNow;
        SendPhone("ev:" + ((int)Math.Round(e.NewValue)).ToString(Inv));
    }

    private void PhoneIso_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (PhoneIsoAuto is null) return; // during XAML load
        if (PhoneIsoAuto.IsChecked != true) PhoneIso.Header = $"ISO {e.NewValue:0}";
        if (_proLoading || PhoneIsoAuto.IsChecked == true) return;
        _proTouched = DateTime.UtcNow;
        SendPhone("iso:" + ((int)Math.Round(e.NewValue)).ToString(Inv));
    }

    private void PhoneIsoAuto_Click(object sender, RoutedEventArgs e)
    {
        _proTouched = DateTime.UtcNow;
        bool auto = PhoneIsoAuto.IsChecked == true;
        PhoneIso.IsEnabled = !auto;
        PhoneIso.Header = auto ? "ISO (auto)" : $"ISO {PhoneIso.Value:0}";
        SendPhone(auto ? "iso:auto" : "iso:" + ((int)Math.Round(PhoneIso.Value)).ToString(Inv));
    }

    private void PhoneShutter_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_proStatus is not { HasShutter: true } st || PhoneShutterAuto is null) return;
        long ns = SliderToShutter(e.NewValue, st);
        if (PhoneShutterAuto.IsChecked != true) PhoneShutter.Header = "Shutter " + ShutterLabel(ns);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(PhoneShutter, ShutterLabel(ns));
        if (_proLoading || PhoneShutterAuto.IsChecked == true) return;
        _proTouched = DateTime.UtcNow;
        SendPhone("shutter:" + ns.ToString(Inv));
    }

    private void PhoneShutterAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_proStatus is not { HasShutter: true } st) return;
        _proTouched = DateTime.UtcNow;
        bool auto = PhoneShutterAuto.IsChecked == true;
        PhoneShutter.IsEnabled = !auto;
        long ns = SliderToShutter(PhoneShutter.Value, st);
        PhoneShutter.Header = auto ? "Shutter (auto)" : "Shutter " + ShutterLabel(ns);
        SendPhone(auto ? "shutter:auto" : "shutter:" + ns.ToString(Inv));
    }

    private void PhoneFocus_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_proLoading || PhoneFocusAuto is null || PhoneFocusAuto.IsChecked == true) return;
        _proTouched = DateTime.UtcNow;
        SendPhone("focus:" + (e.NewValue / 100).ToString("0.00", Inv));
    }

    private void PhoneFocusAuto_Click(object sender, RoutedEventArgs e)
    {
        _proTouched = DateTime.UtcNow;
        bool auto = PhoneFocusAuto.IsChecked == true;
        PhoneFocus.IsEnabled = !auto;
        SendPhone(auto ? "focus:auto" : "focus:" + (PhoneFocus.Value / 100).ToString("0.00", Inv));
    }

    private void PhoneWb_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_proLoading || PhoneWb.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        SendPhone("wb:" + mode);
    }

    private void PhoneFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_proLoading || PhoneFps.SelectedItem is not ComboBoxItem { Tag: int fps }) return;
        SendPhone(fps == 0 ? "fps:auto" : "fps:" + fps.ToString(Inv));
    }

    private void PhoneStab_Click(object sender, RoutedEventArgs e) => SendPhone(PhoneStabBtn.IsChecked == true ? "stab:on" : "stab:off");
    private void PhoneHdr_Click(object sender, RoutedEventArgs e) => SendPhone(PhoneHdrBtn.IsChecked == true ? "hdr:on" : "hdr:off");

    /// <summary>Click on the picture while the phone is the camera: focus and meter there.</summary>
    private bool TryPhoneTapFocus(PointerRoutedEventArgs e)
    {
        if (!PhoneIsSource || _proStatus is not { } st || (st.Codecs.Count == 0 && !st.FocusManual)) return false;
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0) return false;
        // Relative to the image itself, so the on-screen mirror (a render transform) is already undone.
        var p = e.GetCurrentPoint(PreviewImage).Position;
        double nx = p.X / PreviewImage.ActualWidth, ny = p.Y / PreviewImage.ActualHeight;
        if (nx is < 0 or > 1 || ny is < 0 or > 1) return false;
        var (u, v) = _transform.ToSource(nx, ny); // undo KISSIK's own mirror, rotation and framing
        SendPhone($"focus:tap:{u.ToString("0.000", Inv)},{v.ToString("0.000", Inv)}");
        if (st.FocusManual) { _proLoading = true; PhoneFocusAuto.IsChecked = true; PhoneFocus.IsEnabled = false; _proLoading = false; }
        Status("Focusing the phone there.");
        return true;
    }

    // ───── H.264 ─────

    private H264Decoder? _h264;
    private int _h264In, _h264Out, _h264InRate, _h264OutRate; private DateTime _h264RateAt;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _arrivals = new();
    private double _netBase = double.MaxValue, _netExtraMs, _decodeMs, _phoneScreenMs; private DateTime _netBaseAt; private long _lastArrivalTicks;

    /// <summary>Called when a phone frame reaches the screen: time since its packet arrived from the network.</summary>
    private void NotePhoneShown()
    {
        long t0 = Volatile.Read(ref _lastArrivalTicks);
        if (t0 == 0) return;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms < 2000) _phoneScreenMs = _phoneScreenMs * 0.9 + ms * 0.1;
    }

    /// <summary>Packets in and pictures out per second, for the HUD (shows where a slowdown is).</summary>
    private string PhoneRates()
    {
        if (_h264 is null) return "";
        var now = DateTime.UtcNow;
        if (now - _h264RateAt >= TimeSpan.FromSeconds(1))
        {
            double s = (now - _h264RateAt).TotalSeconds; _h264RateAt = now;
            _h264InRate = (int)(Interlocked.Exchange(ref _h264In, 0) / s); _h264OutRate = (int)(Interlocked.Exchange(ref _h264Out, 0) / s);
        }
        var st = _phone?.LastStatus;
        string phoneSide = st?.CapEncMs is { } ce ? $" · on phone {ce:0} ms" : "";
        return $" · phone {_h264InRate} in / {_h264OutRate} decoded{phoneSide} · network +{_netExtraMs:0} ms · decode {_decodeMs:0} ms · to screen {_phoneScreenMs:0} ms";
    }
    private bool _h264Failed;      // gave up for this run: stay on JPEG
    private bool _h264Asked;       // codec:h264 sent on this connection
    private readonly Queue<DateTime> _h264Errors = new();

    /// <summary>On each status: start H.264 when the phone offers it and the option is on, or go back to JPEG.</summary>
    private void UpdatePhoneCodec(PhoneStatus st)
    {
        if (_h264 is { } d) d.Rotation = st.Rotation;
        bool want = PhoneIsSource && st.CanH264 && _settings.PhoneH264 && !_h264Failed;
        if (!want)
        {
            if (st.Codec == "h264") SendPhone("codec:jpeg");
            _h264Asked = false;
            return;
        }
        if (_h264Asked) return;
        if (_h264 is null)
        {
            try
            {
                var dec = H264Decoder.Create();
                dec.DropStaleFrames = true; // live: never let pictures queue up behind the phone
                dec.OutputNv12 = FrameProcessor.GpuActive; // the GPU turns NV12 into colour, not this thread
                dec.FrameDecoded += f =>
                {
                    Interlocked.Increment(ref _h264Out);
                    if (_arrivals.TryRemove(f.TimestampUs, out long t0))
                    {
                        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                        _decodeMs = _decodeMs * 0.9 + (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency * 0.1;
                        Volatile.Write(ref _lastArrivalTicks, t0);
                    }
                    _session?.PushBgra(f);
                };
                dec.Error += _ => DispatcherQueue.TryEnqueue(OnH264Error);
                dec.Rotation = st.Rotation;
                _h264 = dec;
            }
            catch (Exception ex)
            {
                FallBackToJpeg($"H.264 isn't available on this PC ({ex.Message}). Staying on the standard stream.");
                return;
            }
        }
        // Only now that the decoder is ready: ask for H.264, then a keyframe to start from.
        _h264Asked = true;
        long before = _h264.FramesDecoded;
        SendPhone("codec:h264");
        SendPhone("key");
        Status(_h264.Hardware ? "Sharper stream on: H.264, decoded by the graphics card." : "Sharper stream on: H.264.");
        // Watchdog: no picture within 4 s means it isn't working here.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4); timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_h264Asked && _h264 is { } h && h.FramesDecoded == before && _phone?.PhoneConnected == true)
                FallBackToJpeg("The sharper stream didn't start. Switched back to the standard stream.");
        };
        timer.Start();
    }

    private void OnPhoneVideo(byte[] data)
    {
        Interlocked.Increment(ref _h264In);
        if (_h264 is not { } d || PhoneVideoPacket.TryParse(data) is not { } p) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _arrivals[p.TimestampUs] = now;
        if (_arrivals.Count > 120) _arrivals.Clear();
        // Network: arrival minus the phone's capture time, relative to the best seen lately = extra queueing delay.
        double rel = now * 1000.0 / System.Diagnostics.Stopwatch.Frequency - p.TimestampUs / 1000.0;
        if (rel < _netBase || DateTime.UtcNow - _netBaseAt > TimeSpan.FromSeconds(20)) { _netBase = rel; _netBaseAt = DateTime.UtcNow; }
        _netExtraMs = _netExtraMs * 0.9 + (rel - _netBase) * 0.1;
        if (!d.Push(p)) SendPhone("key"); // fell behind and dropped: restart from a fresh keyframe
    }

    private void OnH264Error()
    {
        var now = DateTime.UtcNow;
        _h264Errors.Enqueue(now);
        while (_h264Errors.Count > 0 && now - _h264Errors.Peek() > TimeSpan.FromSeconds(10)) _h264Errors.Dequeue();
        if (_h264Errors.Count >= 5) FallBackToJpeg("The sharper stream kept breaking up. Switched back to the standard stream.");
        else SendPhone("key");
    }

    private void FallBackToJpeg(string message)
    {
        _h264Failed = true;
        _h264Asked = false;
        SendPhone("codec:jpeg");
        _h264?.Dispose(); _h264 = null;
        Status(message);
    }

    /// <summary>The phone stopped being the camera: go back to JPEG so the phone does less work.</summary>
    private void StopPhoneH264()
    {
        if (_h264Asked) SendPhone("codec:jpeg");
        _h264Asked = false;
        _h264?.Dispose(); _h264 = null;
    }

    private void PhoneH264_Click(object sender, RoutedEventArgs e)
    {
        _settings.PhoneH264 = PhoneH264Box.IsChecked == true;
        _settings.Save();
        if (_settings.PhoneH264) { _h264Failed = false; _h264Errors.Clear(); }
        else { StopPhoneH264(); Status("Standard stream (JPEG)."); }
        if (_phone?.LastStatus is { } st) UpdatePhoneCodec(st);
    }
}

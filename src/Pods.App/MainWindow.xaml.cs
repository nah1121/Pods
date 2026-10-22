using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pods.Core;


namespace Pods.App;

/// <summary>
/// WPF front-end for the AirPods copycat. All device work happens on a background task
/// because the driver's IO queue is synchronous - writes and reads cannot overlap.
/// </summary>
public partial class MainWindow : Window
{
    AapSession _session;
    readonly SemaphoreSlim _busy = new(1, 1);
    bool _busyFlag;
    bool _suppressEvents;
    int _autoReconnects;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ConnectAsync();
        Closing += (_, _) => _session?.Dispose();

        // Battery can go quiet without any inbound frame arriving, so poll the UI
        // independently to keep the staleness indicator honest.
        var timer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (_session == null) return;
            OnState();

            // The driver hands the push stream to a single reader. If another client
            // (e.g. MagicPods) grabs the channel we go silent, so recover by
            // reconnecting rather than leaving frozen readings on screen.
            if (_session.IsStale && !_busyFlag && _autoReconnects < 3)
            {
                _autoReconnects++;
                Log("No data for 20s - reconnecting automatically.");
                _ = ConnectAsync(auto: true);
            }
        };
        timer.Start();
    }

    // ---------------------------------------------------------------- logging

    void Log(string s)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    void SetStatus(string text, bool ok)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusDot.Fill = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(ok ? "#2ECC71" : "#F04438"));
        });
    }

    void SetBusy(bool busy)
    {
        Dispatcher.Invoke(() =>
        {
            _busyFlag = busy;
            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            FooterHint.Text = busy ? "Applying..." : "";
        });
    }

    // ---------------------------------------------------------------- state

    void OnState()
    {
        var s = _session;
        if (s == null) return;
        Dispatcher.Invoke(() =>
        {
            UpdateBattery(s);
            UpdateIdentity(s);
            UpdateEar(s);
            SyncControls(s);
        });
    }

    void UpdateBattery(AapSession s)
    {
        int stale = s.SecondsSinceBattery;
        bool quiet = stale > 15;

        BatteryPanel.Visibility = Visibility.Visible;

        SetGauge(LeftBatteryFill, LeftBatteryText, s.LeftBattery, s.LeftStatus, quiet);
        SetGauge(RightBatteryFill, RightBatteryText, s.RightBattery, s.RightStatus, quiet);

        // The case reports "disconnected" when it is empty (no pods in it), which is
        // not the same as "not charging" - show a dash rather than a fake 0%.
        bool casePresent = s.CaseStatus is string cs && cs != "disconnected";
        if (casePresent)
        {
            CaseText.Text = s.CaseBattery is int c
                ? $"C {c}%{(s.CaseStatus == "charging" ? " charging" : "")}"
                : "C --";
        }
        else
        {
            CaseText.Text = "C --";
        }
        CaseText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(casePresent ? "#8B95A7" : "#5C6678"));

        FooterHint.Text = s.LastBatteryAt is null
            ? "Waiting for the first battery reading - the buds only report while active"
            : quiet
              ? $"Last update {stale}s ago - buds report about every minute"
              : $"{s.FrameCount} frames received";
    }

    static void SetGauge(Rectangle fill, TextBlock label, int? pct, string status, bool quiet)
    {
        // "disconnected" means the bud is not reporting at all - typically because it is
        // in a closed case. The device sends level 0 with that status, so showing "0%"
        // would be wrong.
        if (status == "disconnected")
        {
            fill.Width = 0;
            label.Text = "in case";
            label.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C6678"));
            return;
        }

        if (pct is int p)
        {
            fill.Width = 86 * Math.Clamp(p, 0, 100) / 100.0;
            string suffix = status switch
            {
                "charging" => " charging",
                "not charging" => "",
                "unknown" => " no read",
                _ => ""
            };
            label.Text = $"{p}%{suffix}";
            label.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                status == "charging" ? "#2ECC71" : quiet ? "#5C6678" : "#8B95A7"));
        }
        else
        {
            fill.Width = 0;
            label.Text = status == "disconnected" ? "in case" : "--";
            label.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C6678"));
        }
    }

    void UpdateIdentity(AapSession s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(s.ModelId)) parts.Add(s.ModelId);
        if (!string.IsNullOrEmpty(s.Serial)) parts.Add(s.Serial);
        if (!string.IsNullOrEmpty(s.Firmware)) parts.Add("fw " + s.Firmware);
        IdentityText.Text = string.Join("  ·  ", parts);

        if (!string.IsNullOrEmpty(s.ModelName)) DeviceName.Text = s.ModelName;
    }

    void UpdateEar(AapSession s)
    {
        // cmd 0x06 carries the state of both buds, but not in a fixed left/right
        // order. The counts are always reliable; the per-side names only when the
        // mapping could be resolved.
        var parts = new List<string>();
        if (s.InEarCount > 0) parts.Add($"{s.InEarCount} in ear");
        if (s.OutOfEarCount > 0) parts.Add($"{s.OutOfEarCount} out");
        if (s.InCaseCount > 0) parts.Add($"{s.InCaseCount} in case");

        if (s.LeftState is byte l && s.RightState is byte r)
        {
            parts.Insert(0, $"L {AapSession.BudStateName(l)}  R {AapSession.BudStateName(r)}");
        }

        EarText.Text = parts.Count > 0 ? string.Join("   ", parts) : "";
        EarText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(s.InEarCount == 2 ? "#2ECC71" : "#8B95A7"));
    }

    /// <summary>Reflect the device-confirmed state on the controls.</summary>
    void SyncControls(AapSession s)
    {
        _suppressEvents = true;
        try
        {
            Check(AncOff, AncOn, AncTransparency, AncAdaptive, s.AncMode);
            Check(PressSpeed0, PressSpeed1, PressSpeed2, s.PressSpeed);
            Check(PressHold0, PressHold1, PressHold2, s.PressHold);
            Check(SwipeLen0, SwipeLen1, SwipeLen2, s.VolumeSwipeLength);

            if (s.ConversationAwareness is byte ca) CaSwitch.IsChecked = ca == 1;
            if (s.VolumeSwipe is byte vs) VSwipeSwitch.IsChecked = vs == 1;
            if (s.PersonalizedVolume is byte pv) PersonalizedSwitch.IsChecked = pv == 1;
            if (s.OneAirPod is byte oa) OneAirPodSwitch.IsChecked = oa == 1;
            if (s.ToneVolume is byte tv) { ToneSlider.Value = Math.Clamp((int)tv, 15, 100); ToneValue.Text = tv.ToString(); }
        }
        finally { _suppressEvents = false; }

        AncStateText.Text = s.AncMode is byte m
            ? $"Device reports: {AapSession.AncName(m)}"
            : "";
    }

    /// <summary>Select the radio button whose Tag matches the device value.</summary>
    void Check(params object[] radioAndValue)
    {
        for (int i = 0; i < radioAndValue.Length; i += 2)
        {
            if (radioAndValue[i] is RadioButton rb && radioAndValue[i + 1] is byte v
                && byte.TryParse(rb.Tag?.ToString(), out var tag) && tag == v)
                rb.IsChecked = true;
        }
    }

    // ---------------------------------------------------------------- connect

    async Task ConnectAsync(bool auto = false)
    {
        if (!await _busy.WaitAsync(0)) return;
        if (auto)
        {
            SetStatus("Reconnecting...", false);
        }
        else
        {
            _autoReconnects = 0;
            SetBusy(true);
            SetStatus("Connecting...", false);
        }

        try
        {
            var session = await Task.Run(() => AapSession.Connect());
            session.Log += Log;
            _session?.Dispose();
            _session = session;
            Log($"Connected: {session.DevicePath}");
            SetStatus("Connected", true);

            // Order matters: the driver queue is synchronous+sequential, so all writes
            // must complete before the blocking receive loop is started.
            await Task.Run(() => session.Handshake());
            Log("Session initialised.");

            // Re-apply saved preferences on a cold session (writes must precede the
            // receive loop, same as the handshake).
            if (!auto && RestoreOnLaunch.IsChecked == true && session.Store.Packets().Any())
            {
                int n = await Task.Run(() => session.RestoreSettings());
                Log($"Restored {n} saved setting(s).");
            }

            Log("Starting receive loop...");
            session.StartReceiving();
            session.StateChanged += OnState;
            Log("Receive loop running.");
        }
        catch (Exception ex)
        {
            Log("Connect failed: " + ex.Message);
            SetStatus("Not connected", false);
        }
        finally { SetBusy(false); _busy.Release(); }
    }

    /// <summary>Run one apply on a background thread, guarding against overlapping applies.</summary>
    async Task ApplyAsync(string label, Func<AapSession, int> op, byte? sub = null)
    {
        if (_session == null)
        {
            Log("Not connected.");
            return;
        }
        if (!await _busy.WaitAsync(0))
        {
            Log("Busy - ignoring overlapping request.");
            return;
        }
        SetBusy(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var bytes = await Task.Run(() => op(_session));
            Log($"{label} applied ({bytes} B) in {sw.ElapsedMilliseconds} ms.");
            if (sub.HasValue && AapSession.IsWriteOnly(sub.Value))
                Log($"   {AapSession.SettingName(sub.Value)} is write-only on these AirPods - "
                    + "sent, but the hardware never echoes it.");
        }
        catch (Exception ex)
        {
            Log($"{label} failed: {ex.Message}");
        }
        finally { SetBusy(false); _busy.Release(); }
    }

    // ---------------------------------------------------------------- handlers

    private void Anc_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is RadioButton { Tag: string tag } rb && rb.IsChecked == true
            && byte.TryParse(tag, out var mode))
        {
            _ = ApplyAsync($"Noise control -> {AapSession.AncName(mode)}", s => s.SetAnc(mode), 0x0d);
        }
    }

    private void PressSpeed_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is RadioButton { Tag: string tag } rb && rb.IsChecked == true
            && byte.TryParse(tag, out var m))
            _ = ApplyAsync($"Press speed -> {AapSession.PressSpeedName(m)}", s => s.SetPressSpeed(m), 0x17);
    }

    private void PressHold_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is RadioButton { Tag: string tag } rb && rb.IsChecked == true
            && byte.TryParse(tag, out var m))
            _ = ApplyAsync($"Press and hold -> {AapSession.PressHoldName(m)}", s => s.SetPressAndHoldDuration(m), 0x18);
    }

    private void SwipeLen_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is RadioButton { Tag: string tag } rb && rb.IsChecked == true
            && byte.TryParse(tag, out var m))
            _ = ApplyAsync($"Volume swipe length -> {AapSession.SwipeLengthName(m)}", s => s.SetVolumeSwipeLength(m), 0x23);
    }

    private void Ca_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = CaSwitch.IsChecked == true;
        _ = ApplyAsync($"Conversation awareness -> {(on ? "On" : "Off")}", s => s.SetConversationAwareness(on), 0x28);
    }

    private void VSwipe_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = VSwipeSwitch.IsChecked == true;
        _ = ApplyAsync($"Volume swipe -> {(on ? "On" : "Off")}", s => s.SetVolumeSwipe(on), 0x25);
    }

    private void Personalized_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = PersonalizedSwitch.IsChecked == true;
        _ = ApplyAsync($"Personalized volume -> {(on ? "On" : "Off")}", s => s.SetPersonalizedVolume(on), 0x26);
    }

    private void OneAirPod_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = OneAirPodSwitch.IsChecked == true;
        _ = ApplyAsync($"ANC with one AirPod -> {(on ? "On" : "Off")}", s => s.SetNoiseCancellationOneAirPod(on), 0x1b);
    }

    private void Tone_Changed(object sender, RoutedEventArgs e)
    {
        // ValueChanged also fires while the XAML is still being parsed, so the sibling
        // elements may not exist yet.
        if (ToneSlider == null || ToneValue == null || ToneValueText == null) return;
        ToneValue.Text = ((int)ToneSlider.Value).ToString();
        ToneValueText.Text = $"{ToneSlider.Value:F0}%";
    }

    private void Adaptive_Changed(object sender, RoutedEventArgs e)
    {
        if (AdaptiveSlider == null || AdaptiveValue == null || AdaptiveValueText == null) return;
        AdaptiveValue.Text = ((int)AdaptiveSlider.Value).ToString();
        AdaptiveValueText.Text = $"{AdaptiveSlider.Value:F0}%";
    }

    private async void Tone_Apply(object sender, RoutedEventArgs e)
    {
        var v = (byte)Math.Clamp((int)ToneSlider.Value, 15, 100);
        await ApplyAsync($"Tone volume -> {v}", s => s.SetToneVolume(v), 0x1f);
    }

    private async void Adaptive_Apply(object sender, RoutedEventArgs e)
    {
        var v = (byte)Math.Clamp((int)AdaptiveSlider.Value, 0, 100);
        await ApplyAsync($"Adaptive audio noise -> {v}%", s => s.SetAdaptiveAudioNoise(v), 0x2e);
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    /// <summary>
    /// Force an immediate state refresh. Re-opens the device, which is what makes the
    /// AirPods re-dump their state.
    /// </summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || !await _busy.WaitAsync(0)) return;
        SetBusy(true);
        try
        {
            await Task.Run(() => _session.RefreshState());
            Log("Refresh requested.");
        }
        catch (Exception ex) { Log("Refresh failed: " + ex.Message); }
        finally { SetBusy(false); _busy.Release(); }
    }

    /// <summary>
    /// Full reconnect. Needed when the AirPods drop off and come back - Windows
    /// creates a new BTHENUM instance then, so the old device path is dead.
    /// </summary>
    private async void ReconnectDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || !await _busy.WaitAsync(0)) return;
        SetBusy(true);
        try
        {
            await Task.Run(() => _session.Reconnect());
            Log("Reconnected to the AirPods.");
            SetStatus("Connected", true);
        }
        catch (Exception ex)
        {
            Log("Reconnect failed: " + ex.Message);
            SetStatus("Not connected", false);
        }
        finally { SetBusy(false); _busy.Release(); }
    }

    private void Restore_Changed(object sender, RoutedEventArgs e) { }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();
}
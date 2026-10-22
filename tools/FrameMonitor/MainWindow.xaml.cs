using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pods.Core;

namespace FrameMonitor;

/// <summary>
/// Live AAP frame monitor.
///
/// Shows every frame the AirPods send, in order, so unknown codes can be identified
/// empirically. Anything you see can be tagged with what it means, and the tags are
/// saved next to the app - which is how the cmd 0x06 / 0x08 layout was worked out.
/// </summary>
public partial class MainWindow : Window
{
    AapSession _session;
    volatile bool _paused;
    // Each queued item carries its own key. Sharing a single field for this meant
    // every frame flushed in one batch got the same key, so tagging two frames
    // wrote the same key twice and the second tag replaced the first.
    readonly ConcurrentQueue<(string Line, string Key)> _pending = new();
    readonly List<(string Key, string Label)> _map = new();
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    string _lastKey;
    string _filter = "";

    /// <summary>Every frame received this session, in order, so bursts can be tagged.</summary>
    readonly List<(string Key, string Line)> _all = new();
    int _markIndex;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ConnectAsync();
        Closing += (_, _) => { AapSession.CaptureAllFrames = false; _session?.Dispose(); };
        LoadMap();
    }

    static string MapPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PodsClone", "frame-map.json");

    /// <summary>
    /// Every frame is appended here as it arrives, so a session can be analysed
    /// afterwards. "Mark" writes a timestamped note into the same file, which is
    /// what lets a physical state be lined up with the frames around it.
    /// </summary>
    static string LogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PodsClone", "frame-log.txt");

    static readonly object LogLock = new();

    static void AppendLog(string line)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { }
    }

    async Task ConnectAsync()
    {
        StatusText.Text = "Connecting...";
        try
        {
            var s = await Task.Run(() => AapSession.Connect());
            AapSession.CaptureAllFrames = true;
            s.Log += l => Dispatcher.Invoke(() => { });
            _session = s;
            await Task.Run(() => s.Handshake());
            s.StartReceiving();
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Connected - live";
                StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2ECC71"));
            });

            var pump = new Thread(Loop) { IsBackground = true, Name = "frame-pump" };
            pump.Start();
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusText.Text = "Not connected: " + ex.Message);
        }
    }

    void Loop()
    {
        while (true)
        {
            bool any = false;
            while (AapSession.RawFrames.TryDequeue(out var f))
            {
                any = true;
                if (_paused) continue;

                string key = KeyOf(f);
                bool isNew = _seen.Add(key);
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] cmd 0x{f[4]:x2} ({f.Length,3} B)  {Hex.F(f)}"
                            + (isNew ? "   <new>" : "");

                Dispatcher.Invoke(() =>
                {
                    // Track the most recent frame of any kind, so "Tag last frame"
                    // always tags what just arrived - not the last unseen code.
                    _lastKey = key;
                    _all.Add((key, line));
                    _pending.Enqueue((line, key));
                });
                AppendLog(line);
            }
            if (any)
            {
                _lastFrameAt = DateTime.Now;
                Dispatcher.Invoke(Flush);
            }
            else
            {
                // The AirPods dropping off Bluetooth leaves the driver's read blocked
                // forever, so the feed just stops. Reconnect on our own initiative.
                int quiet = (int)(DateTime.Now - _lastFrameAt).TotalSeconds;
                if (quiet > 25 && !_reconnecting && _autoReconnects < 10)
                {
                    _autoReconnects++;
                    Dispatcher.Invoke(() => StatusText.Text = $"No frames for {quiet}s - reconnecting...");
                    Dispatcher.Invoke(() => Reconnect_Click(this, null));
                }
            }
            Thread.Sleep(60);
        }
    }

    void Flush()
    {
        int added = 0;
        while (_pending.TryDequeue(out var p) && added < 200)
        {
            string line = p.Line;
            if (_filter.Length > 0 && !line.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var item = new ListBoxItem { Content = line, Tag = p.Key };
            // Show an existing label inline so it is obvious what is already tagged.
            string existing = LabelOf(p.Key);
            if (existing != null) item.Content = line + "   =>  " + existing;
            FrameList.Items.Add(item);
            added++;
        }
        while (FrameList.Items.Count > 500) FrameList.Items.RemoveAt(0);
        if (added > 0) FrameList.ScrollIntoView(FrameList.Items[^1]);

        FrameCount.Text = FrameList.Items.Count.ToString();
        if (_lastKey != null)
            LastFrame.Text = "last frame: " + _lastKey;
    }

    static string KeyOf(byte[] f) =>
        f.Length >= 6 ? $"cmd 0x{f[4]:x2} {Hex.F(f)}" : Hex.F(f);

    void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        FrameList.Items.Clear();
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        Pause.Content = _paused ? "Resume" : "Pause";
        StatusText.Text = _paused ? "Paused" : "Connected - live";
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        FrameList.Items.Clear();
        _seen.Clear();
        _lastKey = null;
    }

    /// <summary>Write a timestamped marker into the log, e.g. "right pod in ear".</summary>
    void Mark_Click(object sender, RoutedEventArgs e)
    {
        string note = MarkBox.Text.Trim();
        if (note.Length == 0) note = "(no note)";
        string line = $"=== MARK [{DateTime.Now:HH:mm:ss.fff}] {note} ===";
        AppendLog(line);
        _pending.Enqueue((line, null));
        Flush();
        MarkBox.Clear();
        // Everything after this point is the burst caused by the next action.
        _markIndex = _all.Count;
        FooterHint.Text = "Marked: " + note;
    }

    void MarkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Mark_Click(sender, e);
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(LogPath)) { FooterHint.Text = "No log yet."; return; }
            System.Diagnostics.Process.Start("notepad.exe", LogPath);
            FooterHint.Text = "Opened " + LogPath;
        }
        catch (Exception ex) { FooterHint.Text = "Could not open log: " + ex.Message; }
    }

    // ---- reconnect ---------------------------------------------------------

    DateTime _lastFrameAt = DateTime.Now;
    int _autoReconnects;

    /// <summary>
    /// Reconnect to the driver. Needed when the AirPods drop off and come back:
    /// Windows creates a new BTHENUM instance, so the old device path is dead and
    /// the blocked read never returns.
    /// </summary>
    async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_reconnecting) return;
        _reconnecting = true;
        try
        {
            StatusText.Text = "Reconnecting...";
            if (_session == null)
            {
                await ConnectAsync();
            }
            else
            {
                await Task.Run(() => _session.Reconnect());
                _lastFrameAt = DateTime.Now;
                StatusText.Text = "Connected - live";
            }
            FooterHint.Text = "Reconnected.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Reconnect failed: " + ex.Message;
            FooterHint.Text = ex.Message;
        }
        finally { _reconnecting = false; }
    }

    bool _reconnecting;

    void TagSelected_Click(object sender, RoutedEventArgs e)
    {
        string label = LabelBox.Text.Trim();
        if (label.Length == 0) { FooterHint.Text = "Type a label first."; return; }

        var keys = FrameList.SelectedItems
            .OfType<ListBoxItem>()
            .Select(i => i.Tag as string)
            .Where(k => k != null)
            .Distinct()
            .ToList();

        if (keys.Count == 0)
        {
            FooterHint.Text = "Select one or more frames first (click, or ctrl/shift-click).";
            return;
        }

        foreach (var k in keys)
        {
            _map.RemoveAll(m => m.Key == k);
            _map.Add((k, label));
            LogTag(k, label);
        }
        LabelBox.Clear();
        RenderMap();
        FooterHint.Text = $"Tagged {keys.Count} frame(s): {label}";
    }

    void FrameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = FrameList.SelectedItems.Count;
        SelectionInfo.Text = n == 0
            ? "Click or ctrl/shift-click frames to select several, then Tag selected."
            : $"{n} frame(s) selected - type a label and press Tag selected.";
    }

    /// <summary>Label every frame that arrived since the previous Mark.</summary>
    void TagSinceMark_Click(object sender, RoutedEventArgs e)
    {
        string label = LabelBox.Text.Trim();
        if (label.Length == 0) { FooterHint.Text = "Type a label first."; return; }

        if (_all.Count <= _markIndex)
        {
            FooterHint.Text = "No new frames since the last mark.";
            return;
        }

        var keys = _all.Skip(_markIndex).Select(f => f.Key).Distinct().ToList();
        foreach (var k in keys)
        {
            _map.RemoveAll(m => m.Key == k);
            _map.Add((k, label));
            LogTag(k, label);
        }
        int n = _all.Count - _markIndex;
        _markIndex = _all.Count;
        LabelBox.Clear();
        RenderMap();
        FooterHint.Text = $"Tagged {n} frame(s) ({keys.Count} distinct) since the last mark: {label}";
    }

    void Tag_Click(object sender, RoutedEventArgs e)
    {
        if (_lastKey == null)
        {
            MapBox.Text = "No frames have arrived yet.";
            return;
        }
        string label = LabelBox.Text.Trim();
        if (label.Length == 0) return;

        _map.RemoveAll(m => m.Key == _lastKey);
        _map.Add((_lastKey, label));
        LogTag(_lastKey, label);
        LabelBox.Clear();
        RenderMap();
        FooterHint.Text = "Tagged: " + _lastKey;
    }

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_map.Count > 0)
        {
            _map.RemoveAt(_map.Count - 1);
            RenderMap();
        }
    }

    void LabelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Tag_Click(sender, e);
    }

    /// <summary>Record a tag in the log too, so the mapping can be correlated with frames.</summary>
    void LogTag(string key, string label)
    {
        AppendLog($"=== TAG [{DateTime.Now:HH:mm:ss.fff}] {label} :: {key} ===");
    }

    /// <summary>Existing label for a frame key, or null.</summary>
    string LabelOf(string key)
    {
        if (key == null) return null;
        foreach (var m in _map) if (m.Key == key) return m.Label;
        return null;
    }

    void RenderMap()
    {
        MapBox.Text = _map.Count == 0
            ? "(no labels yet)"
            : string.Join("\n\n", _map.Select(m => m.Key + "\n   -> " + m.Label));
    }

    void LoadMap()
    {
        try
        {
            if (File.Exists(MapPath))
            {
                var list = JsonSerializer.Deserialize<List<MapEntry>>(File.ReadAllText(MapPath));
                if (list != null) _map.AddRange(list.Select(e => (e.Key, e.Label)));
            }
        }
        catch { }
        RenderMap();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MapPath));
            File.WriteAllText(MapPath, JsonSerializer.Serialize(
                _map.Select(m => new MapEntry { Key = m.Key, Label = m.Label }).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
            FooterHint.Text = "Saved to " + MapPath;
        }
        catch (Exception ex) { FooterHint.Text = "Save failed: " + ex.Message; }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MapBox.Text);
            FooterHint.Text = "Mapping copied to the clipboard.";
        }
        catch (Exception ex) { FooterHint.Text = "Copy failed: " + ex.Message; }
    }

    public class MapEntry
    {
        public string Key { get; set; }
        public string Label { get; set; }
    }
}
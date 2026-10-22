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
    readonly ConcurrentQueue<string> _pending = new();
    readonly List<(string Key, string Label)> _map = new();
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    string _lastNewKey;
    string _filter = "";

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
                    if (isNew) _lastNewKey = key;
                    _pending.Enqueue(line);
                });
            }
            if (any) Dispatcher.Invoke(Flush);
            Thread.Sleep(60);
        }
    }

    void Flush()
    {
        int added = 0;
        while (_pending.TryDequeue(out var line) && added < 200)
        {
            if (_filter.Length > 0 && !line.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var item = new ListBoxItem { Content = line };
            FrameList.Items.Add(item);
            added++;
        }
        while (FrameList.Items.Count > 500) FrameList.Items.RemoveAt(0);
        if (added > 0) FrameList.ScrollIntoView(FrameList.Items[^1]);

        FrameCount.Text = FrameList.Items.Count.ToString();
        if (_lastNewKey != null)
            LastFrame.Text = "last new: " + _lastNewKey;
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
        _lastNewKey = null;
    }

    void Tag_Click(object sender, RoutedEventArgs e)
    {
        if (_lastNewKey == null)
        {
            MapBox.Text = "Nothing new to tag yet - wait for a frame marked <new>.";
            return;
        }
        string label = LabelBox.Text.Trim();
        if (label.Length == 0) return;

        _map.RemoveAll(m => m.Key == _lastNewKey);
        _map.Add((_lastNewKey, label));
        LabelBox.Clear();
        RenderMap();
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
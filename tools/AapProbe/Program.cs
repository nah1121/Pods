using Pods.Core;

// AapProbe - a protocol debugging tool.
//
//   aapprobe frames [seconds]   print every inbound frame with a timestamp
//   aapprobe watch  [seconds]   print only ear-detection / role frames
//   aapprobe battery [seconds]  print only battery frames
//   aapprobe settings [seconds] print only settings (cmd 0x09) frames
//
// Useful when working out frame layouts, or when checking whether the driver is
// actually delivering the push stream to this client.

if (args.Length == 0) { Usage(); return 1; }

string mode = args[0].ToLowerInvariant();
int seconds = args.Length > 1 ? int.Parse(args[1]) : 60;

try
{
    using var s = AapSession.Connect();
    s.Log += l => { };

    var sw = System.Diagnostics.Stopwatch.StartNew();
    s.Handshake();
    s.StartReceiving();

    Console.WriteLine($"aapprobe {mode} - listening {seconds}s from {DateTime.Now:HH:mm:ss}");
    Console.WriteLine("(move the AirPods - wear them for live data)\n");

    var seen = new HashSet<string>();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        foreach (var f in Drain())
        {
            if (!Match(mode, f)) continue;
            string hex = Hex.F(f);
            string key = f[4] + ":" + hex;
            // "watch" prints every frame so transitions are visible; the others dedupe.
            if (mode == "frames" || mode == "watch" || seen.Add(key))
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] cmd 0x{f[4]:x2} ({f.Length,3} B)  {hex}");
        }
        Thread.Sleep(60);
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

static IEnumerable<byte[]> Drain()
{
    while (AapSession.RawWatch.TryDequeue(out var f)) yield return f;
    foreach (var f in AapSession.RawSettings) { }
    AapSession.RawSettings.Clear();
    foreach (var f in AapSession.RawBattery) { }
    AapSession.RawBattery.Clear();
    yield break;
}

static bool Match(string mode, byte[] f)
{
    if (f.Length < 6) return false;
    return mode switch
    {
        "frames" => true,
        "watch" => f[4] is 0x06 or 0x08,
        "battery" => f[4] == 0x04,
        "settings" => f[4] == 0x09,
        _ => true
    };
}

static void Usage()
{
    Console.WriteLine("""
        AapProbe - AAP protocol debugger

        USAGE
          aapprobe frames   [seconds]   every inbound frame
          aapprobe watch    [seconds]   ear-detection and role frames only
          aapprobe battery  [seconds]   battery frames only
          aapprobe settings [seconds]   settings frames only
        """);
}
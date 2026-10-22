using Pods.Core;

// Pods CLI - a scriptable front-end for the same core the GUI uses.
//
//   pods list                    show discovered device interfaces
//   pods read                    open the device and dump inbound frames
//   pods set <name> <value>      apply a single setting
//   pods full <ancmode>          startup sequence then set noise control
//
// ancmode: 1=off 2=noise cancellation 3=transparency 4=adaptive

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

string cmd = args[0].ToLowerInvariant();

try
{
    switch (cmd)
    {
        case "list":
            {
                var paths = AapTransport.FindDevicePaths();
                Console.WriteLine($"MagicAAP device interfaces: {paths.Count}");
                foreach (var p in paths) Console.WriteLine("  " + p);
                return paths.Count > 0 ? 0 : 1;
            }

        case "read":
            {
                using var s = AapSession.Connect();
                s.Log += l => Console.WriteLine(l);
                s.Handshake();
                s.StartReceiving();
                int seconds = args.Length > 1 ? int.Parse(args[1]) : 60;
                Console.WriteLine($"Listening for {seconds}s...");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                return 0;
            }

        case "full":
            {
                using var s = AapSession.Connect();
                s.Log += l => Console.WriteLine(l);
                byte mode = args.Length > 1 ? byte.Parse(args[1]) : Aap.Transparency;
                s.Handshake();
                Thread.Sleep(AapSession.SettleMs);
                s.SendRaw(Aap.SetAnc(mode));
                Thread.Sleep(400);
                s.SendRaw(Aap.SetAnc(mode));
                Console.WriteLine($"Noise control -> {AapSession.AncName(mode)}");
                return 0;
            }

        case "set":
            {
                if (args.Length < 3) { PrintUsage(); return 1; }
                using var s = AapSession.Connect();
                s.Log += l => Console.WriteLine(l);
                string what = args[1].ToLowerInvariant();
                byte val = byte.Parse(args[2]);

                byte sub = 0;
                byte[] pkt = null;
                switch (what)
                {
                    case "anc": sub = 0x0d; pkt = Aap.SetAnc(val); break;
                    case "pressspeed": sub = 0x17; pkt = Aap.SetPressSpeed(val); break;
                    case "presshold": sub = 0x18; pkt = Aap.SetPressAndHoldDuration(val); break;
                    case "swipelength": sub = 0x23; pkt = Aap.SetVolumeSwipeLength(val); break;
                    case "oneswipe": sub = 0x25; pkt = Aap.SetVolumeSwipe(val == 1); break;
                    case "personalized": sub = 0x26; pkt = Aap.SetPersonalizedVolume(val == 1); break;
                    case "conversation": sub = 0x28; pkt = Aap.SetConversationAwareness(val == 1); break;
                    case "tone": sub = 0x1f; pkt = Aap.SetToneVolume(val); break;
                    case "oneairpod": sub = 0x1b; pkt = Aap.SetNoiseCancellationOneAirPod(val == 1 ? (byte)1 : (byte)2); break;
                    case "adaptive": sub = 0x2e; pkt = Aap.SetAdaptiveAudioNoise(val); break;
                }
                if (pkt == null) { Console.WriteLine($"Unknown setting '{what}'."); PrintUsage(); return 1; }

                s.Handshake();
                Thread.Sleep(AapSession.SettleMs);
                s.SendRaw(pkt);
                Thread.Sleep(400);
                s.SendRaw(pkt);
                Console.WriteLine($"{AapSession.SettingName(sub)} applied"
                    + (AapSession.IsWriteOnly(sub) ? " (write-only: the device will not echo this)" : ""));
                return 0;
            }

        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Pods - AirPods control for Windows

        USAGE
          pods list                 list MagicAAP device interfaces
          pods read [seconds]       connect and print inbound frames
          pods set <name> <value>   apply one setting
          pods full <ancmode>       handshake then set noise control

        SETTINGS
          anc            1=off 2=noise cancellation 3=transparency 4=adaptive
          pressspeed     0=default 1=slower 2=slowest
          presshold      0=default 1=shorter 2=shortest
          swipelength    0=default 1=longer 2=longest
          oneswipe       1=on 2=off
          personalized   1=on 2=off
          conversation   1=on 2=off
          tone           15-100
          oneairpod      1=on 2=off
          adaptive       0-100

        NOTE
          Close MagicPods (or any other AAP client) first - the driver hands the
          L2CAP channel to a single client at a time.
        """);
}
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;


namespace Pods.Core;

/// <summary>
/// High-level session over the MagicAAP driver.
///
/// The single most important behaviour encoded here: a setting is only accepted by the
/// AirPods when the full startup sequence has been sent on this connection first, a settle
/// delay has elapsed, and the setting is written twice. Every one of those steps was
/// established empirically - a lone setting write reports success at the WriteFile level
/// and is then silently discarded by the AirPods.
/// </summary>
public sealed class AapSession : IDisposable
{
    public const int SettleMs = 2000;

    AapTransport _t;
    public string DevicePath { get; private set; }

    public event Action<string> Log;

    void Say(string s) => Log?.Invoke(s);

    public AapSession(AapTransport t)
    {
        _t = t;
        DevicePath = t.DevicePath;
    }

    /// <summary>Publish a decoded state update to the UI.</summary>
    public void PublishState() => StateChanged?.Invoke();

    /// <summary>Open the driver, waiting for the AirPods interface to come live.</summary>
    public static AapSession Connect(CancellationToken ct = default)
    {
        var paths = AapTransport.FindDevicePaths();
        foreach (var p in paths)
        {
            try { return new AapSession(AapTransport.Open(p)); }
            catch (Win32Exception) { }
        }
        throw new InvalidOperationException(
            "MagicAAP interface unavailable. AirPods must be connected and awake. " +
            "Close MagicPods first so only one client owns the channel.");
    }

    public bool TryConnect(out string error)
    {
        try
        {
            var s = Connect();
            Dispose();
            _held = s;
            error = null;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    AapSession _held;

    /// <summary>Run the documented startup sequence. Must precede any setting change.</summary>
    public void Handshake()
    {
        Say("Initialising session...");
        foreach (var (name, pkt) in Aap.StartupSequence())
        {
            _t.Send(pkt);
            Say($"  {name} ({pkt.Length} B)");
            Thread.Sleep(350);
        }
    }

    /// <summary>
    /// Apply a setting.
    ///
    /// The driver's queue is synchronous + sequential, so a pending ReadFile owns the
    /// queue and any write issued during that window blocks forever. The receive loop
    /// must therefore be stopped (CancelSynchronousIo) before writing and restarted
    /// afterwards. This is precisely the pattern MagicPods uses - its log shows
    /// ReceiveStringLoop paired with CancelSynchronousIo.
    ///
    /// A double write is retained because the AirPods reliably accept only the second
    /// packet; a single write is often silently dropped even though WriteFile succeeds.
    /// </summary>
    public int Apply(string label, byte[] packet, byte? sub = null)
    {
        bool wasReceiving = _receiving;

        if (wasReceiving)
        {
            _t.StopReceiveLoop();
            _receiving = false;
        }
        else
        {
            // Cold session: the AirPods need the handshake and a settle window before
            // they accept a setting, and the first write is usually dropped.
            Handshake();
            Thread.Sleep(SettleMs);
            _t.Send(packet);
            Thread.Sleep(500);
        }

        try
        {
            uint n = _t.Send(packet);
            Say($"  {label} -> {n} B"
                + (sub is byte s2 && IsWriteOnly(s2)
                    ? "  (write-only: this AirPods never confirms it)" : ""));
            if (sub is byte s3) Store.Record(s3, packet.Length > 7 ? packet[7] : (byte)0);
            return (int)n;
        }
        finally
        {
            if (wasReceiving) StartReceiving();
        }
    }

    bool _receiving;

    /// <summary>Persisted user preferences.</summary>
    public SettingsStore Store { get; } = SettingsStore.Load();

    /// <summary>
    /// Re-apply everything the user previously chose. Must run on a cold session
    /// (before the receive loop starts), since it issues a batch of writes.
    /// </summary>
    public int RestoreSettings()
    {
        var packets = Store.Packets().ToList();
        if (packets.Count == 0) return 0;

        Say($"Restoring {packets.Count} saved setting(s)...");
        foreach (var (sub, pkt) in packets)
        {
            _t.Send(pkt);
            Say($"  {SettingName(sub)} -> {pkt[7]}");
            Thread.Sleep(250);
        }
        return packets.Count;
    }

    public int SetAnc(byte mode) => Apply($"ANC={AncName(mode)}", Aap.SetAnc(mode));
    public int SetConversationAwareness(bool on) => Apply($"Conversation awareness={(on ? "On" : "Off")}", Aap.SetConversationAwareness(on), 0x28);
    public int SetPressSpeed(byte mode) => Apply($"Press speed={PressSpeedName(mode)}", Aap.SetPressSpeed(mode), 0x17);
    public int SetPressAndHoldDuration(byte mode) => Apply($"Press & hold={PressHoldName(mode)}", Aap.SetPressAndHoldDuration(mode), 0x18);
    public int SetVolumeSwipe(bool on) => Apply($"Volume swipe={(on ? "On" : "Off")}", Aap.SetVolumeSwipe(on), 0x25);
    public int SetVolumeSwipeLength(byte mode) => Apply($"Volume swipe length={SwipeLengthName(mode)}", Aap.SetVolumeSwipeLength(mode), 0x23);
    public int SetPersonalizedVolume(bool on) => Apply($"Personalized volume={(on ? "On" : "Off")}", Aap.SetPersonalizedVolume(on), 0x26);
    public int SetToneVolume(byte v) => Apply($"Tone volume={v}", Aap.SetToneVolume(v), 0x1f);
    public int SetAdaptiveAudioNoise(byte pct) => Apply($"Adaptive audio noise={pct}%", Aap.SetAdaptiveAudioNoise(pct), 0x2e);
    public int SetNoiseCancellationOneAirPod(bool on) => Apply($"ANC with one AirPod={(on ? "On" : "Off")}", Aap.SetNoiseCancellationOneAirPod((byte)(on ? 1 : 2)), 0x1b);

    public static string AncName(byte m) => m switch
    {
        Aap.AncOff => "Off",
        Aap.AncOn => "Noise cancellation",
        Aap.Transparency => "Transparency",
        Aap.Adaptive => "Adaptive",
        _ => "?"
    };

    /// <summary>Write a setting without the handshake+settle+double recipe (probe use).</summary>
    public int SetAncRaw(byte mode) => (int)_t.Send(Aap.SetAnc(mode));

    /// <summary>Write an arbitrary packet once (probe use).</summary>
    public int SendRaw(byte[] packet) => (int)_t.Send(packet);

    // ---- live state, as echoed back by the AirPods -------------------------
    // MagicPods logs these through its AapAncWatcher / battery watcher, proving the
    // buds actively echo applied settings rather than only accepting writes.
    public byte? AncMode { get; private set; }
    public int? LeftBattery { get; private set; }
    public int? RightBattery { get; private set; }
    public int? CaseBattery { get; private set; }
    public string LeftCharging { get; private set; }
    public string RightCharging { get; private set; }
    public bool? LeftInEar { get; private set; }
    public bool? RightInEar { get; private set; }
    public DateTime? InEarChangedAt { get; private set; }

    /// <summary>
    /// How many buds are currently connected over the AAP channel, as reported by
    /// cmd 0x06 / cmd 0x08. This is link state, not in-ear state.
    /// </summary>
    public int? PodsConnected { get; private set; }

    /// <summary>
    /// The state of the currently active bud, from cmd 0x06 byte 6:
    /// 0 = in ear, 1 = out of ear, 2 = in case.
    /// </summary>
    public byte? BudState { get; private set; }

    /// <summary>Human name for a cmd 0x06 state code.</summary>
    public static string BudStateName(byte? v) => v switch
    {
        null => "?",
        0x00 => "in ear",
        0x01 => "out of ear",
        0x02 => "in case",
        _ => $"({v})"
    };

    /// <summary>Primary/secondary index reported by cmd 0x06.</summary>
    public byte? BudRole { get; private set; }
    public string ModelName { get; private set; }
    public string Firmware { get; private set; }
    public string Serial { get; private set; }
    public DateTime? LastFrameAt { get; private set; }
    public int FrameCount { get; private set; }

    public event Action StateChanged;

    void Raise() => StateChanged?.Invoke();

    /// <summary>
    /// Start the blocking receive loop and decode everything the AirPods push back.
    /// MUST be called only after all writes, because the driver's queue is synchronous
    /// and sequential: a pending read starves every later write.
    /// </summary>
    /// <summary>
    /// Force an immediate state refresh.
    ///
    /// The AirPods have no battery-request command - battery is purely pushed on their
    /// own ~60s schedule (verified: poking with EnableNotifications or AapInitExt does
    /// not speed it up). MagicPods has the same limitation.
    /// However, the startup sequence makes the buds dump their ENTIRE state at once,
    /// so replaying the handshake is the only way to refresh on demand.
    /// </summary>
    /// <summary>
    /// True when no inbound frame has arrived for a while. Because the driver delivers
    /// the push stream to a single reader, another client (MagicPods) taking the
    /// channel shows up as silence here - so the UI can prompt a reconnect instead of
    /// silently showing frozen data.
    /// </summary>
    public bool IsStale => LastFrameAt is DateTime f && (DateTime.Now - f).TotalSeconds > 20;

    public void RefreshState()
    {
        // Replaying the handshake on an ESTABLISHED session does NOT make the buds
        // re-dump their state - only a genuinely fresh session does. Reconnect()
        // does that, and re-enumerates the device path so it also works after the
        // AirPods have dropped off and come back.
        Reconnect();
        Say("Device state dumped.");
    }

    /// <summary>
    /// Full reconnect. Used when the AirPods drop off and come back.
    ///
    /// The device path is re-enumerated rather than reused: when the buds
    /// disconnect, Windows creates a NEW BTHENUM instance (the "...&amp;0" suffix
    /// changes), so the old path no longer exists and opening it fails.
    /// </summary>
    public void Reconnect()
    {
        Say("Reconnecting...");
        DevicePath = null;
        StopReceiving();
        Thread.Sleep(200);

        try { _t.Dispose(); } catch { }
        Thread.Sleep(400);

        // The path may have changed - look it up again.
        var paths = AapTransport.FindDevicePaths();
        string path = null;
        foreach (var p in paths)
        {
            try { _t = AapTransport.Open(p); path = p; break; }
            catch (Win32Exception) { }
        }
        if (path == null)
        {
            throw new InvalidOperationException(
                "The MagicAAP device interface is not available. The AirPods must be "
                + "connected and awake - check that the MagicAAP driver is running.");
        }
        if (path != DevicePath) Say($"Device path changed to {path}");
        DevicePath = path;

        Thread.Sleep(300);
        Handshake();
        Thread.Sleep(400);
        StartReceiving();
        Say("Reconnected.");
    }

    /// <summary>Pause the receive loop so a write can take the (synchronous) queue.</summary>
    public void StopReceiving()
    {
        if (!_receiving) return;
        _t.StopReceiveLoop();
        _receiving = false;
    }

    public void StartReceiving()
    {
        if (_receiving) return;
        _receiving = true;
        _t.StartReceiveLoop(
            frame =>
            {
                FrameCount++;
                LastFrameAt = DateTime.Now;
                // A decode fault must never kill the loop: one malformed frame would
                // otherwise silently end all live updates.
                try
                {
                    Apply(frame);
                    var decoded = Describe(frame).ToList();
                    Say("Rx: " + string.Join(" | ", decoded));
                    Raise();
                }
                catch (Exception ex) { Say("  decode skipped: " + ex.Message); }
            },
            ex => Say("Receive loop stopped: " + ex.Message));
    }

    /// <summary>Fold a frame into the live state.</summary>
    void Apply(byte[] d)
    {
        if (d == null || d.Length < 6) return;

        if (CaptureAllFrames) RawFrames.Enqueue(d);

        if (d[4] == 0x04 && d.Length >= 11)
        {
            RawBattery.Add(d);
            LastBatteryAt = DateTime.Now;

            foreach (var b in AapDecoder.ParseBattery(d))
            {
                switch (b.Side)
                {
                    case "Single":
                        LeftBattery = RightBattery = b.Level;
                        LeftCharging = RightCharging = b.Status;
                        LeftStatus = RightStatus = b.Status;
                        break;
                    case "Right": RightBattery = b.Level; RightCharging = b.Status; RightStatus = b.Status; break;
                    case "Left": LeftBattery = b.Level; LeftCharging = b.Status; LeftStatus = b.Status; break;
                    case "Case": CaseBattery = b.Level; CaseCharging = b.Status; CaseStatus = b.Status; break;
                }
            }
        }
        else if (d[4] == 0x09 && d.Length >= 8)
        {
            RawSettings.Add(d);
            // The AirPods dump their entire settings state at session start and echo
            // each change, so this is authoritative device state, not just an ack.
            switch (d[6])
            {
                case 0x0d: AncMode = d[7]; break;
                case 0x17: PressSpeed = d[7]; break;
                case 0x18: PressHold = d[7]; break;
                case 0x1b: OneAirPod = d[7]; break;
                case 0x1f: ToneVolume = d[7]; break;
                case 0x25: VolumeSwipe = d[7]; break;
                case 0x23: VolumeSwipeLength = d[7]; break;
                case 0x26: PersonalizedVolume = d[7]; break;
                case 0x28: ConversationAwareness = d[7]; break;
                case 0x2e: AdaptiveNoise = d.Length > 8 ? d[8] : (byte?)null; break;
            }
        }
        else if (d[4] == 0x1d && d.Length > 14)
        {
            // The string table is followed by binary data which can itself contain
            // printable runs, so each field is shape-checked before it is accepted.
            var id = AapDecoder.ParseIdentity(d);
            if (id.ProductName != null) ModelName = id.ProductName;
            if (id.Model != null) ModelId = id.Model;
            if (id.Manufacturer != null) Manufacturer = id.Manufacturer;
            if (id.Serial != null) Serial = id.Serial;
            if (id.Firmware != null) Firmware = id.Firmware;
        }
        else if (d[4] is 0x06 or 0x08 or 0x0c or 0x0e)
        {
            RawWatch.Enqueue(d);
        }

if (d[4] == 0x08)
        {
            int n = AapDecoder.ParseConnectedFrom08(d);
            if (n >= 0) PodsConnected = n;
        }

        if (d[4] == 0x06)
        {
            // Bud link state: byte 7 counts the buds connected to the host.
            var buds = AapDecoder.ParseBudState(d);
            PodsConnected = buds.Connected;
            BudRole = buds.State;
            BudState = buds.State;
            InEarChangedAt = DateTime.Now;
        }
    }

    public string ModelId { get; private set; }
    public string Manufacturer { get; private set; }

    // Device-reported settings (authoritative, pushed by the AirPods at session start
    // and again on every change).
    public byte? PressSpeed { get; private set; }
    public byte? PressHold { get; private set; }
    public byte? OneAirPod { get; private set; }
    public byte? ToneVolume { get; private set; }
    public byte? VolumeSwipe { get; private set; }
    public byte? VolumeSwipeLength { get; private set; }
    public byte? PersonalizedVolume { get; private set; }
    public byte? ConversationAwareness { get; private set; }
    public byte? AdaptiveNoise { get; private set; }

    /// <summary>Per-side charging status from the device, including "disconnected".</summary>
    public string CaseCharging { get; private set; }
    public string LeftStatus { get; private set; }
    public string RightStatus { get; private set; }
    public string CaseStatus { get; private set; }

    /// <summary>
    /// Seconds since a battery frame last arrived. The AirPods stop reporting entirely
    /// when a pod sits in a CLOSED case - the case is radio-transparent and the pod
    /// talks to the phone only via the buds themselves - so an old reading must be
    /// shown as stale rather than presented as current truth.
    /// </summary>
    public int SecondsSinceBattery =>
        LastBatteryAt is DateTime t ? (int)(DateTime.Now - t).TotalSeconds : int.MaxValue;

    public DateTime? LastBatteryAt { get; private set; }

    /// <summary>Raw battery frames, for offline protocol analysis.</summary>
    public static List<byte[]> RawBattery { get; } = new();

    /// <summary>Raw cmd 0x09 settings frames, for offline echo analysis.</summary>
    public static List<byte[]> RawSettings { get; } = new();

    /// <summary>Raw ear-detection / role frames (cmd 0x06, 0x08, 0x0c, 0x0e).</summary>
    public static ConcurrentQueue<byte[]> RawWatch { get; } = new();

    /// <summary>
    /// Every inbound frame, in arrival order. Used by the live frame monitor so
    /// unknown codes can be inspected and labelled.
    /// </summary>
    public static ConcurrentQueue<byte[]> RawFrames { get; } = new();

    /// <summary>Frames that are only useful for debugging - not captured by default.</summary>
    public static bool CaptureAllFrames { get; set; }

    public static string PressSpeedName(byte? v) => v switch
    { null => "?", 0 => "Default", 1 => "Slower", 2 => "Slowest", _ => $"({v})" };
    public static string PressHoldName(byte? v) => v switch
    { null => "?", 0 => "Default", 1 => "Shorter", 2 => "Shortest", _ => $"({v})" };
    public static string SwipeLengthName(byte? v) => v switch
    { null => "?", 0 => "Default", 1 => "Longer", 2 => "Longest", _ => $"({v})" };
    public static string OnOff(byte? v) => v switch
    { null => "?", 1 => "On", 2 => "Off", _ => $"({v})" };

    /// <summary>
    /// Settings the AirPods report back in their state dump / on change.
    /// Verified by capturing raw cmd 0x09 frames across 12s windows per setting.
    /// Everything else is WRITE-ONLY: the packets are correct (byte-identical to
    /// MagicPodsCore) but the hardware never echoes them, which the MagicPods dev
    /// documents as "Manually notify because AirPods will not send a notification
    /// that the option has been changed."
    /// </summary>
    public static readonly HashSet<byte> ReadbackSettings =
        new() { 0x0d, 0x17, 0x18, 0x1b, 0x1f, 0x24 };

    public static bool IsWriteOnly(byte sub) => !ReadbackSettings.Contains(sub);

    /// <summary>Human label for a setting sub-code.</summary>
    public static string SettingName(byte sub) => sub switch
    {
        0x0d => "Noise control", 0x17 => "Press speed", 0x18 => "Press & hold",
        0x1b => "ANC with one AirPod", 0x1f => "Tone volume", 0x24 => "End call",
        0x25 => "Volume swipe", 0x23 => "Volume swipe length",
        0x26 => "Personalized volume", 0x28 => "Conversation awareness",
        0x2e => "Adaptive audio noise", _ => $"0x{sub:x2}"
    };

    /// <summary>
    /// Decode an inbound frame. Battery (cmd 0x04) follows AapBatteryWatcher::ProcessResponse:
    /// block count at byte 6, then 5-byte blocks from offset 7 where [0]=type, [2]=level,
    /// [3]=charging status. Settings (cmd 0x09) echo the applied value at byte 7.
    /// </summary>
    public static IEnumerable<string> Describe(byte[] d)
    {
        if (d == null || d.Length < 6) yield break;

        if (d[4] == 0x04 && d.Length >= 11)
        {
            int count = d[6], start = 7;
            for (int i = 0; i < count && start + 3 < d.Length; i++)
            {
                string t = d[start] switch { 0x01 => "Single", 0x02 => "Right", 0x04 => "Left", 0x08 => "Case", _ => "?" };
                string s = d[start + 3] switch { 0 => "undef", 1 => "charging", 2 => "notCharging", 4 => "disconnected", _ => "?" };
                yield return $"battery {t}={d[start + 2]}%({s})";
                start += 5;
            }
        }
        else if (d[4] == 0x09 && d.Length >= 8)
        {
            string label = d[6] switch
            {
                0x0d => "ANC", 0x17 => "PressSpeed", 0x18 => "PressHold", 0x1b => "OneAirPod",
                0x1f => "ToneVolume", 0x23 => "SwipeLength", 0x24 => "EndCall", 0x25 => "VolumeSwipe",
                0x26 => "PersonalizedVolume", 0x28 => "ConversationAwareness", 0x2e => "AdaptiveNoise",
                _ => "setting"
            };
            string val = d[6] switch
            {
                0x0d => AncName(d[7]), _ => d[7].ToString()
            };
            yield return $"{label}={val}";
        }
        else if (d[4] == 0x1d) yield return "device identity";
        else if (d[4] == 0x17) yield return "firmware info";
        else if (d[4] == 0x06)
        {
            var buds = AapDecoder.ParseBudState(d);
            yield return $"bud state state={buds.State} connected={buds.Connected}";
        }
        else if (d[4] == 0x08)
        {
            int n = AapDecoder.ParseConnectedFrom08(d);
            yield return n >= 0 ? $"bud setup connected={n}" : "bud setup";
        }
        else yield return $"cmd 0x{d[4]:x2} ({d.Length} B)";
    }

    public void Dispose()
    {
        try { _t?.StopReceiveLoop(1000); } catch { }
        _receiving = false;
        _held?.Dispose();
        _held = null;
        _t.Dispose();
    }
}
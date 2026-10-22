using System.IO;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pods.Core;

/// <summary>
/// Persists the settings the user has chosen, mirroring what MagicPods calls
/// ApplicationSettings. Only the values the AirPods report back are stored, because for
/// the write-only settings we cannot read the device's current state and would
/// otherwise be restoring a guess.
/// </summary>
public sealed class SettingsStore
{
    public byte? Anc { get; set; }
    public byte? PressSpeed { get; set; }
    public byte? PressHold { get; set; }
    public byte? OneAirPod { get; set; }
    public byte? ToneVolume { get; set; }
    public byte? ConversationAwareness { get; set; }
    public byte? VolumeSwipe { get; set; }
    public byte? VolumeSwipeLength { get; set; }
    public byte? PersonalizedVolume { get; set; }
    public byte? AdaptiveNoise { get; set; }

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Path_ =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PodsClone", "settings.json");

    public static SettingsStore Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<SettingsStore>(File.ReadAllText(Path_)) ?? new();
        }
        catch { /* fall back to defaults */ }
        return new SettingsStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_));
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Record a setting the user just applied, keyed by its wire sub-code.</summary>
    public void Record(byte sub, byte value)
    {
        switch (sub)
        {
            case 0x0d: Anc = value; break;
            case 0x17: PressSpeed = value; break;
            case 0x18: PressHold = value; break;
            case 0x1b: OneAirPod = value; break;
            case 0x1f: ToneVolume = value; break;
            case 0x28: ConversationAwareness = value; break;
            case 0x25: VolumeSwipe = value; break;
            case 0x23: VolumeSwipeLength = value; break;
            case 0x26: PersonalizedVolume = value; break;
            case 0x2e: AdaptiveNoise = value; break;
        }
        Save();
    }

    public byte? Get(byte sub) => sub switch
    {
        0x0d => Anc, 0x17 => PressSpeed, 0x18 => PressHold, 0x1b => OneAirPod,
        0x1f => ToneVolume, 0x28 => ConversationAwareness, 0x25 => VolumeSwipe,
        0x23 => VolumeSwipeLength, 0x26 => PersonalizedVolume, 0x2e => AdaptiveNoise,
        _ => null
    };

    /// <summary>Packets for every stored setting, in a sensible order.</summary>
    public IEnumerable<(byte Sub, byte[] Packet)> Packets()
    {
        if (Anc is byte a) yield return (0x0d, Aap.SetAnc(a));
        if (PressSpeed is byte ps) yield return (0x17, Aap.SetPressSpeed(ps));
        if (PressHold is byte ph) yield return (0x18, Aap.SetPressAndHoldDuration(ph));
        if (OneAirPod is byte oa) yield return (0x1b, Aap.SetNoiseCancellationOneAirPod(oa));
        if (VolumeSwipe is byte vs) yield return (0x25, Aap.SetVolumeSwipe(vs == 1));
        if (VolumeSwipeLength is byte vl) yield return (0x23, Aap.SetVolumeSwipeLength(vl));
        if (PersonalizedVolume is byte pv) yield return (0x26, Aap.SetPersonalizedVolume(pv == 1));
        if (ConversationAwareness is byte ca) yield return (0x28, Aap.SetConversationAwareness(ca == 1));
        if (AdaptiveNoise is byte an) yield return (0x2e, Aap.SetAdaptiveAudioNoise(an));
        if (ToneVolume is byte tv) yield return (0x1f, Aap.SetToneVolume(tv));
    }
}
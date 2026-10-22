using System.Text;

namespace Pods.Core;

/// <summary>
/// Pure decoding of the frames the AirPods send. Kept free of any device access so it
/// can be unit tested against captured byte sequences.
/// </summary>
public static class AapDecoder
{
    /// <summary>Identity fields from a cmd 0x1d frame.</summary>
    public readonly record struct Identity(
        string ProductName, string Model, string Manufacturer, string Serial, string Firmware);

    /// <summary>One battery block from a cmd 0x04 frame.</summary>
    public readonly record struct BatteryBlock(
        string Side, int? Level, string Status);

    /// <summary>Identity strings, in the order the AirPods report them.</summary>
    public static readonly string[] IdentityOrder =
        { "product", "model", "manufacturer", "serial", "firmware", "build", "version", "app id" };

    /// <summary>
    /// Parse the identity string table from a cmd 0x1d frame.
    ///
    /// Layout (verified against captured frames):
    ///   [0..3] envelope   [4]=0x1d   [5..10] header   [11..] NULL-TERMINATED strings
    ///
    /// The strings are null-terminated, not length-prefixed, and binary data follows
    /// them - so parsing stops at the first byte that is not printable ASCII. Each
    /// field is then shape-checked, because binary data can contain printable runs that
    /// would otherwise be mistaken for text.
    /// </summary>
    public static Identity ParseIdentity(byte[] d)
    {
        var s = ParseStrings(d);
        string Get(int i) => i < s.Count ? s[i] : null;

        return new Identity(
            IsWords(Get(0)) ? Get(0) : null,
            IsAlnum(Get(1), 12) ? Get(1) : null,
            IsWords(Get(2)) ? Get(2) : null,
            IsAlnum(Get(3), 24) ? Get(3) : null,
            IsVersion(Get(4)) ? Get(4) : null);
    }

    /// <summary>Split the null-terminated string run that starts at offset 11.</summary>
    public static List<string> ParseStrings(byte[] d, int start = 11)
    {
        var result = new List<string>();
        if (d == null) return result;

        int p = start;
        while (p < d.Length)
        {
            int end = p;
            while (end < d.Length && d[end] != 0) end++;
            if (end >= d.Length) break;

            if (end == p) { p++; continue; }               // empty string, skip

            bool printable = true;
            for (int i = p; i < end; i++)
                if (d[i] < 0x20 || d[i] > 0x7E) { printable = false; break; }
            if (!printable) break;                          // binary data starts here

            result.Add(Encoding.ASCII.GetString(d, p, end - p));
            p = end + 1;
        }
        return result;
    }

    /// <summary>Letters and spaces only - product names and manufacturers.</summary>
    public static bool IsWords(string s) =>
        !string.IsNullOrEmpty(s) && s.Length is >= 3 and <= 32
        && s.All(c => char.IsLetter(c) || c == ' ' || c == '.');

    /// <summary>Alphanumeric identifier, e.g. a model or serial number.</summary>
    public static bool IsAlnum(string s, int max) =>
        !string.IsNullOrEmpty(s) && s.Length >= 3 && s.Length <= max
        && s.All(c => char.IsLetterOrDigit(c));

    /// <summary>Dotted numeric version, e.g. "51.9.6". Each part is short.</summary>
    public static bool IsVersion(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var parts = s.Split('.');
        return parts.Length is >= 2 and <= 4
            && parts.All(p => p.Length > 0 && p.Length <= 3 && p.All(char.IsDigit));
    }

    /// <summary>
    /// Parse a battery frame (cmd 0x04).
    ///
    ///   [6] block count, then that many 5-byte blocks:
    ///   [0] side, [1] unused, [2] level, [3] charging status, [4] unused
    /// </summary>
    public static List<BatteryBlock> ParseBattery(byte[] d)
    {
        var result = new List<BatteryBlock>();
        if (d == null || d.Length < 11 || d[4] != 0x04) return result;

        int count = d[6], start = 7;
        for (int i = 0; i < count && start + 3 < d.Length; i++)
        {
            string side = d[start] switch
            {
                0x01 => "Single", 0x02 => "Right", 0x04 => "Left", 0x08 => "Case", _ => "Unknown"
            };
            string status = ChargingStatus(d[start + 3]);

            // A level above 100 is a no-reading sentinel on clone hardware, not a
            // percentage - see docs/PROTOCOL.md.
            int? level = d[start + 2] > 100 ? null : d[start + 2];

            result.Add(new BatteryBlock(side, level, status));
            start += 5;
        }
        return result;
    }

    /// <summary>Human name for a charging-status byte.</summary>
    public static string ChargingStatus(byte v) => v switch
    {
        0x00 => "undefined",
        0x01 => "charging",
        0x02 => "not charging",
        0x04 => "disconnected",
        _ => "unknown"
    };

    /// <summary>Bud link state reported by cmd 0x06 / cmd 0x08.</summary>
    /// <param name="Role">Byte 6 of cmd 0x06 - 1 or 2, a primary/secondary index.</param>
    /// <param name="Connected">How many buds are connected to the host.</param>
    public readonly record struct BudState(byte State, int Connected);

    /// <summary>
    /// Parse the bud link state from a cmd 0x06 frame.
    ///
    ///   04 00 04 00 06 00 &lt;role&gt; &lt;connected&gt;
    ///
    /// Verified by capturing three physical states in sequence:
    ///
    ///   both buds in the closed case  ->  06 00 01 01   (1 connected)
    ///   one bud taken out of the case ->  06 00 01 02   (2 connected)
    ///   both buds in the ears         ->  06 00 02 02   (2 connected)
    ///
    /// So byte 7 counts the buds connected to the host. This is link state - it is
    /// NOT in-ear detection. See docs/PROTOCOL.md.
    /// </summary>
    public static BudState ParseBudState(byte[] d)
    {
        if (d == null || d.Length < 6 || d[4] != 0x06) return default;
        byte state = d.Length >= 7 ? d[6] : (byte)0;
        int connected = d.Length >= 8 ? d[7] : 0;
        return new BudState(state, connected);
    }

    /// <summary>
    /// Parse the bud link state from a cmd 0x08 frame:
    ///   04 00 04 00 08 00 &lt;connected&gt; ...
    /// Byte 6 counts the connected buds (observed 0x01 with one, 0x02 with both).
    /// </summary>
    public static int ParseConnectedFrom08(byte[] d)
    {
        if (d == null || d.Length < 7 || d[4] != 0x08) return -1;
        return d[6];
    }

    /// <summary>Parse a settings frame (cmd 0x09): the sub-command and its value.</summary>
    public static (byte Sub, byte Value)? ParseSetting(byte[] d)
    {
        if (d == null || d.Length < 8 || d[4] != 0x09) return null;
        return (d[6], d[7]);
    }
}
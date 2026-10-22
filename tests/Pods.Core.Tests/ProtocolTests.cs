using Pods.Core;
using Xunit;

namespace Pods.Core.Tests;

/// <summary>
/// Tests for the pure protocol logic: packet layouts and frame decoding.
/// Every expectation here was verified against captured frames or against
/// MagicPodsCore's reference implementations.
/// </summary>
public class ProtocolTests
{
    static byte[] Hex(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
         .Select(x => Convert.ToByte(x, 16)).ToArray();

    static string AsHex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("x2")));

    // ---------------------------------------------------------------- packets

    [Fact]
    public void AapInit_MatchesReferenceLayout()
    {
        // MagicPodsCore: AapInit::Request()
        Assert.Equal("00 00 04 00 01 00 02 00 00 00 00 00 00 00 00 00", AsHex(Aap.Init));
    }

    [Fact]
    public void InitExt_MatchesReferenceLayout()
    {
        Assert.Equal("04 00 04 00 4d 00 0e 00 00 00 00 00 00 00", AsHex(Aap.InitExt));
    }

    [Fact]
    public void PrivateKeys_MatchesReferenceLayout()
    {
        Assert.Equal("04 00 04 00 30 00 05 00", AsHex(Aap.PrivateKeys));
    }

    [Theory]
    [InlineData(0xef, "04 00 04 00 0f 00 ff ff ef ff")]
    [InlineData(0xff, "04 00 04 00 0f 00 ff ff ff ff")]
    public void EnableNotifications_MatchesReferenceLayout(byte mode, string expected)
    {
        Assert.Equal(expected, AsHex(Aap.EnableNotifications(mode)));
    }

    [Theory]
    [InlineData(1, "04 00 04 00 09 00 0d 01 00 00 00")]
    [InlineData(2, "04 00 04 00 09 00 0d 02 00 00 00")]
    [InlineData(3, "04 00 04 00 09 00 0d 03 00 00 00")]
    [InlineData(4, "04 00 04 00 09 00 0d 04 00 00 00")]
    public void SetAnc_MatchesReferenceLayout(byte mode, string expected)
    {
        Assert.Equal(expected, AsHex(Aap.SetAnc(mode)));
    }

    [Fact]
    public void SetToneVolume_MatchesReferenceLayout()
    {
        // { 04 00 04 00 09 00 1f <mode> 00 00 00 }
        Assert.Equal("04 00 04 00 09 00 1f 50 00 00 00", AsHex(Aap.SetToneVolume(80)));
    }

    [Fact]
    public void SetEndCall_CarriesTwoValues()
    {
        // 04 00 04 00 09 00 24 21 <mute> <call> 00
        Assert.Equal("04 00 04 00 09 00 24 21 01 02 00", AsHex(Aap.SetEndCall(1, 2)));
    }

    [Fact]
    public void EverySettingUsesTheSameEnvelope()
    {
        foreach (var pkt in new[]
        {
            Aap.SetAnc(1), Aap.SetPressSpeed(1), Aap.SetPressAndHoldDuration(1),
            Aap.SetVolumeSwipe(true), Aap.SetVolumeSwipeLength(1),
            Aap.SetPersonalizedVolume(true), Aap.SetConversationAwareness(true),
            Aap.SetToneVolume(50), Aap.SetAdaptiveAudioNoise(50),
            Aap.SetNoiseCancellationOneAirPod(1),
        })
        {
            Assert.Equal(0x04, pkt[0]);
            Assert.Equal(0x00, pkt[1]);
            Assert.Equal(0x04, pkt[2]);
            Assert.Equal(0x00, pkt[3]);
            Assert.Equal(0x09, pkt[4]);   // settings command
            Assert.Equal(0x00, pkt[5]);
            Assert.Equal(11, pkt.Length);
        }
    }

    // ---------------------------------------------------------------- battery

    [Fact]
    public void ParseBattery_ReadsBothBudsAndTheCase()
    {
        // captured: Right 71%, Left 78%, Case disconnected
        var frame = Hex("04 00 04 00 04 00 03 02 01 47 02 01 04 01 4e 02 01 08 01 00 04 01");
        var blocks = AapDecoder.ParseBattery(frame);

        Assert.Equal(3, blocks.Count);
        Assert.Equal("Right", blocks[0].Side);
        Assert.Equal(71, blocks[0].Level);
        Assert.Equal("not charging", blocks[0].Status);
        Assert.Equal("Left", blocks[1].Side);
        Assert.Equal(78, blocks[1].Level);
        Assert.Equal("Case", blocks[2].Side);
        Assert.Equal("disconnected", blocks[2].Status);
    }

    [Fact]
    public void ParseBattery_TreatsAbove100AsNoReading()
    {
        // 0xFF is a sentinel on clone hardware, not a percentage.
        var frame = Hex("04 00 04 00 04 00 03 04 01 46 02 01 02 01 ff 03 01 08 01 00 04 01");
        var blocks = AapDecoder.ParseBattery(frame);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(70, blocks[0].Level);      // left is fine
        Assert.Null(blocks[1].Level);           // right is unreadable (0xFF)
        Assert.Equal(0, blocks[2].Level);        // case genuinely reports 0
        Assert.Equal("disconnected", blocks[2].Status);
    }

    [Theory]
    [InlineData(0x00, "undefined")]
    [InlineData(0x01, "charging")]
    [InlineData(0x02, "not charging")]
    [InlineData(0x04, "disconnected")]
    public void ChargingStatus_IsMapped(byte raw, string expected)
    {
        Assert.Equal(expected, AapDecoder.ChargingStatus(raw));
    }

    // ---------------------------------------------------------------- identity

    [Fact]
    public void ParseIdentity_ReadsTheStringTable()
    {
        // captured cmd 0x1d frame
        var frame = Hex(
            "04 00 04 00 1d 00 02 c1 00 0d 00 " +
            "41 69 72 50 6f 64 73 20 50 72 6f 00 " +   // "AirPods Pro"
            "41 32 30 38 34 00 " +                        // "A2084"
            "41 70 70 6c 65 20 49 6e 63 2e 00 " +        // "Apple Inc."
            "47 58 35 5a 4b 33 54 39 4c 4b 4b 54 00 " +  // "GX5ZK3T9LKKT"
            "35 31 2e 39 2e 36 00 " +                     // "51.9.6"
            "35 31 2e 39 2e 36 00 " +                     // "51.9.6"
            "31 2e 30 2e 30 00 " +                        // "1.0.0"
            "63 6f 6d 2e 61 70 70 6c 65 2e 61 63 63 65 73 73 6f 72 79 2e 75 70 64 61 74 65 2e 61 70 70 2e 36 30 2d 41 4e 43 00");

        var id = AapDecoder.ParseIdentity(frame);

        Assert.Equal("AirPods Pro", id.ProductName);
        Assert.Equal("A2084", id.Model);
        Assert.Equal("Apple Inc.", id.Manufacturer);
        Assert.Equal("GX5ZK3T9LKKT", id.Serial);
        Assert.Equal("51.9.6", id.Firmware);
    }

    [Fact]
    public void ParseIdentity_StopsAtBinaryData()
    {
        // Same frame but followed by binary that contains printable runs.
        var frame = Hex(
            "04 00 04 00 1d 00 02 c1 00 0d 00 " +
            "41 69 72 50 6f 64 73 20 50 72 6f 00 " +   // "AirPods Pro"
            "41 32 30 38 34 00 " +                        // "A2084"
            "00 03 81 12 0c 7f b1 d2 d7 bc ea 71 00");    // binary blob

        var id = AapDecoder.ParseIdentity(frame);

        Assert.Equal("AirPods Pro", id.ProductName);
        Assert.Equal("A2084", id.Model);
        Assert.Null(id.Serial);
    }

    [Theory]
    [InlineData("51.9.6", true)]
    [InlineData("1.0.0", true)]
    [InlineData("65.1868046005000000.52", false)]   // binary, not a version
    [InlineData("51", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVersion_RejectsNonVersions(string s, bool expected)
    {
        Assert.Equal(expected, AapDecoder.IsVersion(s));
    }

    [Theory]
    [InlineData("A2084", true)]
    [InlineData("GX5ZK3T9LKKT", true)]
    [InlineData("AirPods Pro", false)]
    [InlineData("ab", false)]
    public void IsAlnum_AcceptsIdentifiersOnly(string s, bool expected)
    {
        Assert.Equal(expected, AapDecoder.IsAlnum(s, 24));
    }

    // ---------------------------------------------------------------- ear / settings

    [Fact]
    public void ParseEar_ReadsTheBitmask()
    {
        // Captured frames are 8 bytes; byte 7 is a bitmask.
        var none = AapDecoder.ParseEar(Hex("04 00 04 00 06 00 01 00"));
        Assert.False(none.LeftIn);
        Assert.False(none.RightIn);
        Assert.Equal(1, none.Role);

        var one = AapDecoder.ParseEar(Hex("04 00 04 00 06 00 00 01"));
        Assert.True(one.LeftIn);
        Assert.False(one.RightIn);
        Assert.Equal(0, one.Role);

        var both = AapDecoder.ParseEar(Hex("04 00 04 00 06 00 00 03"));
        Assert.True(both.LeftIn);
        Assert.True(both.RightIn);
        Assert.Equal(3, both.Mask);
    }

    [Fact]
    public void ParseEar_HandlesShortFrames()
    {
        var ear = AapDecoder.ParseEar(Hex("04 00 04 00 06 00"));
        Assert.Equal(0, ear.Role);
        Assert.Equal(0, ear.Mask);
        Assert.False(ear.LeftIn);
        Assert.False(ear.RightIn);
    }

    [Fact]
    public void ParseSetting_ReadsSubAndValue()
    {
        var s = AapDecoder.ParseSetting(Hex("04 00 04 00 09 00 0d 01 00 00 00"));
        Assert.NotNull(s);
        Assert.Equal(0x0d, s.Value.Sub);
        Assert.Equal(0x01, s.Value.Value);
    }

    // ---------------------------------------------------------------- readback

    [Theory]
    [InlineData(0x0d, false)]
    [InlineData(0x17, false)]
    [InlineData(0x18, false)]
    [InlineData(0x1b, false)]
    [InlineData(0x1f, false)]
    [InlineData(0x24, false)]
    [InlineData(0x23, true)]
    [InlineData(0x25, true)]
    [InlineData(0x26, true)]
    [InlineData(0x28, true)]
    [InlineData(0x2e, true)]
    public void ReadbackSettings_MatchesMeasuredBehaviour(byte sub, bool writeOnly)
    {
        // Measured over 12s windows per setting: only these six appear in the
        // AirPods' startup state dump. The rest are never echoed.
        Assert.Equal(writeOnly, AapSession.IsWriteOnly(sub));
    }
}
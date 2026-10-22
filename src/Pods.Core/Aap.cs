using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pods.Core;

    // =====================================================================
    //  AAP packet builders.
    //  Recovered from MagicPodsCore (the MagicPods dev's own open-source core)
    //  and cross-checked against AAP_PROTOCOL.md.
    //
    //  Transport envelope (little-endian):
    //    [0]=0x04 [1]=0x00 [2]=0x04 [3]=0x00 [4]=cmd [5]=0x00 [6]=sub [7..]=args
    // =====================================================================
    public static class Aap
    {
        public const byte AncOff = 0x01, AncOn = 0x02, Transparency = 0x03, Adaptive = 0x04;

        public static byte[] Init =>
            new byte[] { 0x00,0x00,0x04,0x00,0x01,0x00,0x02,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00 };

        public static byte[] InitExt =>
            new byte[] { 0x04,0x00,0x04,0x00,0x4d,0x00,0x0e,0x00,0x00,0x00,0x00,0x00,0x00,0x00 };

        public static byte[] PrivateKeys =>
            new byte[] { 0x04,0x00,0x04,0x00,0x30,0x00,0x05,0x00 };

        public static byte[] EnableNotifications(byte mode) =>
            new byte[] { 0x04,0x00,0x04,0x00,0x0f,0x00,0xff,0xff,mode,0xff };

        // Generic settings setter: cmd 0x09, sub at byte6, value at byte7.
        public static byte[] SetSetting(byte sub, byte value) =>
            new byte[] { 0x04,0x00,0x04,0x00,0x09,0x00,sub,value,0x00,0x00,0x00 };

        public static byte[] SetAnc(byte mode) => SetSetting(0x0d, mode);
        public static byte[] SetConversationAwareness(bool on) => SetSetting(0x28, (byte)(on ? 1 : 2));
        public static byte[] SetNoiseCancellationOneAirPod(byte mode) => SetSetting(0x1b, mode);
        public static byte[] SetPressAndHoldDuration(byte mode) => SetSetting(0x18, mode);
        public static byte[] SetPressSpeed(byte mode) => SetSetting(0x17, mode);
        public static byte[] SetVolumeSwipe(bool on) => SetSetting(0x25, (byte)(on ? 1 : 2));
        public static byte[] SetVolumeSwipeLength(byte mode) => SetSetting(0x23, mode);
        public static byte[] SetPersonalizedVolume(bool on) => SetSetting(0x26, (byte)(on ? 1 : 2));
        public static byte[] SetToneVolume(byte v) => SetSetting(0x1f, v);
        public static byte[] SetAdaptiveAudioNoise(byte pct) => SetSetting(0x2e, pct);
        public static byte[] SetEndCall(byte mute, byte call) =>
            new byte[] { 0x04,0x00,0x04,0x00,0x09,0x00,0x24,0x21,mute,call,0x00 };

        /// <summary>Full documented startup sequence from MagicPodsCore.</summary>
        public static IEnumerable<(string Name, byte[] Pkt)> StartupSequence()
        {
            yield return ("AapInit",                 Init);
            yield return ("EnableNotifications(ef)", EnableNotifications(0xef));
            yield return ("EnableNotifications(ff)", EnableNotifications(0xff));
            yield return ("AapInitExt",              InitExt);
            yield return ("AapPrivateKeys",          PrivateKeys);
        }
    }

using System.ComponentModel;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pods.Core;

    // =====================================================================
    //  Win32 transport to the MagicAAP kernel driver.
    //  Semantics recovered verbatim from the decompiled MagicPodsService:
    //    CreateFile(path, 0xC0000000, 3, NULL, OPEN_EXISTING, 0, NULL)
    //    WriteFile(h, data, data.Length, out written, NULL)
    //    ReadFile (h, new byte[4096], 4096, out read, NULL)  -> exact-size copy
    // =====================================================================
    public sealed class AapTransport : IDisposable
    {
        // NOTE: must be lowercase. The registry key lookup is case-insensitive, but the
        // NT device-interface path is not - an uppercase GUID suffix fails CreateFile.
        public const string InterfaceGuid = "9eec98bb-3c54-45d4-a843-7900c4635e08";
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint SHARE_READ = 1, SHARE_WRITE = 2;
        const uint OPEN_EXISTING = 3;
        /// <summary>Required for ReadFile/WriteFile to be truly asynchronous.</summary>
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        const int ReadBufferSize = 4096;

        SafeFileHandle _h;
        public string DevicePath { get; }
        public bool IsOpen => _h != null && !_h.IsInvalid && !_h.IsClosed;

        AapTransport(string path, SafeFileHandle h) { DevicePath = path; _h = h; }

        // ---- SetupAPI interface discovery -------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_INFO
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr w, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr di, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_INFO info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_INFO info, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        /// <summary>
        /// Enumerate device-interface paths for the MagicAAP GUID.
        /// SetupAPI is tried first, but on this box it returns nothing even though the
        /// interface is live, so the registry (Control\DeviceClasses) is used as the
        /// authoritative fallback. Registry subkey names encode the NT path with '#'
        /// in place of '\' and a leading '#' stripping the "\\?\" prefix.
        /// </summary>
        public static List<string> FindDevicePaths()
        {
            var result = new List<string>();
            foreach (var p in FindDevicePathsRegistry()) if (!result.Contains(p)) result.Add(p);
            foreach (bool presentOnly in new[] { false, true })
            {
                var g = new Guid(InterfaceGuid);
                uint flags = 0x10 /*DICF_DEVICEINTERFACE*/ | (presentOnly ? 0x2u : 0u);
                IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, flags);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) continue;
                try
                {
                    for (uint i = 0; ; i++)
                    {
                        var info = new SP_DEVICE_INTERFACE_INFO { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_INFO>() };
                        if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref info)) break;

                        uint required = 0;
                        SetupDiGetDeviceInterfaceDetail(set, ref info, IntPtr.Zero, 0, out required, IntPtr.Zero);
                        if (required == 0) continue;

                        IntPtr buf = Marshal.AllocHGlobal((int)required);
                        try
                        {
                            if (!SetupDiGetDeviceInterfaceDetail(set, ref info, buf, required, out required, IntPtr.Zero)) continue;
                            // On 64-bit the path WCHAR begins at offset 8; on 32-bit at offset 6.
                            int offset = IntPtr.Size == 8 ? 8 : 6;
                            string path = Marshal.PtrToStringUni(buf + offset);
                            if (!string.IsNullOrEmpty(path) && !result.Contains(path)) result.Add(path);
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
            return result;
        }

        /// <summary>Read live device-interface paths straight out of the PnP registry.</summary>
        public static List<string> FindDevicePathsRegistry()
        {
            var result = new List<string>();
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey($@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{{{InterfaceGuid}}}");
                if (key == null) return result;

                foreach (var sub in key.GetSubKeyNames())
                {
                    // The subkey name IS the NT device path with its "\\?\" prefix
                    // re-encoded as "##?#". Everything after that prefix must be kept
                    // byte-for-byte - notably "BTHENUM#{service-class-guid}" uses '#',
                    // NOT '\'. Swapping those to '\' yields ERROR_PATH_NOT_FOUND.
                    //   ##?#BTHENUM#{74ec..}_VID&..#7&..#{9eec..}  ->  \\?\BTHENUM#{74ec..}_VID&..#7&..#{9eec..}
                    const string RegPrefix = "##?#";
                    const string NtPrefix = @"\\?\";
                    if (!sub.StartsWith(RegPrefix, StringComparison.Ordinal)) continue;
                    result.Add(NtPrefix + sub.Substring(RegPrefix.Length));
                }
            }
            catch (Exception) { /* fall through to whatever SetupAPI found */ }
            return result;
        }

        // ---- open --------------------------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint acc, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint toWrite, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint toRead, out uint read, ref OVERLAPPED overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, ref OVERLAPPED ov, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED
        {
            public UIntPtr Internal;
            public UIntPtr InternalHigh;
            public IntPtr hEvent;
            public IntPtr Offset;
            public IntPtr OffsetHigh;
        }

        /// <summary>
        /// Post one OVERLAPPED read and return immediately.
        /// Overlapped I/O is mandatory here: the driver serialises IRPs, so a synchronous
        /// ReadFile left pending blocks every subsequent WriteFile. The real MagicPodsService
        /// hits the same constraint, which is why it imports ReadFileEx/WriteFileEx/CancelIoEx.
        /// </summary>
        public bool PostRead(out byte[] buffer, out OVERLAPPED ov, out ManualResetEvent ev)
        {
            buffer = new byte[ReadBufferSize];
            ev = new ManualResetEvent(false);
            ov = new OVERLAPPED { hEvent = ev.SafeWaitHandle.DangerousGetHandle() };
            uint read;
            if (ReadFile(_h, buffer, ReadBufferSize, out read, ref ov)) return true; // completed inline
            int err = Marshal.GetLastWin32Error();
            return err == 997 /*ERROR_IO_PENDING*/;
        }

        /// <summary>Wait for a posted read and return the exact-length payload (null on timeout).</summary>
        public byte[] CompleteRead(byte[] buffer, ref OVERLAPPED ov, ManualResetEvent ev, int timeoutMs)
        {
            if (!ev.WaitOne(timeoutMs)) return null;
            if (!GetOverlappedResult(_h, ref ov, out uint transferred, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetOverlappedResult failed");
            if (transferred == 0) return null;
            var exact = new byte[transferred];
            Array.Copy(buffer, exact, (int)transferred);
            return exact;
        }

        /// <summary>Blocking synchronous read. This is what MagicPods' ReceiveStringLoop does.</summary>
        public byte[] ReceiveSync()
        {
            var buf = new byte[ReadBufferSize];
            if (!ReadFileSync(_h, buf, ReadBufferSize, out uint read, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed");
            if (read == 0) return null;
            var exact = new byte[read];
            Array.Copy(buf, exact, (int)read);
            return exact;
        }

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadFile")]
        static extern bool ReadFileSync(SafeFileHandle h, byte[] buf, uint toRead, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelSynchronousIo(SafeFileHandle h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);

        /// <summary>Break a blocked ReceiveStringLoop, exactly as MagicPods does on shutdown.</summary>
        public bool CancelSyncIo() => CancelSynchronousIo(_h);

        volatile bool _loopStop;
        Thread _recvThread;

        /// <summary>
        /// Mirror of MagicPods' MagicAap.ReceiveStringLoop: a dedicated thread runs a
        /// blocking ReadFile.
        ///
        /// CRITICAL: because the driver queue is WdfIoQueueReadSynchronous +
        /// DispatchSequential, that blocking read OWNS the queue - a WriteFile issued
        /// while it is pending never completes (the GUI just hangs). That is exactly why
        /// MagicPods imports CancelSynchronousIo: to abort the pending read before
        /// writing. Callers must therefore StopReceiveLoop() before writing.
        /// </summary>
        public void StartReceiveLoop(Action<byte[]> onFrame, Action<Exception> onError = null)
        {
            _loopStop = false;
            _recvThread = new Thread(() =>
            {
                while (!_loopStop)
                {
                    try
                    {
                        var frame = ReceiveSync();
                        if (frame != null) onFrame(frame);
                    }
                    catch (Win32Exception ex)
                    {
                        // 995 / 0x80004005 is the expected result of CancelSyncIo.
                        if (!_loopStop && ex.NativeErrorCode != 995 && ex.NativeErrorCode != 0x80004005)
                            onError?.Invoke(ex);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!_loopStop) onError?.Invoke(ex);
                        return;
                    }
                }
            })
            { IsBackground = true, Name = "aap-recv" };
            _recvThread.Start();
        }

        /// <summary>
        /// Abort the pending read so the queue is free for a write.
        ///
        /// Uses CancelIoEx(handle, NULL) rather than CancelSynchronousIo: the latter only
        /// cancels synchronous I/O issued by the CALLING thread, so calling it from the
        /// writer thread does nothing to the reader thread's blocked ReadFile and we
        /// simply waited out the join timeout. CancelIoEx cancels every pending I/O on
        /// the handle regardless of which thread issued it.
        /// </summary>
        public void StopReceiveLoop(int timeoutMs = 1500)
        {
            _loopStop = true;
            CancelIoEx(_h, IntPtr.Zero);
            CancelSyncIo();                    // belt-and-braces for the reader thread
            _recvThread?.Join(timeoutMs);
            _recvThread = null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref long inBuf, int inSize,
                                           IntPtr outBuf, int outSize, out int bytesReturned, IntPtr ov);

        /// <summary>Issue an IOCTL with an 8-byte (ulong) input, matching MagicPodsService.</summary>
        public bool Ioctl(uint code, ulong input)
        {
            long buf = unchecked((long)input);
            bool ok = DeviceIoControl(_h, code, ref buf, 8, IntPtr.Zero, 0, out int ret, IntPtr.Zero);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeviceIoControl 0x{code:X} failed");
            return true;
        }

        public static AapTransport Open(string path, uint flagsAndAttributes = 0)
        {
            var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, SHARE_READ | SHARE_WRITE,
                                IntPtr.Zero, OPEN_EXISTING, flagsAndAttributes, IntPtr.Zero);
            if (h == null || h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    $"CreateFile failed ({err} {new Win32Exception(err).Message}) on:{Environment.NewLine}{path}");
            }
            return new AapTransport(path, h);
        }

        // ---- send --------------------------------------------------------------
        public uint Send(byte[] data)
        {
            if (!WriteFile(_h, data, (uint)data.Length, out uint written, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteFile failed");
            return written;
        }

        public void Dispose()
        {
            if (_h != null && !_h.IsClosed) _h.Dispose();
            _h = null;
        }
    }

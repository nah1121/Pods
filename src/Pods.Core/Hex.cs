using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pods.Core;

    public static class Hex
    {
        public static string F(byte[] b)
        {
            if (b == null || b.Length == 0) return "(empty)";
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }

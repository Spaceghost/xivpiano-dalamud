using System.Runtime.InteropServices;
using System.Text;

namespace XivPiano.Plugin;

/// <summary>
/// The saved password, encrypted with the Windows Data Protection API for the current user (Wine implements it
/// too), so the plugin's config file never holds it in the clear. Only this user on this machine can read it back.
/// </summary>
internal static partial class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("XivPiano.Pandora");

    public static string Protect(string secret)
    {
        if (secret.Length == 0)
            return "";
        var data = Encoding.UTF8.GetBytes(secret);
        return Convert.ToBase64String(Transform(data, protect: true));
    }

    public static string? Unprotect(string stored)
    {
        if (stored.Length == 0)
            return null;
        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(stored), protect: false));
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return null; // another user or machine: ask for it again
        }
    }

    private static unsafe byte[] Transform(byte[] input, bool protect)
    {
        fixed (byte* inPtr = input)
        fixed (byte* entPtr = Entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (IntPtr)inPtr };
            var entBlob = new DataBlob { Size = Entropy.Length, Data = (IntPtr)entPtr };
            var ok = protect
                ? CryptProtectData(ref inBlob, "XivPiano", ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok)
                throw new InvalidOperationException($"DPAPI failed ({Marshal.GetLastPInvokeError()}).");
            try
            {
                var output = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, output, 0, outBlob.Size);
                return output;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);
}

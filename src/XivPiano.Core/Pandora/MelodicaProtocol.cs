using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XivPiano.Core.Pandora;

/// <summary>
/// The native libpiano protocol session. HTTP, account policy and application
/// metadata belong to PandoraClient; native allocations never cross this boundary.
/// All access is serialized by PandoraClient's transaction gate.
/// </summary>
internal sealed class MelodicaProtocol : IDisposable
{
    private readonly SessionHandle session;

    public MelodicaProtocol(Partner partner)
    {
        if (Native.AbiVersion() != 1)
            throw new NotSupportedException("The installed melodica bridge has an incompatible ABI.");
        var pointer = Native.Create(partner.Host, partner.Username, partner.Password,
            partner.DeviceModel, partner.DecryptKey, partner.EncryptKey);
        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException("Could not initialize the melodica protocol session.");
        session = new SessionHandle(pointer);
    }

    public long ServerTime => Native.ServerTime(session);

    public (Uri Url, string Body) Prepare(string method, string body)
    {
        Check(Native.Prepare(session, method, body));
        var url = Marshal.PtrToStringUTF8(Native.RequestUrl(session));
        var content = Marshal.PtrToStringUTF8(Native.RequestBody(session));
        if (url == null || content == null)
            throw new InvalidOperationException("Melodica did not construct a complete request.");
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The Pandora protocol request must use HTTPS.");
        return (uri, content);
    }

    // The native partner-login result is CONTINUE_REQUEST (3), followed by
    // the user-login transaction. All other successful operations return OK (1).
    public int Accept(string response) => Native.Accept(session, response);

    public void Check(int result, bool allowContinue = false)
    {
        if (result == 1 || (result == 3 && allowContinue))
            return;
        if (result is 4 or -4)
            throw new OutOfMemoryException("Melodica could not allocate the protocol result.");
        throw new FormatException($"Melodica rejected the protocol transaction (status {result}).");
    }

    public void Abort() => Native.Abort(session);

    public void Dispose() => session.Dispose();

    private sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SessionHandle(IntPtr pointer) : base(ownsHandle: true) => SetHandle(pointer);

        protected override bool ReleaseHandle()
        {
            Native.Free(handle);
            return true;
        }
    }

    private static class Native
    {
        private const string Library = "xivpiano_melodica";

        static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (name != Library)
                return IntPtr.Zero;

            // Explicit path for isolated host/Wine tests and developer builds.
            // Failure is intentional: do not silently load another protocol backend.
            var explicitPath = Environment.GetEnvironmentVariable("XIVPIANO_MELODICA_LIBRARY");
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return NativeLibrary.Load(Path.GetFullPath(explicitPath));

            var directory = Path.GetDirectoryName(assembly.Location)!;
            var path = OperatingSystem.IsWindows()
                ? Path.Combine(directory, "runtimes", "win-x64", "native", Library + ".dll")
                : Path.Combine(directory, "lib" + Library + ".so");
            return NativeLibrary.Load(path);
        }

        [DllImport(Library, EntryPoint = "xp_bridge_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AbiVersion();

        [DllImport(Library, EntryPoint = "xp_session_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string partnerUser,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string partnerPassword,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string device,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string decryptKey,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string encryptKey);

        [DllImport(Library, EntryPoint = "xp_session_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(IntPtr session);

        [DllImport(Library, EntryPoint = "xp_prepare", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Prepare(SessionHandle session,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string method,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string body);

        [DllImport(Library, EntryPoint = "xp_request_url", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr RequestUrl(SessionHandle session);

        [DllImport(Library, EntryPoint = "xp_request_body", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr RequestBody(SessionHandle session);

        [DllImport(Library, EntryPoint = "xp_accept", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Accept(SessionHandle session, [MarshalAs(UnmanagedType.LPUTF8Str)] string response);

        [DllImport(Library, EntryPoint = "xp_abort", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Abort(SessionHandle session);

        [DllImport(Library, EntryPoint = "xp_server_time", CallingConvention = CallingConvention.Cdecl)]
        internal static extern long ServerTime(SessionHandle session);
    }
}

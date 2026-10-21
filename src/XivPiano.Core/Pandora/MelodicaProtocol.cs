using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
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

    public MelodicaProtocol(Partner partner, string? nativeLibraryPath = null)
    {
        var lease = Native.Acquire(nativeLibraryPath);
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (Native.AbiVersion() != 1)
                throw new NotSupportedException("The installed melodica bridge has an incompatible ABI.");
            pointer = Native.Create(partner.Host, partner.Username, partner.Password,
                partner.DeviceModel, partner.DecryptKey, partner.EncryptKey);
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("Could not initialize the melodica protocol session.");
            session = new SessionHandle(pointer, lease);
            pointer = IntPtr.Zero;
        }
        catch
        {
            try { if (pointer != IntPtr.Zero) Native.Free(pointer); }
            finally { lease.Dispose(); }
            throw;
        }
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

    // Observable without calling native code, including after unload is requested.
    internal static (int Loads, int Frees, int Leases, bool Closing, string? Source, string? LoadedPath)
        LoaderState => Native.State;

    private sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly IDisposable moduleLease;

        public SessionHandle(IntPtr pointer, IDisposable moduleLease) : base(ownsHandle: true)
        {
            this.moduleLease = moduleLease;
            SetHandle(pointer);
        }

        protected override bool ReleaseHandle()
        {
            try
            {
                Native.Free(handle);
                return true;
            }
            finally { moduleLease.Dispose(); }
        }
    }

    private static class Native
    {
        private const string Library = "xivpiano_melodica";
        private static readonly object moduleSync = new();
        private static IntPtr module;
        private static string? sourcePath;
        private static string? loadedPath;
        private static string? shadowDirectory;
        private static int leases;
        private static int loads;
        private static int frees;
        private static bool closing;

        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
            var context = AssemblyLoadContext.GetLoadContext(typeof(Native).Assembly);
            if (context?.IsCollectible == true)
                context.Unloading += _ => BeginUnload();
        }

        internal static (int Loads, int Frees, int Leases, bool Closing, string? Source, string? LoadedPath) State
        {
            get { lock (moduleSync) return (loads, frees, leases, closing, sourcePath, loadedPath); }
        }

        internal static IDisposable Acquire(string? configuredPath)
        {
            lock (moduleSync)
            {
                if (closing)
                    throw new ObjectDisposedException(nameof(MelodicaProtocol), "The plugin load context is unloading.");
                if (module != IntPtr.Zero)
                {
                    if (!string.IsNullOrWhiteSpace(configuredPath) &&
                        !SamePath(sourcePath!, Path.GetFullPath(configuredPath)))
                        throw new InvalidOperationException("The native protocol module is already loaded from another path.");
                }
                else
                {
                    var path = ResolvePath(configuredPath);
                    var context = AssemblyLoadContext.GetLoadContext(typeof(Native).Assembly);
                    string? shadow = null;
                    var actualPath = path;
                    try
                    {
                        if (context?.IsCollectible == true)
                        {
                            // Dalamud loads managed assemblies from streams. Keep the
                            // installation DLL replaceable while this context is active.
                            shadow = Path.Combine(Path.GetTempPath(), "xivpiano-melodica-" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(shadow);
                            actualPath = Path.Combine(shadow, Path.GetFileName(path));
                            File.Copy(path, actualPath);
                        }
                        module = NativeLibrary.Load(actualPath);
                        sourcePath = path;
                        loadedPath = actualPath;
                        shadowDirectory = shadow;
                        ++loads;
                    }
                    catch
                    {
                        if (shadow != null) DeleteShadow(shadow);
                        throw;
                    }
                }
                var lease = new ModuleLease();
                ++leases;
                return lease;
            }
        }

        private static bool SamePath(string left, string right) => string.Equals(left, right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static string ResolvePath(string? configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
                return Path.GetFullPath(configuredPath);
            var explicitPath = Environment.GetEnvironmentVariable("XIVPIANO_MELODICA_LIBRARY");
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return Path.GetFullPath(explicitPath);
            var location = typeof(Native).Assembly.Location;
            if (string.IsNullOrEmpty(location))
                throw new InvalidOperationException("The in-memory plugin must supply its native library path from DalamudPluginInterface.AssemblyLocation.");
            var directory = Path.GetDirectoryName(location)!;
            return OperatingSystem.IsWindows()
                ? Path.Combine(directory, "runtimes", "win-x64", "native", Library + ".dll")
                : Path.Combine(directory, "lib" + Library + ".so");
        }

        private static void BeginUnload()
        {
            lock (moduleSync)
            {
                closing = true;
                FreeIfUnused();
            }
        }

        private static void FreeIfUnused()
        {
            if (!closing || leases != 0 || module == IntPtr.Zero)
                return;
            NativeLibrary.Free(module);
            module = IntPtr.Zero;
            ++frees;
            if (shadowDirectory != null)
            {
                DeleteShadow(shadowDirectory);
                shadowDirectory = null;
            }
        }

        private static void DeleteShadow(string directory)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class ModuleLease : IDisposable
        {
            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lock (moduleSync)
                {
                    --leases;
                    FreeIfUnused();
                }
            }
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (name != Library)
                return IntPtr.Zero;

            lock (moduleSync)
            {
                // A final SessionHandle may bind xp_session_free for the first
                // time after Unloading. Its lease keeps this handle valid.
                if (module == IntPtr.Zero)
                    throw new ObjectDisposedException(nameof(MelodicaProtocol), "The native protocol module is not loaded.");
                return module;
            }
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

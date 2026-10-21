using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using XivPiano.Core.Pandora;

namespace XivPiano.Core.Tests;

public class MelodicaLoaderTests
{
    [Fact]
    public void MemoryLoadedCoreUsesConfiguredPathAndOneReplaceableShadowModule()
    {
        using var library = new NativeCopy();
        using var core = new MemoryCore();
        Assert.Equal(string.Empty, core.Assembly.Location);
        string? loaded = null;

        for (var i = 0; i < 12; ++i)
        {
            using var protocol = core.CreateProtocol(library.Path);
            var state = core.State;
            Assert.Equal(1, state.Loads);
            Assert.Equal(0, state.Frees);
            Assert.Equal(1, state.Leases);
            Assert.Equal(library.Path, state.Source);
            loaded ??= state.LoadedPath;
            Assert.Equal(loaded, state.LoadedPath);
            Assert.NotEqual(library.Path, loaded);
            Assert.Equal(File.ReadAllBytes(library.Path), File.ReadAllBytes(loaded!));

            // This fails on Windows if the installation file itself is loaded.
            File.Move(library.Path, library.Path + ".moved");
            File.Move(library.Path + ".moved", library.Path);

            var prepared = ((Uri Url, string Body))protocol.GetType().GetMethod("Prepare")!
                .Invoke(protocol, ["auth.partnerLogin", "{}"])!;
            Assert.Equal("https", prepared.Url.Scheme);
            protocol.GetType().GetMethod("Abort")!.Invoke(protocol, null);
        }

        Assert.Equal(0, core.State.Leases);
        Assert.Equal(0, core.State.Frees); // Cached P/Invoke targets remain valid.
        using var another = new NativeCopy();
        var mismatch = Assert.Throws<TargetInvocationException>(() => core.CreateProtocol(another.Path));
        Assert.IsType<InvalidOperationException>(mismatch.InnerException);
        Assert.Equal(1, core.State.Loads);

        core.RequestUnload();
        Assert.True(core.State.Closing);
        Assert.Equal(1, core.State.Frees);
        Assert.False(File.Exists(loaded));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(loaded!)));
    }

    [Fact]
    public async Task UnloadRequestWaitsForPausedHttpAndLateNativeSessionFree()
    {
        using var library = new NativeCopy();
        using var core = new MemoryCore();
        using var handler = new PausedPandora();
        using var http = new HttpClient(handler);
        var client = core.CreateClient(http, library.Path);
        try
        {
            var login = (Task)client.GetType().GetMethod("LoginAsync")!
                .Invoke(client, ["fake@example.test", "right", CancellationToken.None])!;
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var loaded = core.State.LoadedPath;
            Assert.Equal(1, core.State.Leases);

            core.RequestUnload();
            Assert.True(core.State.Closing);
            Assert.Equal(0, core.State.Frees);
            Assert.True(File.Exists(loaded));
            var rejected = Assert.Throws<TargetInvocationException>(() => core.CreateProtocol(library.Path));
            Assert.IsType<ObjectDisposedException>(rejected.InnerException);
            Assert.Equal(1, core.State.Leases);

            // The already leased session may accept a response and bind further
            // P/Invoke entry points after the context's Unloading event.
            handler.Resume.TrySetResult();
            await login.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True((bool)client.GetType().GetProperty("LoggedIn")!.GetValue(client)!);
            Assert.Equal(0, core.State.Frees);
            ((IDisposable)client).Dispose();
            await Until(() => core.State.Frees == 1);
            Assert.Equal(0, core.State.Leases);
            Assert.Equal(1, core.State.Loads);
            Assert.False(File.Exists(loaded));
        }
        finally
        {
            handler.Resume.TrySetResult();
            ((IDisposable)client).Dispose();
        }
    }

    [Fact]
    public void AbandonedSafeSessionKeepsModuleUntilItsNativeFinalizerCompletes()
    {
        using var library = new NativeCopy();
        using var core = new MemoryCore();
        var abandoned = AbandonPendingSession(core, library.Path);
        var loaded = core.State.LoadedPath;

        for (var i = 0; i < 12 && (abandoned.IsAlive || core.State.Frees != 1); ++i)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(abandoned.IsAlive);
        Assert.Equal(0, core.State.Leases);
        Assert.Equal(1, core.State.Frees);
        Assert.False(File.Exists(loaded));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonPendingSession(MemoryCore core, string path)
    {
        var protocol = core.CreateProtocol(path);
        protocol.GetType().GetMethod("Prepare")!.Invoke(protocol, ["auth.partnerLogin", "{}"]);
        Assert.Equal(1, core.State.Leases);
        core.RequestUnload();
        Assert.Equal(0, core.State.Frees);
        return new WeakReference(protocol);
    }

    private static async Task Until(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        Assert.True(completed());
    }

    private sealed class MemoryCore : IDisposable
    {
        private readonly AssemblyLoadContext context = new("XivPiano loader regression", isCollectible: true);
        private bool unloading;
        public Assembly Assembly { get; }

        public MemoryCore()
        {
            // Matches Dalamud: no managed assembly path survives LoadFromStream.
            using var stream = File.OpenRead(typeof(PandoraClient).Assembly.Location);
            Assembly = context.LoadFromStream(stream);
        }

        public (int Loads, int Frees, int Leases, bool Closing, string? Source, string? LoadedPath) State =>
            ((int, int, int, bool, string?, string?))Assembly
                .GetType("XivPiano.Core.Pandora.MelodicaProtocol")!
                .GetProperty("LoaderState", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        public IDisposable CreateProtocol(string path)
        {
            var partner = Assembly.GetType("XivPiano.Core.Pandora.Partner")!
                .GetField("Android", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            return (IDisposable)Activator.CreateInstance(
                Assembly.GetType("XivPiano.Core.Pandora.MelodicaProtocol")!,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, args: [partner, path], culture: null)!;
        }

        public object CreateClient(HttpClient http, string path) => Activator.CreateInstance(
            Assembly.GetType("XivPiano.Core.Pandora.PandoraClient")!, [http, null, path])!;

        public void RequestUnload()
        {
            if (unloading) return;
            unloading = true;
            context.Unload();
        }

        public void Dispose() => RequestUnload();
    }

    private sealed class NativeCopy : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "xivpiano-loader-source-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }

        public NativeCopy()
        {
            var original = Environment.GetEnvironmentVariable("XIVPIANO_MELODICA_LIBRARY");
            if (string.IsNullOrWhiteSpace(original))
            {
                var root = System.IO.Path.GetDirectoryName(typeof(PandoraClient).Assembly.Location)!;
                original = OperatingSystem.IsWindows()
                    ? System.IO.Path.Combine(root, "runtimes", "win-x64", "native", "xivpiano_melodica.dll")
                    : System.IO.Path.Combine(root, "libxivpiano_melodica.so");
            }
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(original));
            File.Copy(original, Path);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class PausedPandora : HttpMessageHandler
    {
        private readonly HttpMessageInvoker server = new(new FakePandora());
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Query.Contains("method=auth.userLogin"))
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(ct);
            }
            return await server.SendAsync(request, ct);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) server.Dispose();
            base.Dispose(disposing);
        }
    }
}

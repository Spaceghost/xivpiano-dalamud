using System.Security.Cryptography;
using System.Text.Json;
using XivPiano.Core.Pandora;

namespace XivPiano.Core;

/// <summary>Public song metadata only; never stores Pandora credentials, tokens or expiring URLs.</summary>
public sealed record SavedRecording(string Id, string Title, string Artist, string Album,
    long DurationMs, int Bitrate, double GainDb, long Bytes, DateTimeOffset SavedAt)
{
    public Track PlaybackTrack() => new($"recording:{Id}:{Guid.NewGuid():N}", "", Title, Artist, Album,
        null, Id, "mp3", Bitrate, TimeSpan.FromMilliseconds(DurationMs), false, GainDb, null, DateTimeOffset.UtcNow)
        { RecordingId = Id };
}

/// <summary>Content-addressed MP3s and atomic sidecars. Published snapshots never perform disk I/O on Draw.</summary>
public sealed class RecordingLibrary(string directory)
{
    public const int MaxTrackBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 5000;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SavedRecording[] entries = [];
    private string? error;
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public IReadOnlyList<SavedRecording> Entries => Volatile.Read(ref entries);
    public string? Error => Volatile.Read(ref error);
    public long TotalBytes => Entries.Sum(r => r.Bytes);

    public async Task LoadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureDirectory();
            var found = new List<SavedRecording>();
            var damaged = false;
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Take(MaxEntries))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (new FileInfo(file).Length > 16384) { damaged = true; continue; }
                    var item = JsonSerializer.Deserialize<SavedRecording>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                    if (item == null || !Valid(item) || Path.GetFileNameWithoutExtension(file) != item.Id ||
                        !File.Exists(AudioPath(item.Id)) || new FileInfo(AudioPath(item.Id)).Length != item.Bytes)
                    { damaged = true; continue; }
                    found.Add(item);
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
                { damaged = true; }
            }
            Volatile.Write(ref entries, found.OrderByDescending(r => r.SavedAt).ToArray());
            Volatile.Write(ref error, damaged ? "Some recordings could not be read. Their files have been left in the recordings folder." : null);
        }
        finally { gate.Release(); }
    }

    public async Task<SavedRecording> SaveAsync(Track track, ReadOnlyMemory<byte> mp3, long capacityBytes, CancellationToken ct)
    {
        if (mp3.Length is <= 0 or > MaxTrackBytes)
            throw new IOException("This song exceeds the 64 MiB recording limit; playback can continue without saving it.");
        var id = Convert.ToHexStringLower(SHA256.HashData(mp3.Span));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = entries.FirstOrDefault(r => r.Id == id);
            if (existing != null) return existing;
            EnsureDirectory();
            var files = Directory.EnumerateFiles(DirectoryPath, "*.mp3").Take(MaxEntries + 1).Select(p => new FileInfo(p)).ToArray();
            if (files.Length >= MaxEntries || files.Sum(f => f.Length) + mp3.Length > capacityBytes)
                throw new IOException("The recording library is full. Delete saved songs or increase its storage limit.");
            var item = new SavedRecording(id, Text(track.Title), Text(track.Artist), Text(track.Album),
                Math.Clamp((long)track.Length.TotalMilliseconds, 0, 86400000), Math.Clamp(track.Bitrate, 0, 1024),
                double.IsFinite(track.GainDb) ? Math.Clamp(track.GainDb, -15, 6) : 0, mp3.Length, DateTimeOffset.UtcNow);
            await AtomicWriteAsync(AudioPath(id), mp3, ct).ConfigureAwait(false);
            try { await AtomicWriteAsync(MetadataPath(id), JsonSerializer.SerializeToUtf8Bytes(item), ct).ConfigureAwait(false); }
            catch { File.Delete(AudioPath(id)); throw; }
            Volatile.Write(ref entries, new[] { item }.Concat(entries).ToArray());
            Volatile.Write(ref error, null);
            return item;
        }
        finally { gate.Release(); }
    }

    public async Task<byte[]> ReadAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new IOException("Invalid recording identifier.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var item = entries.FirstOrDefault(r => r.Id == id) ?? throw new IOException("This recording is no longer in the library.");
            var path = AudioPath(id);
            if (new FileInfo(path).Length != item.Bytes) throw new IOException("The recording file has changed.");
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != id) throw new IOException("The recording failed its integrity check.");
            return bytes;
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new IOException("Invalid recording identifier.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            File.Delete(AudioPath(id));
            File.Delete(MetadataPath(id));
            Volatile.Write(ref entries, entries.Where(r => r.Id != id).ToArray());
            Volatile.Write(ref error, null);
        }
        finally { gate.Release(); }
    }

    public void ReportError(string message) => Volatile.Write(ref error, message);

    private string AudioPath(string id) => Path.Combine(DirectoryPath, id + ".mp3");
    private string MetadataPath(string id) => Path.Combine(DirectoryPath, id + ".json");
    private static bool ValidId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Valid(SavedRecording r) => ValidId(r.Id) && r.Bytes is > 0 and <= MaxTrackBytes &&
        r.DurationMs is >= 0 and <= 86400000 && r.Bitrate is >= 0 and <= 1024 && double.IsFinite(r.GainDb) &&
        r.Title is { Length: <= 512 } && r.Artist is { Length: <= 512 } && r.Album is { Length: <= 512 };
    private static string Text(string value)
    {
        var clean = string.Concat(value.Where(c => !char.IsControl(c))).Trim();
        return clean[..Math.Min(clean.Length, 512)];
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
        else Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task AtomicWriteAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temp, options))
            {
                await file.WriteAsync(data, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

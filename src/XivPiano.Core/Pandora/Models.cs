namespace XivPiano.Core.Pandora;

/// <summary>A station. On the Shuffle (QuickMix) station, <see cref="QuickMixIds"/> are the stations it mixes.</summary>
public sealed record Station(string Token, string Id, string Name, bool IsQuickMix, bool IsShared)
{
    public IReadOnlyList<string> QuickMixIds { get; init; } = [];
}

/// <summary>What a station is built from: a song, an artist or a genre (<see cref="SeedId"/> removes it).</summary>
public sealed record Seed(string SeedId, SearchKind Kind, string Name, string? Artist);

/// <summary>A thumb you gave on a station (<see cref="FeedbackId"/> takes it back).</summary>
public sealed record Feedback(string FeedbackId, string Title, string Artist, bool Positive);

public sealed record StationDetails(Station Station, IReadOnlyList<Seed> Seeds, IReadOnlyList<Feedback> Feedback);

/// <summary>A Pandora station mode ("My Station", "Crowd Faves", "Deep Cuts", ...).</summary>
public sealed record StationMode(int Id, string Name, string Description, bool Active);

public sealed record GenreCategory(string Name, IReadOnlyList<Station> Stations);

/// <summary>One song of a playlist. Audio URLs expire about an hour after the playlist was fetched.</summary>
public sealed record Track(
    string Token,
    string StationId,
    string Title,
    string Artist,
    string Album,
    string? ArtUrl,
    string AudioUrl,
    string AudioEncoding,
    int Bitrate,
    TimeSpan Length,
    bool Loved,
    double GainDb,
    string? DetailUrl,
    DateTimeOffset FetchedAt)
{
    /// <summary>Thumbed down in this session (it is not played again on the station).</summary>
    public bool Banned { get; init; }

    /// <summary>Pandora's audio links stop working about an hour after the playlist call; skip past older ones.</summary>
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(55);

    public bool IsStale(DateTimeOffset now) => now - FetchedAt > UrlLifetime;
}

public enum SearchKind
{
    Song,
    Artist,
    Genre,
}

public sealed record SearchResult(SearchKind Kind, string MusicToken, string Name, string? Artist, int Score);

/// <summary>
/// The signed-in listener. <see cref="IsPaid"/>: Pandora Plus or Premium (Pandora One in the older clients),
/// judged the way Pithos (<c>isSubscriber</c>) and Elpis (no audio ads) each do, either one being enough.
/// </summary>
public sealed record Account(string UserId, bool IsSubscriber, bool HasAudioAds, string Client)
{
    public bool IsPaid => IsSubscriber || !HasAudioAds;
}

/// <summary>What to stream.</summary>
public enum AudioQuality
{
    /// <summary>The best MP3 the account gets: 192 kbit/s for paid accounts, 128 otherwise.</summary>
    Best,

    /// <summary>128 kbit/s MP3 even on a paid account: less data.</summary>
    Standard,
}

/// <summary>Which Pandora client identity to log in as.</summary>
public enum ClientChoice
{
    /// <summary>Android (as Elpis and pianobar); a paid account that gets no 192 kbit/s stream there moves to Pandora One.</summary>
    Automatic,

    /// <summary>Always the Android client.</summary>
    Android,

    /// <summary>Always the Pandora One client (as Pithos does for subscribers); paid accounts only.</summary>
    PandoraOne,
}

/// <summary>A "fail" answer from the API; <see cref="Code"/> is Pandora's error number.</summary>
public sealed class PandoraException(int code, string message) : Exception(Describe(code, message))
{
    public int Code { get; } = code;

    public const int InvalidAuthToken = 1001;
    public const int InvalidLogin = 1002;
    public const int ListenerNotAuthorized = 1003;
    public const int PartnerNotAuthorized = 1004;
    public const int StationLimit = 1005;
    public const int StationDoesNotExist = 1006;
    public const int ApiVersionNotSupported = 11;
    public const int LicensingRestrictions = 12;
    public const int ReadOnlyMode = 1000;

    /// <summary>What a player should be told, in words, for the errors people actually meet.</summary>
    public static string Describe(int code, string message) => code switch
    {
        InvalidLogin => "Pandora did not accept that email and password.",
        LicensingRestrictions => "Pandora is not available where this request comes from (it only serves the US).",
        ListenerNotAuthorized => "This Pandora account cannot listen right now (subscription or account status).",
        PartnerNotAuthorized => "Pandora refused this client's partner login; XivPiano may need an update.",
        StationLimit => "You have reached Pandora's limit of stations.",
        StationDoesNotExist => "That station does not exist any more.",
        ApiVersionNotSupported => "Pandora no longer supports this API version; XivPiano needs an update.",
        ReadOnlyMode => "Pandora is in maintenance (read-only) right now; try again later.",
        InvalidAuthToken => "The Pandora session expired.",
        _ => $"Pandora error {code}: {message}",
    };
}

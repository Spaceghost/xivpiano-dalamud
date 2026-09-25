using System.Text;

namespace XivPiano.Core.Pandora;

/// <summary>
/// A Pandora API "partner": the device identity a client logs in as before the user does, with the Blowfish
/// keys for that device. These are Pandora's published client credentials, the same ones pianobar, Pithos and
/// pydora ship; nothing secret about the user is here.
/// </summary>
public sealed record Partner(string DeviceModel, string Username, string Password, string EncryptKey, string DecryptKey, string Host)
{
    /// <summary>The Android partner: works for free and Plus accounts, and offers the 128 kbit/s MP3 stream.</summary>
    public static readonly Partner Android = new(
        DeviceModel: "android-generic",
        Username: "android",
        Password: "AC7IBG09A3DTSYM4R41UJWL07VLN8JI7",
        EncryptKey: "6#26FRL$ZWD",
        DecryptKey: "R=U!LH$O2B#",
        Host: "tuner.pandora.com");

    /// <summary>
    /// The Pandora One client, for paid accounts (Plus, Premium): its playlists carry a 192 kbit/s MP3 as the
    /// high-quality stream. It only answers on internal-tuner.pandora.com, and only logs in paid listeners.
    /// This is the route Pithos takes for subscribers.
    /// </summary>
    public static readonly Partner PandoraOne = new(
        DeviceModel: "D01",
        Username: "pandora one",
        Password: "TVCKIBGS9AO9TSYLNNFUML0743LH82D",
        EncryptKey: "2%3WCL*JU$MP]4",
        DecryptKey: "U#IO$RZPAB%VX2",
        Host: "internal-tuner.pandora.com");
}

/// <summary>Pandora's request wrapping: the JSON body zero-padded to 8 bytes, Blowfish-ECB, lowercase hex.</summary>
public sealed class PandoraCrypto(Partner partner)
{
    private readonly Blowfish encrypt = new(Encoding.ASCII.GetBytes(partner.EncryptKey));
    private readonly Blowfish decrypt = new(Encoding.ASCII.GetBytes(partner.DecryptKey));

    public string Encrypt(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        var padded = new byte[(raw.Length + 7) / 8 * 8];
        raw.CopyTo(padded, 0);
        encrypt.EncryptBlocks(padded);
        return Convert.ToHexStringLower(padded);
    }

    public byte[] Decrypt(string hex)
    {
        var data = Convert.FromHexString(hex);
        if (data.Length % 8 != 0)
            throw new FormatException("Pandora ciphertext is not whole 8-byte blocks.");
        decrypt.DecryptBlocks(data);
        return data;
    }

    /// <summary>
    /// The server clock from auth.partnerLogin's syncTime: after decryption, 4 junk bytes and then the Unix time in
    /// ASCII digits. Every later request must carry the server's time, so clients keep the offset to their own.
    /// </summary>
    public long DecryptSyncTime(string hex)
    {
        var plain = Decrypt(hex);
        long value = 0;
        var digits = 0;
        for (var i = 4; i < plain.Length && plain[i] is >= (byte)'0' and <= (byte)'9'; i++, digits++)
            value = (value * 10) + (plain[i] - '0');
        if (digits == 0)
            throw new FormatException("syncTime did not decrypt to a time; the partner keys are probably wrong.");
        return value;
    }
}

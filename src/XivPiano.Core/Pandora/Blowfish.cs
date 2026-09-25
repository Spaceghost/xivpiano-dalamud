namespace XivPiano.Core.Pandora;

/// <summary>
/// Blowfish (Schneier, 1993) in ECB mode, which is what Pandora's JSON API uses to wrap request bodies and the
/// login's sync time. Written from the published algorithm; the tables are generated (see BlowfishTables).
/// Not for anything that needs to be secret: Pandora's keys are public, and ECB leaks block patterns.
/// </summary>
public sealed class Blowfish
{
    private readonly uint[] p = new uint[18];
    private readonly uint[] s = new uint[1024];

    public Blowfish(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 1 or > 56)
            throw new ArgumentOutOfRangeException(nameof(key), "Blowfish keys are 1 to 56 bytes.");
        BlowfishTables.P.CopyTo(p, 0);
        BlowfishTables.S.CopyTo(s, 0);

        var k = 0;
        for (var i = 0; i < 18; i++)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++)
            {
                word = (word << 8) | key[k];
                k = (k + 1) % key.Length;
            }

            p[i] ^= word;
        }

        uint l = 0, r = 0;
        for (var i = 0; i < 18; i += 2)
        {
            Encrypt(ref l, ref r);
            p[i] = l;
            p[i + 1] = r;
        }

        for (var i = 0; i < 1024; i += 2)
        {
            Encrypt(ref l, ref r);
            s[i] = l;
            s[i + 1] = r;
        }
    }

    private uint F(uint x) =>
        ((s[x >> 24] + s[256 + ((x >> 16) & 0xFF)]) ^ s[512 + ((x >> 8) & 0xFF)]) + s[768 + (x & 0xFF)];

    private void Encrypt(ref uint l, ref uint r)
    {
        for (var i = 0; i < 16; i++)
        {
            l ^= p[i];
            r ^= F(l);
            (l, r) = (r, l);
        }

        (l, r) = (r, l);
        r ^= p[16];
        l ^= p[17];
    }

    private void Decrypt(ref uint l, ref uint r)
    {
        for (var i = 17; i > 1; i--)
        {
            l ^= p[i];
            r ^= F(l);
            (l, r) = (r, l);
        }

        (l, r) = (r, l);
        r ^= p[1];
        l ^= p[0];
    }

    /// <summary>Encrypts whole 8-byte blocks in place (big-endian halves, as the algorithm defines).</summary>
    public void EncryptBlocks(Span<byte> data) => Transform(data, true);

    /// <summary>Decrypts whole 8-byte blocks in place.</summary>
    public void DecryptBlocks(Span<byte> data) => Transform(data, false);

    private void Transform(Span<byte> data, bool encrypt)
    {
        if (data.Length % 8 != 0)
            throw new ArgumentException("Blowfish works on whole 8-byte blocks.", nameof(data));
        for (var i = 0; i < data.Length; i += 8)
        {
            var block = data.Slice(i, 8);
            var l = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block);
            var r = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block[4..]);
            if (encrypt)
                Encrypt(ref l, ref r);
            else
                Decrypt(ref l, ref r);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(block, l);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(block[4..], r);
        }
    }
}

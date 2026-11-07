using System.Text;
namespace XivPiano.Core.Tests;

public class SongShareTests
{
    [Fact]
    public void SharesPublicSongPathWithoutTrackingOrCredentials()
    {
        Assert.Equal("Listening to Title — Artist https://www.pandora.com/artist/song", SongShare.Format("Title", "Artist", "https://www.pandora.com/artist/song?token=private#tracking"));
        foreach (var url in new[] { "http://pandora.com/song", "https://pandora.com.evil.test/song", "https://user:password@pandora.com/song", "file:///secret", "https://audio.test/stream?token=private" })
            Assert.Equal("Listening to Title — Artist", SongShare.Format("Title", "Artist", url));
    }

    [Fact]
    public void KeepsClipboardOneLineBoundedAndUnicodeIntact()
    {
        var text = SongShare.Format("  Song\r\n\0\u202eName  ", " Artist\tName ", null);
        Assert.Equal("Listening to Song Name — Artist Name", text);
        text = SongShare.Format(string.Concat(Enumerable.Repeat("🎹", 200)), new string('界', 200), "https://pandora.com/" + new string('x', 500));
        Assert.True(Encoding.UTF8.GetByteCount(text) <= SongShare.MaxBytes);
        Assert.DoesNotContain("�", text);
        Assert.DoesNotContain("https", text);
    }
}

using Streamy.Core.Files;

namespace Streamy.Core.Tests;

public class PhoneFilesTests
{
    [Fact]
    public void ParsesAListingFoldersFirst()
    {
        const string output = """
            regular file|2048|1795094694|notes.txt
            directory|3452|1763517799|Alarms
            regular empty file|0|1795094694|.nomedia
            directory|3452|1795094694|Download
            regular file|5|1795094694|a|b.txt
            directory|3452|1795094694|.
            directory|3452|1795094694|..
            """;
        var files = PhoneFiles.ParseListing(output);
        Assert.Equal(["Alarms", "Download", ".nomedia", "a|b.txt", "notes.txt"], files.Select(f => f.Name));
        Assert.True(files[0].IsDirectory);
        Assert.Equal(0, files[0].Size); // folder sizes are block sizes, not contents
        Assert.Equal(2048, files[4].Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795094694), files[4].Modified);
    }

    [Fact]
    public void EmptyFolderListsNothing() => Assert.Empty(PhoneFiles.ParseListing(""));

    [Fact]
    public void Extension()
    {
        Assert.Equal("jpg", new PhoneFile("IMG_1.JPG", false, 1, default).Extension);
        Assert.Equal("", new PhoneFile(".nomedia", false, 0, default).Extension);
        Assert.Equal("", new PhoneFile("folder.d", true, 0, default).Extension);
    }

    [Theory]
    [InlineData("/sdcard", "'/sdcard'")]
    [InlineData("/sdcard/My Files", "'/sdcard/My Files'")]
    [InlineData("it's $HOME", "'it'\\''s $HOME'")]
    public void QuotesForSh(string input, string expected) => Assert.Equal(expected, PhoneFiles.Quote(input));

    [Fact]
    public void ListCommandQuotesThePathAndChecksItCanBeRead()
    {
        var cmd = PhoneFiles.ListCommand("/sdcard/a b");
        Assert.StartsWith("cd '/sdcard/a b' && ", cmd);
        Assert.Contains("[ -r . ]", cmd);
    }

    [Theory]
    [InlineData("/sdcard/Download", "/sdcard")]
    [InlineData("/sdcard", "/")]
    [InlineData("/sdcard/", "/")]
    [InlineData("/", null)]
    public void Parent(string path, string? parent) => Assert.Equal(parent, PhoneFiles.Parent(path));

    [Theory]
    [InlineData("sdcard//Download/", "/sdcard/Download")]
    [InlineData("/sdcard/./DCIM/../Download", "/sdcard/Download")]
    [InlineData("\\sdcard\\Music", "/sdcard/Music")]
    [InlineData("/..", "/")]
    public void Normalize(string path, string expected) => Assert.Equal(expected, PhoneFiles.Normalize(path));

    [Fact]
    public void CombineAddsOneSlash()
    {
        Assert.Equal("/sdcard/a.txt", PhoneFiles.Combine("/sdcard", "a.txt"));
        Assert.Equal("/a.txt", PhoneFiles.Combine("/", "a.txt"));
    }

    [Fact]
    public void UniqueNames()
    {
        Assert.Equal("New folder", PhoneFiles.UniqueName("New folder", ["Download"]));
        Assert.Equal("New folder (3)", PhoneFiles.UniqueName("New folder", ["new folder", "New folder (2)"]));
    }

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    public void ValidNames(string name, bool ok) => Assert.Equal(ok, PhoneFiles.IsValidName(name));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    [InlineData(150L * 1024 * 1024, "150 MB")]
    public void Sizes(long bytes, string text) => Assert.Equal(text, PhoneFiles.FormatSize(bytes));
}

using BSpeak.Host;

namespace BSpeak.Infrastructure.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v0.1.1", "0.1.0", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("0.1.0", "0.1.0-alpha.0.7", true)]   // a release beats the prerelease of the same version
    [InlineData("0.1.0-rc.1", "0.1.0", false)]
    public void Compares_semantic_versions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, Updates.IsNewer(candidate, current));

    [Fact]
    public void Notice_tells_the_agent_to_ask_first_and_how_to_update()
    {
        var notice = Updates.Notice(new UpdateStatus("0.1.0", "0.2.0", true, "https://example/r", DateTimeOffset.UtcNow, "release"))!;
        Assert.Contains("ask the user", notice);
        Assert.Contains("bspeak update", notice);
        Assert.Null(Updates.Notice(new UpdateStatus("0.2.0", "0.2.0", false, null, null, "release")));
        Assert.Contains("git pull", Updates.Notice(new UpdateStatus("0.1.0", "0.2.0", true, null, null, "source"))!);
    }

    [Fact]
    public void Parses_a_github_release_response()
    {
        const string json = """
            {"tag_name":"v0.1.1","html_url":"https://github.com/BlakeHastings/b-speak/releases/tag/v0.1.1","name":"b-speak v0.1.1",
             "assets":[{"name":"bspeak-0.1.1-win-x64.zip","size":1,"browser_download_url":"https://example/win.zip"},
                       {"name":"SHA256SUMS","size":1,"browser_download_url":"https://example/sums"}]}
            """;
        var release = Updates.ParseRelease(json);
        Assert.Equal("0.1.1", release.Version);
        Assert.Equal(["bspeak-0.1.1-win-x64.zip", "SHA256SUMS"], release.Assets.Select(a => a.Name));
        Assert.Equal("https://example/sums", release.Assets[1].Url);
    }

    [Fact]
    public void Swap_replaces_files_and_adds_new_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "bspeak-swap-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(root, "new");
        var to = Path.Combine(root, "bin");
        Directory.CreateDirectory(Path.Combine(from, "voices"));
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "bspeak.dll"), "v2");
        File.WriteAllText(Path.Combine(from, "voices", "a.bin"), "voice");
        File.WriteAllText(Path.Combine(to, "bspeak.dll"), "v1");
        File.WriteAllText(Path.Combine(to, "keep.txt"), "untouched");
        try
        {
            Updates.Swap(from, to);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(to, "bspeak.dll")));
            Assert.Equal("voice", File.ReadAllText(Path.Combine(to, "voices", "a.bin")));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(to, "keep.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

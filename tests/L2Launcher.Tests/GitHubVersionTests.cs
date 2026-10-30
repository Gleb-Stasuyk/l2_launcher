using L2Launcher.Core;
using L2Launcher.Core.Local;
using Xunit;

namespace L2Launcher.Tests;

public class GitHubVersionTests
{
    private const string Json = """
    [
      {"tag_name":"server-v0.1.0","draft":false,"prerelease":false,"html_url":"h1","assets":[]},
      {"tag_name":"v1.5.0-rc1","draft":false,"prerelease":true,"html_url":"h2","assets":[]},
      {"tag_name":"v1.4.0","draft":false,"prerelease":false,"html_url":"h3","assets":[
        {"name":"L2Launcher.exe","browser_download_url":"https://github.com/Gleb-Stasuyk/l2_launcher/releases/download/v1.4.0/L2Launcher.exe","size":123,
         "digest":"sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789"}]},
      {"tag_name":"v1.3.9","draft":true,"prerelease":false,"html_url":"h4","assets":[]}
    ]
    """;

    [Fact]
    public void PicksTheLatestLauncherReleaseIgnoringServerTagsDraftsAndPrereleases()
    {
        var r = GitHubReleases.Pick(Json, null)!;
        Assert.Equal("v1.4.0", r.Tag);
        Assert.Equal(new Version(1, 4, 0), r.Version);
        var a = Assert.Single(r.Assets);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", a.Sha256);
        Assert.True(GitHubReleases.IsTrustedAssetUrl(a.Url, GitHubReleases.LauncherRepo));
    }

    [Fact]
    public void PicksServerReleaseByPrefix()
    {
        var r = GitHubReleases.Pick(Json, GitHubReleases.ServerTagPrefix)!;
        Assert.Equal(new Version(0, 1, 0), r.Version);
    }

    [Theory]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("1.4", "1.4")]
    [InlineData("0.2.0-rc1", "0.2.0")]
    public void ParsesTags(string tag, string want) => Assert.Equal(Version.Parse(want), GitHubReleases.ParseVersion(tag));

    [Fact]
    public void DevAndGarbageAreNotVersions()
    {
        Assert.Null(GitHubReleases.ParseVersion("dev"));
        Assert.Null(GitHubReleases.ParseVersion(""));
    }

    [Fact]
    public void ComparesIgnoringMissingParts() => Assert.Equal(0, GitHubReleases.Compare(new Version(1, 4, 0, 0), new Version(1, 4)));

    [Fact]
    public void UntrustedUrlsAreRejected()
    {
        Assert.False(GitHubReleases.IsTrustedAssetUrl("http://github.com/Gleb-Stasuyk/l2_launcher/releases/download/v1/L2Launcher.exe", GitHubReleases.LauncherRepo));
        Assert.False(GitHubReleases.IsTrustedAssetUrl("https://evil.com/Gleb-Stasuyk/l2_launcher/releases/download/v1/L2Launcher.exe", GitHubReleases.LauncherRepo));
        Assert.False(GitHubReleases.IsTrustedAssetUrl("https://github.com/other/l2_launcher/releases/download/v1/L2Launcher.exe", GitHubReleases.LauncherRepo));
    }

    [Theory]
    [InlineData("ghcr.io/gleb-stasuyk/l2start-server:0.1.0", "0.1.0")]
    [InlineData("l2start-server:dev", "dev")]
    [InlineData("localhost:5000/l2start-server", null)]
    public void ImageTag(string image, string? tag) => Assert.Equal(tag, ServerVersion.TagOf(image));

    [Fact]
    public void ServerComparison()
    {
        var latest = new Version(0, 1, 0);
        Assert.Equal(VersionCompare.UpToDate, ServerVersion.Compare("0.1.0", latest));
        Assert.Equal(VersionCompare.Outdated, ServerVersion.Compare("0.0.9", latest));
        Assert.Equal(VersionCompare.Newer, ServerVersion.Compare("0.2.0", latest));
        Assert.Equal(VersionCompare.Unknown, ServerVersion.Compare("dev", latest));
        Assert.Equal(VersionCompare.Unknown, ServerVersion.Compare(null, latest));
    }
}
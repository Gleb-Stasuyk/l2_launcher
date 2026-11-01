using L2Launcher.Core;
using Xunit;

public class L2IniTests
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "data", "l2-sample.ini");

    [Fact]
    public void DecodesRealClientIni()
    {
        var plain = L2Ini.Decode(File.ReadAllBytes(Sample));
        Assert.Equal("80.78.246.62", L2Ini.ReadServerAddr(plain));
    }

    [Fact]
    public void RoundTripsAndRewritesServerAddr()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(dir, "system"));
        var ini = Path.Combine(dir, "system", "l2.ini");
        File.Copy(Sample, ini);
        Assert.True(L2Ini.SetServerAddr(dir, "89.104.70.159"));
        var raw = File.ReadAllBytes(ini);
        Assert.True(L2Ini.IsEncrypted(raw));
        Assert.Equal("89.104.70.159", L2Ini.ReadServerAddr(L2Ini.Decode(raw)));
        Assert.False(L2Ini.SetServerAddr(dir, "89.104.70.159"));
        Assert.Equal(L2Ini.Decode(File.ReadAllBytes(Sample)).Length + 1, L2Ini.Decode(raw).Length);
    }
}
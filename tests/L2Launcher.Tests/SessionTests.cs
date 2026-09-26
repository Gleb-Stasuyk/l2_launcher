using L2Launcher.Core;
using Xunit;

namespace L2Launcher.Tests;

public class SessionTests
{
    sealed class Fake
    {
        public List<string> Calls { get; } = new();
        public bool CheckResult = true;
        public LauncherSession Session { get; }
        public Fake()
        {
            Session = new LauncherSession(
                (full, ct) => { Calls.Add(full ? "check-full" : "check-quick"); return Task.FromResult(CheckResult); },
                ct => { Calls.Add("launch"); return Task.CompletedTask; });
        }
    }

    [Fact]
    public void NothingHappensAtStartup()
    {
        var f = new Fake();
        Assert.Empty(f.Calls);
        Assert.False(f.Session.VerifiedThisSession);
    }

    [Fact]
    public async Task PlayWithoutPriorCheck_ChecksThenLaunches()
    {
        var f = new Fake();
        Assert.True(await f.Session.PlayAsync());
        Assert.Equal(new[] { "check-quick", "launch" }, f.Calls);
        Assert.True(f.Session.VerifiedThisSession);
    }

    [Fact]
    public async Task PlayAfterSuccessfulCheck_LaunchesWithoutRecheck()
    {
        var f = new Fake();
        Assert.True(await f.Session.CheckFilesAsync());
        Assert.True(await f.Session.PlayAsync());
        Assert.True(await f.Session.PlayAsync());
        Assert.Equal(new[] { "check-full", "launch", "launch" }, f.Calls);
    }

    [Fact]
    public async Task CheckFilesNeverLaunches()
    {
        var f = new Fake();
        await f.Session.CheckFilesAsync();
        await f.Session.CheckFilesAsync();
        Assert.Equal(new[] { "check-full", "check-full" }, f.Calls);
    }

    [Fact]
    public async Task FailedCheck_NoLaunch_AndNextPlayChecksAgain()
    {
        var f = new Fake { CheckResult = false };
        Assert.False(await f.Session.PlayAsync());
        Assert.False(f.Session.VerifiedThisSession);
        f.CheckResult = true;
        Assert.True(await f.Session.PlayAsync());
        Assert.Equal(new[] { "check-quick", "check-quick", "launch" }, f.Calls);
    }

    [Fact]
    public async Task FailedExplicitCheck_ResetsVerifiedState()
    {
        var f = new Fake();
        await f.Session.CheckFilesAsync();
        f.CheckResult = false;
        Assert.False(await f.Session.CheckFilesAsync());
        Assert.False(await f.Session.PlayAsync());
        Assert.Equal(new[] { "check-full", "check-full", "check-quick" }, f.Calls);
    }

    [Fact]
    public async Task InvalidateForcesCheckOnNextPlay()
    {
        var f = new Fake();
        await f.Session.CheckFilesAsync();
        f.Session.Invalidate();
        await f.Session.PlayAsync();
        Assert.Equal(new[] { "check-full", "check-quick", "launch" }, f.Calls);
    }

    [Fact]
    public async Task ConcurrentClicksAreIgnoredWhileBusy()
    {
        var gate = new TaskCompletionSource<bool>();
        int checks = 0;
        var s = new LauncherSession((full, ct) => { checks++; return gate.Task; }, ct => Task.CompletedTask);
        var first = s.PlayAsync();
        Assert.True(s.Busy);
        Assert.False(await s.PlayAsync());
        Assert.False(await s.CheckFilesAsync());
        gate.SetResult(true);
        Assert.True(await first);
        Assert.Equal(1, checks);
    }
}

using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class PolishTests
{
    [Theory]
    [InlineData(-1920, -200, 1920, 1080, -960, 340)]
    [InlineData(100, 200, 101, 201, 150, 300)]
    public void CenterUsesSelectedMonitorPhysicalCoordinates(int x, int y, int w, int h, int expectedX, int expectedY)
    {
        var monitor = new MonitorInfo("selected", x, y, w, h, 144, false);
        Assert.Equal((expectedX, expectedY), monitor.Center);
    }
    [Fact]
    public void DiscoveryMergesDuplicateEndpointsButNotDifferentMacsWithTheSameName()
    {
        var result = MacCandidates.Visible([
            new("unpaired", "Mac", "aa:bb", false, MajorClass: 1), new("paired", "Mac", "AA-BB", true),
            new("other", "Mac", "cc:dd", false, MajorClass: 1), new("unnamed", "", "ee:ff", false, MajorClass: 1)
        ]);
        Assert.Equal(new[] { "paired", "other" }, result.Select(item => item.Id));
    }
    [Fact]
    public void FreshDualModeMacUsesClassicPairingButAnExistingBondIsPreferred()
    {
        var classic = new MacCandidate("classic", "Mac", "AA:BB", false, MacTransport.Classic, MajorClass: 1);
        var le = new MacCandidate("le", "Mac", "aa-bb", false);
        Assert.Equal(classic, Assert.Single(MacCandidates.Visible([le, classic])));
        var paired = le with { Paired = true };
        Assert.Equal(paired, Assert.Single(MacCandidates.Visible([classic, paired])));
    }
    [Fact]
    public void DefaultDiscoveryShowsComputersAndVerifiedSelectionWhileShowAllRestoresOtherDevices()
    {
        MacCandidate[] found = [
            new("computer", "Workstation", "01", false, MajorClass: 1),
            new("keyboard", "Mac keyboard", "02", true, MajorClass: 5),
            new("speaker", "Speaker", "03", false, MajorClass: 4),
            new("phone", "Phone", "04", false, MajorClass: 2),
            new("unknown", "Unknown category", "05", false),
            new("saved", "Previously verified Mac", "06", true)
        ];
        Assert.Equal(new[] { "computer" }, MacCandidates.Visible(found).Select(item => item.Id));
        Assert.Equal(new[] { "saved", "computer" },
            MacCandidates.Visible(found, knownDeviceIds: new HashSet<string> { "saved" }).Select(item => item.Id));
        Assert.Equal(found.Length, MacCandidates.Visible(found, showAll: true).Length);
        Assert.Single(MacCandidates.Visible(found));
    }
    [Fact]
    public void ClassificationArrivingLaterMakesTheComputerVisible()
    {
        var candidate = new MacCandidate("mac", "Mac", "01", false);
        Assert.Empty(MacCandidates.Visible([candidate]));
        Assert.Equal(candidate, Assert.Single(MacCandidates.Visible([candidate], showAll: true)));
        var updated = candidate with { MajorClass = 1 };
        Assert.Equal(updated, Assert.Single(MacCandidates.Visible([updated])));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ConnectionVerifiesBeforeSavingAndReusesExistingBonds(bool created)
    {
        var connection = new Connection(created);
        await MacConnection.Connect(connection);
        Assert.Equal(new[] { "pair", "verify", "save" }, connection.Calls);
    }
    [Theory]
    [InlineData("verify", true)] [InlineData("save", true)]
    [InlineData("verify", false)] [InlineData("save", false)]
    public async Task FailedConnectionNeverUnpairsAnExistingBond(string failure, bool created)
    {
        var connection = new Connection(created, failure);
        await Assert.ThrowsAsync<IOException>(() => MacConnection.Connect(connection));
        Assert.Equal(created, connection.Calls.Contains("rollback"));
        if (failure == "verify") Assert.DoesNotContain("save", connection.Calls);
    }
    [Fact]
    public async Task RollbackFailureIsReportedWithTheOriginalConnectionFailure()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MacConnection.Connect(new Connection(true, "verify", true)));
        Assert.Contains("verify failed", error.Message); Assert.Contains("rollback failed", error.Message);
    }
    [Theory]
    [InlineData(null, 3)] [InlineData("stop", 1)] [InlineData("unregister", 2)] [InlineData("files", 3)]
    public async Task RemovalStopsAtFailureAndPreservesLaterRetrySteps(string? failure, int calls)
    {
        var steps = new RemovalSteps(failure);
        if (failure is null) await RemovalWorkflow.Run(steps);
        else await Assert.ThrowsAsync<IOException>(() => RemovalWorkflow.Run(steps));
        Assert.Equal(new[] { "stop", "unregister", "files" }.Take(calls), steps.Calls);
    }
    private sealed class Connection(bool created, string? failure = null, bool rollbackFailure = false) : IMacConnection
    {
        public List<string> Calls { get; } = [];
        public Task<bool> Pair() { Calls.Add("pair"); return Task.FromResult(created); }
        public Task<CompanionSettings> Verify()
        {
            Calls.Add("verify");
            if (failure == "verify") throw new IOException("verify failed");
            return Task.FromResult(new CompanionSettings("specific-endpoint", "Mac"));
        }
        public Task Save(CompanionSettings settings)
        {
            Calls.Add("save"); Assert.Equal("specific-endpoint", settings.DeviceId);
            if (failure == "save") throw new IOException("save failed");
            return Task.CompletedTask;
        }
        public Task RollbackPairing()
        {
            Calls.Add("rollback");
            if (rollbackFailure) throw new IOException("rollback failed");
            return Task.CompletedTask;
        }
    }
    private sealed class RemovalSteps(string? failure) : IRemovalSteps
    {
        public List<string> Calls { get; } = [];
        private Task Step(string name) { Calls.Add(name); if (failure == name) throw new IOException(name); return Task.CompletedTask; }
        public Task Stop() => Step("stop");
        public Task Unregister() => Step("unregister");
        public Task FinishFiles() => Step("files");
    }
}

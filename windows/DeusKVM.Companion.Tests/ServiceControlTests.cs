using System.Text;
using DeusKVM.Companion.Core;
using Xunit;

namespace DeusKVM.Companion.Tests;

public sealed class ServiceControlTests
{
    private const string MacId = "5c8a1f2e-3b4d-4e6f-8a9b-0c1d2e3f4a5b";

    private sealed class FakeTarget : IServiceControlTarget
    {
        public readonly List<string> Calls = [];
        public Exception? Failure;
        public ServiceSnapshot? Snapshot;
        public ServiceSnapshot? Status() { Calls.Add("status"); return Snapshot; }
        public void Configure(CompanionSettings mac) { Calls.Add($"configure {mac.DeviceId} {mac.DeviceName}"); Throw(); }
        public void SetAutomaticStartup(bool automatic) { Calls.Add($"startup {automatic}"); Throw(); }
        public void SetTrayStartup(bool enabled) { Calls.Add($"tray {enabled}"); Throw(); }
        public void StopService() { Calls.Add("stop"); Throw(); }
        private void Throw() { if (Failure is not null) throw Failure; }
    }

    [Fact]
    public void RequestsRoundTripAndEveryCommandReachesTheTarget()
    {
        var target = new FakeTarget();
        ServiceControlRequest[] requests =
        [
            new(ServiceControlProtocol.Configure, Mac: new CompanionSettings(MacId, "Amadeus’s Mac Studio")),
            new(ServiceControlProtocol.Startup, Enabled: false),
            new(ServiceControlProtocol.TrayStartup, Enabled: true),
            new(ServiceControlProtocol.Stop)
        ];
        foreach (var request in requests)
        {
            var parsed = ServiceControlProtocol.Parse(ServiceControlProtocol.Serialize(request));
            Assert.Equal(request.Command, parsed.Command);
            Assert.True(ServiceControlProtocol.Handle(parsed, target).Ok);
        }
        Assert.Equal([$"configure {MacId} Amadeus’s Mac Studio", "startup False", "tray True", "stop"], target.Calls);
    }

    [Fact]
    public void StatusCarriesTheSnapshot()
    {
        var worker = new WorkerStatus("Connected", "Windows edge return ready", DateTimeOffset.UtcNow, DeviceName: "Mac");
        var target = new FakeTarget { Snapshot = new ServiceSnapshot(1, "SYSTEM", 0, 1, "User session present", worker, DateTimeOffset.UtcNow) };
        var response = ServiceControlProtocol.Handle(ServiceControlProtocol.Parse("{\"Command\":\"status\"}"), target);
        var line = ServiceControlProtocol.Serialize(response);
        Assert.DoesNotContain('\n', line);
        var parsed = ServiceControlProtocol.ParseResponse(line);
        Assert.True(parsed.Ok);
        Assert.Equal("Mac", parsed.Status?.Worker.DeviceName);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Command\":\"install\"}")]
    [InlineData("{\"Command\":\"remove\"}")]
    [InlineData("{\"Command\":\"configure\"}")]
    [InlineData("{\"Command\":\"configure\",\"Mac\":{\"DeviceId\":\"\",\"DeviceName\":\"Mac\"}}")]
    [InlineData("{\"Command\":\"configure\",\"Mac\":{\"DeviceId\":\"id\",\"DeviceName\":\"two\\nlines\"}}")]
    [InlineData("{\"Command\":\"startup\"}")]
    [InlineData("{\"Command\":\"tray-startup\"}")]
    public void UnusableRequestsAreRejected(string line) =>
        Assert.Throws<ArgumentException>(() => ServiceControlProtocol.Parse(line));

    [Fact]
    public void OversizedRequestsAreRejected()
    {
        var name = new string('x', ServiceControlProtocol.MaximumRequest);
        var line = ServiceControlProtocol.Serialize(new ServiceControlRequest(ServiceControlProtocol.Configure, Mac: new CompanionSettings(MacId, name)));
        Assert.Throws<ArgumentException>(() => ServiceControlProtocol.Parse(line));
    }

    [Fact]
    public void ExpectedFailuresBecomeMessagesAndOthersPropagate()
    {
        var target = new FakeTarget { Failure = new InvalidOperationException("Install the service first.") };
        var response = ServiceControlProtocol.Handle(new(ServiceControlProtocol.Stop), target);
        Assert.False(response.Ok);
        Assert.Equal("Install the service first.", response.Error);
        target.Failure = new UnauthorizedAccessException("denied");
        Assert.Equal("denied", ServiceControlProtocol.Handle(new(ServiceControlProtocol.TrayStartup, Enabled: true), target).Error);
        target.Failure = new NullReferenceException();
        Assert.Throws<NullReferenceException>(() => ServiceControlProtocol.Handle(new(ServiceControlProtocol.Stop), target));
        // Watch is the server's own stream, not a dispatched command.
        Assert.False(ServiceControlProtocol.Handle(new(ServiceControlProtocol.Watch), new FakeTarget()).Ok);
    }

    [Fact]
    public void AStatusTooLargeToSendSaysSo()
    {
        var worker = new WorkerStatus("Connected", new string('x', ServiceControlProtocol.MaximumResponse), DateTimeOffset.UtcNow);
        var line = ServiceControlProtocol.Serialize(new ServiceControlResponse(true,
            Status: new ServiceSnapshot(1, "SYSTEM", 0, 1, "", worker, DateTimeOffset.UtcNow)));
        Assert.True(Encoding.UTF8.GetByteCount(line) <= ServiceControlProtocol.MaximumResponse);
        Assert.False(ServiceControlProtocol.ParseResponse(line).Ok);
    }

    [Fact]
    public async Task LinesAreBoundedAndEndCleanly()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\r\nsecond ✓\n"));
        Assert.Equal("first", await ServiceControlProtocol.ReadLineAsync(stream, 64, CancellationToken.None));
        Assert.Equal("second ✓", await ServiceControlProtocol.ReadLineAsync(stream, 64, CancellationToken.None));
        Assert.Null(await ServiceControlProtocol.ReadLineAsync(stream, 64, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServiceControlProtocol.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes("toolong\n")), 4, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServiceControlProtocol.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes("no newline")), 64, CancellationToken.None));
        var written = new MemoryStream();
        await ServiceControlProtocol.WriteLineAsync(written, "{\"Ok\":true}", CancellationToken.None);
        Assert.Equal("{\"Ok\":true}\n", Encoding.UTF8.GetString(written.ToArray()));
    }
}

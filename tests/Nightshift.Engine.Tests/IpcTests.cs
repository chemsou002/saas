using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;
using Nightshift.Ipc;

namespace Nightshift.Engine.Tests;

/// <summary>Real named pipes (Unix domain sockets on Linux, named pipes on Windows).</summary>
public sealed class IpcTests : IAsyncLifetime, IDisposable
{
    private readonly EngineFixture _f = new();
    private readonly string _pipe = "nightshift-test-" + Guid.NewGuid().ToString("N")[..12];
    private SessionManager _manager = null!;
    private IpcServer _server = null!;
    private readonly FixedIdentity _identity = new(EngineFixture.Alice);

    public Task InitializeAsync()
    {
        _manager = _f.NewManager();
        _manager.RecoverOnStartup();
        var handler = new SessionCommandHandler(_manager, () => new SystemStatusSnapshot
        {
            Capabilities = _f.Caps.Get(),
            Power = _f.Power.Get(),
            Compatibility = Nightshift.Core.Compatibility.CompatibilityAssessor.Assess(_f.Caps.Get(), _f.Power.Get(), 1, 1),
            Service = new ServiceHealthSnapshot { InstanceId = Guid.Empty, Version = "test", StartedAt = DateTimeOffset.UtcNow, Healthy = true },
        });
        _server = new IpcServer(new IpcServerOptions { PipeName = _pipe, RequestTimeout = TimeSpan.FromSeconds(2) }, handler, _identity);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    public void Dispose() => _f.Dispose();

    private NightshiftClient Client() => new(_pipe, TimeSpan.FromSeconds(5));

    private static StartSessionPayload Start(string mode = "timed", double? seconds = 3600) => new()
    {
        Mode = mode,
        DurationSeconds = seconds,
        Applications = [new ApplicationPayload { Name = "Claude Code", ProcessId = 4242 }],
        VentilationAcknowledged = true,
    };

    [Fact]
    public async Task All_commands_round_trip()
    {
        var c = Client();
        var started = await c.StartSessionAsync(Start());
        Assert.Equal(SessionStatus.Running, started.Status);
        Assert.Equal(TimeSpan.FromHours(1), started.Duration);
        Assert.Equal(EngineFixture.Alice.UserName, started.OwnerName);

        var status = await c.GetSessionStatusAsync();
        Assert.Equal(started.SessionId, status.Active.SessionId);
        Assert.True(status.Active.KeepAwakeHeld);

        Assert.Equal(SessionStatus.Paused, (await c.PauseSessionAsync()).Status);
        Assert.Equal(SessionStatus.Running, (await c.ResumeSessionAsync()).Status);
        Assert.Equal(SessionStatus.Stopped, (await c.StopSessionAsync()).Status);

        var after = await c.GetSessionStatusAsync();
        Assert.False(after.Active.HasSession);
        Assert.Equal(started.SessionId, after.Last!.SessionId);

        var system = await c.GetSystemStatusAsync();
        Assert.True(system.Capabilities.LidPresent);
        Assert.Equal("test", system.Service.Version);
        Assert.True(_f.SettingsAreOriginal());
    }

    [Fact]
    public async Task Unlimited_session_over_ipc()
    {
        var s = await Client().StartSessionAsync(Start("unlimited", null));
        Assert.Equal(SessionMode.Unlimited, s.Mode);
        Assert.Null(s.Remaining);
    }

    [Fact]
    public async Task Engine_errors_are_forwarded_with_codes()
    {
        var c = Client();
        var ex = await Assert.ThrowsAsync<IpcRemoteException>(() => c.StopSessionAsync());
        Assert.Equal(EngineErrorCodes.NoActiveSession, ex.Code);

        ex = await Assert.ThrowsAsync<IpcRemoteException>(() => c.StartSessionAsync(Start() with { VentilationAcknowledged = false }));
        Assert.Equal(EngineErrorCodes.VentilationAckRequired, ex.Code);

        await c.StartSessionAsync(Start());
        ex = await Assert.ThrowsAsync<IpcRemoteException>(() => c.StartSessionAsync(Start()));
        Assert.Equal(EngineErrorCodes.SessionAlreadyActive, ex.Code);
    }

    [Fact]
    public async Task Identity_comes_from_the_transport_not_the_request()
    {
        await Client().StartSessionAsync(Start());
        _identity.Current = EngineFixture.Bob;
        var ex = await Assert.ThrowsAsync<IpcRemoteException>(() => Client().StopSessionAsync());
        Assert.Equal(EngineErrorCodes.NotAuthorized, ex.Code);
        _identity.Current = EngineFixture.Alice;
        await Client().StopSessionAsync();
    }

    [Fact]
    public async Task Unauthenticated_callers_are_refused()
    {
        _identity.Current = null;
        var response = await Client().SendAsync(new IpcRequest { Command = IpcCommands.GetSessionStatus });
        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCodes.Unauthenticated, response.Error!.Code);
    }

    [Theory]
    [InlineData("""{"version":1,"requestId":"a1","command":"FORMAT_DISK"}""", IpcErrorCodes.UnknownCommand)]
    [InlineData("""{"version":2,"requestId":"a1","command":"GET_SESSION_STATUS"}""", IpcErrorCodes.UnsupportedVersion)]
    [InlineData("""{"version":1,"requestId":"bad id!","command":"GET_SESSION_STATUS"}""", IpcErrorCodes.MalformedRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"GET_SESSION_STATUS","extra":true}""", IpcErrorCodes.MalformedRequest)]
    [InlineData("""not json""", IpcErrorCodes.MalformedRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"STOP_SESSION","payload":{"force":true}}""", EngineErrorCodes.InvalidRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"START_SESSION"}""", EngineErrorCodes.InvalidRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"START_SESSION","payload":{"mode":"timed","durationSeconds":0,"ventilationAcknowledged":true}}""", EngineErrorCodes.InvalidRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"START_SESSION","payload":{"mode":"forever","ventilationAcknowledged":true}}""", EngineErrorCodes.InvalidRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"START_SESSION","payload":{"mode":"unlimited","ventilationAcknowledged":true,"runAsSystem":true}}""", EngineErrorCodes.InvalidRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"START_SESSION","payload":{"mode":"unlimited","ventilationAcknowledged":true,"applications":[{"name":"x","executablePath":"relative\\app.exe"}]}}""", EngineErrorCodes.InvalidRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"START_SESSION","payload":{"mode":"unlimited","ventilationAcknowledged":true,"applications":[{"name":"x","processId":4}]}}""", EngineErrorCodes.InvalidRequest)]
    [InlineData("""{"version":1,"requestId":"a1","command":"START_SESSION","payload":{"mode":"unlimited","ventilationAcknowledged":true,"powerPolicy":"battery-only"}}""", EngineErrorCodes.InvalidRequest)]
    public async Task Invalid_requests_are_rejected(string json, string expectedCode)
    {
        var response = await RawAsync(Encoding.UTF8.GetBytes(json));
        Assert.False(response!.Ok);
        Assert.Equal(expectedCode, response.Error!.Code);
        Assert.Empty(_f.Store.Writes);
    }

    [Fact]
    public async Task Too_many_applications_are_rejected()
    {
        var apps = Enumerable.Range(0, SessionCommandHandler.MaxApplications + 1).Select(i => new ApplicationPayload { Name = $"app{i}" }).ToList();
        var ex = await Assert.ThrowsAsync<IpcRemoteException>(() => Client().StartSessionAsync(Start() with { Applications = apps }));
        Assert.Equal(EngineErrorCodes.InvalidRequest, ex.Code);
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_and_server_keeps_serving()
    {
        await using (var pipe = await ConnectAsync())
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, IpcDefaults.MaxFrameBytes + 1);
            await pipe.WriteAsync(header);
            var frame = await FrameCodec.ReadAsync(pipe, IpcDefaults.MaxFrameBytes, CancellationToken.None);
            var response = JsonSerializer.Deserialize<IpcResponse>(frame!, IpcJson.Options)!;
            Assert.Equal(IpcErrorCodes.FrameTooLarge, response.Error!.Code);
        }
        Assert.False((await Client().GetSessionStatusAsync()).Active.HasSession);
    }

    [Fact]
    public async Task Abrupt_disconnects_and_silent_clients_do_not_break_the_server()
    {
        for (var i = 0; i < 6; i++)
        {
            await using var pipe = await ConnectAsync();
            await pipe.WriteAsync(new byte[] { 10, 0 }); // truncated header, then drop
        }
        await using var silent = await ConnectAsync(); // holds a connection without sending (times out server-side)
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Client().GetSessionStatusAsync()));
        Assert.All(results, r => Assert.False(r.Active.HasSession));
    }

    [Fact]
    public async Task Missing_service_is_reported_as_unavailable()
    {
        var client = new NightshiftClient("nightshift-missing-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.GetSessionStatusAsync());
    }

    [Fact]
    public async Task Internal_errors_do_not_leak_details()
    {
        _f.Caps.Caps = null!; // provoke a NullReferenceException inside the engine
        var response = await RawAsync(Encoding.UTF8.GetBytes(
            """{"version":1,"requestId":"a1","command":"START_SESSION","payload":{"mode":"unlimited","ventilationAcknowledged":true}}"""));
        Assert.Equal(EngineErrorCodes.InternalError, response!.Error!.Code);
        Assert.DoesNotContain("Null", response.Error.Message, StringComparison.Ordinal);
    }

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    private async Task<IpcResponse?> RawAsync(byte[] body)
    {
        await using var pipe = await ConnectAsync();
        await FrameCodec.WriteAsync(pipe, body, CancellationToken.None);
        var frame = await FrameCodec.ReadAsync(pipe, IpcDefaults.MaxFrameBytes, CancellationToken.None);
        return JsonSerializer.Deserialize<IpcResponse>(frame!, IpcJson.Options);
    }

    private sealed class FixedIdentity(ClientIdentity? identity) : IClientIdentityResolver
    {
        public ClientIdentity? Current { get; set; } = identity;

        public ClientIdentity? Resolve(NamedPipeServerStream pipe) => Current;
    }
}

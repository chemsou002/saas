using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;
using Nightshift.Core.Recovery;
using Nightshift.Ipc;
using Nightshift.Storage;
using Nightshift.Windows;

namespace Nightshift.Windows.IntegrationTests;

/// <summary>Runs only on Windows; elsewhere every test is reported as skipped.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}

/// <summary>
/// Real Windows API tests. They briefly modify the ACTIVE power scheme's display timeout and restore it.
/// They prove the API calls work on this Windows build; they say nothing about closed-lid behaviour on a laptop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightshift-wintest-" + Guid.NewGuid().ToString("N"));

    public WindowsIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [WindowsFact]
    public void Reads_power_capabilities_and_status()
    {
        var info = new WindowsSystemInfo();
        var caps = info.Get();
        Assert.True(caps.OsBuild >= 10240);
        var status = ((IPowerStatusProvider)info).Get();
        Assert.NotNull(status);
    }

    [WindowsFact]
    public void Configurator_applies_and_restores_a_real_setting_through_sqlite_recovery()
    {
        var store = new WindowsPowerSettingsStore();
        var scheme = store.GetActiveSchemeId();
        var key = PowerSettingKeys.VideoPowerDownTimeout;
        var original = store.Read(scheme, key, PowerSource.AC);
        Assert.NotNull(original);
        var target = original == 1234u ? 1235u : 1234u;

        var recovery = new SqliteRecoveryStore(new Database(Path.Combine(_dir, "t.db")));
        var configurator = new PowerConfigurator(store, recovery);
        var record = configurator.Apply(new RecoveryRecord { SessionId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, OwnerProcessId = Environment.ProcessId },
            [new PlannedChange(key, PowerSource.AC, target)]);
        try
        {
            Assert.Equal(target, store.Read(scheme, key, PowerSource.AC));
            Assert.True(recovery.Exists);
        }
        finally
        {
            var report = configurator.Restore(record);
            Assert.True(report.Success);
        }
        Assert.Equal(original, store.Read(scheme, key, PowerSource.AC));
        Assert.False(recovery.Exists);
    }

    [WindowsFact]
    public void Keep_awake_request_can_be_acquired_and_released()
    {
        using var request = new WindowsKeepAwakeRequest();
        request.Acquire();
        Assert.True(request.IsHeld);
        request.Release();
        Assert.False(request.IsHeld);
    }

    [WindowsFact]
    public void Callback_power_notifications_register_without_a_window()
    {
        using var source = new CallbackPowerEventSource();
        source.Start();
        // Registration of the console-display and power-source notifications must work on any Windows 10/11.
        Assert.DoesNotContain(source.RegistrationErrors, e => e.StartsWith("power source", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task Secured_pipe_resolves_the_real_caller_identity()
    {
        var pipeName = "nightshift-wintest-" + Guid.NewGuid().ToString("N")[..8];
        var resolver = new CapturingResolver();
        var handler = new EchoHandler();
        await using var server = new IpcServer(new IpcServerOptions { PipeName = pipeName }, handler, resolver);
        server.Start();

        var response = await new NightshiftClient(pipeName).SendAsync(new IpcRequest { Command = IpcCommands.GetSessionStatus });

        Assert.True(response.Ok);
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(me.User!.Value, resolver.Last!.UserSid);
        Assert.Equal(new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator), resolver.Last.IsAdministrator);
    }

    [WindowsFact]
    public void Pipe_name_squatting_is_detected()
    {
        var pipeName = "nightshift-wintest-" + Guid.NewGuid().ToString("N")[..8];
        using var squatter = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4);
        var server = new IpcServer(new IpcServerOptions { PipeName = pipeName }, new EchoHandler(), new WindowsPipeClientIdentityResolver());
        // FILE_FLAG_FIRST_PIPE_INSTANCE fails with ERROR_ACCESS_DENIED when the name already exists.
        var ex = Record.Exception(server.Start);
        Assert.True(ex is IOException or UnauthorizedAccessException, $"Expected the start to fail, got {ex?.GetType().Name ?? "no exception"}");
    }

    [WindowsFact]
    public void Pipe_acl_denies_network_and_does_not_let_users_create_instances()
    {
        var rules = IpcServer.BuildPipeSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        Assert.Contains(rules, r => r.IdentityReference == network && r.AccessControlType == AccessControlType.Deny);
        var user = Assert.Single(rules, r => r.IdentityReference == interactive);
        Assert.Equal(0, (int)(user.PipeAccessRights & PipeAccessRights.CreateNewInstance));
        Assert.DoesNotContain(rules, r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.WorldSid, null));
    }

    private sealed class CapturingResolver : IClientIdentityResolver
    {
        private readonly WindowsPipeClientIdentityResolver _inner = new();

        public ClientIdentity? Last { get; private set; }

        public ClientIdentity? Resolve(NamedPipeServerStream pipe) => Last = _inner.Resolve(pipe);
    }

    private sealed class EchoHandler : IIpcRequestHandler
    {
        public IpcResponse Handle(ReadOnlySpan<byte> requestJson, ClientIdentity caller) =>
            new() { RequestId = "x", Ok = true, Result = System.Text.Json.JsonSerializer.SerializeToElement(new { caller.UserName }) };
    }
}

using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;
using Nightshift.Core.Recovery;
using Nightshift.Ipc;
using Nightshift.Storage;
using Nightshift.Windows;

namespace Nightshift.Service;

/// <summary>Production composition: real Windows power APIs, SQLite in the secured data directory, ACL'd named pipe.</summary>
[SupportedOSPlatform("windows")]
public static class ServiceComposition
{
    public static void AddNightshiftService(this IServiceCollection s)
    {
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<ServiceSettings>>().Value;
            var dir = settings.ResolveDataDirectory();
            DataDirectorySecurity.EnsureSecure(dir, sp.GetRequiredService<ILoggerFactory>().CreateLogger("DataDirectory"));
            return new Database(Path.Combine(dir, "nightshift.db"));
        });
        s.AddSingleton<ISessionRepository>(sp => new SqliteSessionRepository(sp.GetRequiredService<Database>(), sp.GetRequiredService<TimeProvider>()));
        s.AddSingleton<IRecoveryStore>(sp => new SqliteRecoveryStore(sp.GetRequiredService<Database>(), sp.GetRequiredService<TimeProvider>()));
        s.AddSingleton<IPowerSettingsStore, WindowsPowerSettingsStore>();
        s.AddSingleton<IKeepAwakeRequest, WindowsKeepAwakeRequest>();
        s.AddSingleton<WindowsSystemInfo>();
        s.AddSingleton<ISystemCapabilitiesProvider>(sp => sp.GetRequiredService<WindowsSystemInfo>());
        s.AddSingleton<IPowerStatusProvider>(sp => sp.GetRequiredService<WindowsSystemInfo>());
        s.AddSingleton<IPowerEventSource>(sp => new CallbackPowerEventSource(sp.GetRequiredService<TimeProvider>()));
        s.AddSingleton<ProcessProbe>();
        s.AddSingleton<ServiceHealth>();
        s.AddSingleton(sp => new SessionManager(new SessionManagerDependencies
        {
            Settings = sp.GetRequiredService<IPowerSettingsStore>(),
            KeepAwake = sp.GetRequiredService<IKeepAwakeRequest>(),
            Capabilities = sp.GetRequiredService<ISystemCapabilitiesProvider>(),
            PowerStatus = sp.GetRequiredService<IPowerStatusProvider>(),
            Recovery = sp.GetRequiredService<IRecoveryStore>(),
            Repository = sp.GetRequiredService<ISessionRepository>(),
            Time = sp.GetRequiredService<TimeProvider>(),
            IsProcessAlive = sp.GetRequiredService<ProcessProbe>().IsAlive,
        }, sp.GetRequiredService<IOptions<ServiceSettings>>().Value.ToEngineOptions(), sp.GetRequiredService<ILogger<SessionManager>>()));
        s.AddSingleton<IIpcRequestHandler>(sp =>
        {
            var manager = sp.GetRequiredService<SessionManager>();
            var health = sp.GetRequiredService<ServiceHealth>();
            var tick = TimeSpan.FromMilliseconds(sp.GetRequiredService<IOptions<ServiceSettings>>().Value.TickMilliseconds);
            return new SessionCommandHandler(manager,
                () => ServiceHealth.BuildSystemStatus(manager, health, sp.GetRequiredService<ISystemCapabilitiesProvider>(),
                    sp.GetRequiredService<IPowerStatusProvider>(), sp.GetRequiredService<IPowerSettingsStore>(), tick),
                sp.GetRequiredService<ILogger<SessionCommandHandler>>());
        });
        s.AddSingleton<IClientIdentityResolver, WindowsPipeClientIdentityResolver>();
        s.AddSingleton(sp => new IpcServer(
            new IpcServerOptions { PipeName = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.PipeName },
            sp.GetRequiredService<IIpcRequestHandler>(),
            sp.GetRequiredService<IClientIdentityResolver>(),
            sp.GetRequiredService<ILogger<IpcServer>>()));
        s.AddHostedService<SessionWorker>();
    }
}

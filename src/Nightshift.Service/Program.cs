using Nightshift.Service;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Nightshift.Service runs on Windows 10/11 only.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceSettings.ServiceName);
builder.Services.Configure<ServiceSettings>(builder.Configuration.GetSection("Nightshift"));
builder.Services.Configure<HostOptions>(o =>
{
    // Enough time to restore settings during service stop / OS shutdown.
    o.ShutdownTimeout = TimeSpan.FromSeconds(20);
    // A crash of the worker stops the host; the SCM restarts the service and startup recovery restores settings.
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

builder.Services.AddNightshiftService();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;

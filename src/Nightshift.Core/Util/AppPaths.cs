namespace Nightshift.Core.Util;

/// <summary>Per-user data locations. Phase 1 runs as the interactive user, so state lives in LocalAppData.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Nightshift");
    }

    public string Root { get; }
    public string RecoveryFile => Path.Combine(Root, "recovery.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string LogDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}

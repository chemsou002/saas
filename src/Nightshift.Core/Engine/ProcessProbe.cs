using System.Diagnostics;

namespace Nightshift.Core.Engine;

/// <summary>
/// Read-only liveness checks. Keeps the Process object so a recycled PID is not mistaken for the original.
/// Never kills, suspends or injects into anything.
/// </summary>
public sealed class ProcessProbe
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Process?> _processes = [];

    public bool IsAlive(int pid)
    {
        lock (_gate)
        {
            if (!_processes.TryGetValue(pid, out var p))
            {
                try
                {
                    p = Process.GetProcessById(pid);
                }
                catch (ArgumentException)
                {
                    p = null;
                }
                _processes[pid] = p;
            }
            try
            {
                return p is not null && !p.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}

using System.Diagnostics;
using System.Text.Json;

namespace QDNH.Hosting;

public sealed record LocalHealthSnapshot(string State, bool SerialExpected, bool SerialConnected,
    bool CaptureExpected, bool CaptureInitialized, bool PlaybackExpected, bool PlaybackInitialized,
    int Operators, bool TxActive, bool CaptureRecent = false, bool SerialReadResponsive = false);
public sealed record HostHeartbeat(int Protocol, int ProcessId, long ProcessStartTimeUtcTicks, long Sequence,
    DateTime UtcTimestamp, string State, LocalHealthSnapshot Health);

/// <summary>Sonda local sin red ni RF. Debe atravesar la autoridad real de sesiones, no solo un timer independiente.</summary>
public sealed class ServerHeartbeat : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    public ServerHeartbeat(string path, Func<LocalHealthSnapshot> probe, TimeSpan? interval = null)
    {
        path = Path.GetFullPath(path);
        var process = Process.GetCurrentProcess(); int pid = process.Id;
        long started = process.StartTime.ToUniversalTime().Ticks;
        worker = Task.Run(async () =>
        {
            long sequence = 0;
            while (!stop.IsCancellationRequested)
            {
                // Si la autoridad de control queda bloqueada, tampoco avanza este heartbeat.
                var health = probe();
                var heartbeat = new HostHeartbeat(1, pid, started, ++sequence, DateTime.UtcNow, health.State, health);
                try { Publish(path, heartbeat); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Settings.Vars.Err("No se pudo actualizar la sonda local de salud."); }
                try { await Task.Delay(interval ?? TimeSpan.FromSeconds(1), stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }
    public static ServerHeartbeat? StartFromEnvironment(Func<LocalHealthSnapshot> probe)
    {
        string? path = Environment.GetEnvironmentVariable("QDNH_HEARTBEAT_FILE");
        return string.IsNullOrWhiteSpace(path) ? null : new ServerHeartbeat(path, probe);
    }
    public static void Publish(string path, HostHeartbeat heartbeat)
    {
        string temporary = path + "." + heartbeat.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(heartbeat));
        File.Move(temporary, path, true);
    }
    public void Dispose()
    {
        stop.Cancel();
        // Un probe bloqueado no debe impedir la salida del proceso.
        try { worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}

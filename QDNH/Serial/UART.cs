using System.Diagnostics;
using System.IO.Ports;
using QDNH.Settings;
namespace QDNH.Serial;
public interface ISerialConnection : IDisposable
{
    bool HasReadTimeout => true;
    int Read(byte[] buffer);
    void Write(byte[] buffer, int offset, int length);
}
public sealed class UART : IDisposable
{
    private readonly object gate = new();
    private readonly Func<ISerialConnection> open;
    private readonly Action<byte[], int> receive;
    private readonly Action? disconnected;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private ISerialConnection? connection;
    private bool closed;
    private long lastReadCycle = Stopwatch.GetTimestamp();
    private readonly int retryMilliseconds;
    public bool IsConnected { get { lock (gate) return !closed && connection != null; } }
    public bool ReadResponsive
    {
        get
        {
            lock (gate) return !closed && connection != null && (!connection.HasReadTimeout ||
                (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastReadCycle)) / (double)Stopwatch.Frequency < 10);
        }
    }
    public UART(string name, Action<byte[], int> receive, Action? disconnected = null)
        : this(() => new DeviceConnection(name), receive, disconnected) { }
    public UART(Func<ISerialConnection> open, Action<byte[], int> receive, Action? disconnected = null, int retryMilliseconds = 2000)
    {
        this.open = open; this.receive = receive; this.disconnected = disconnected;
        this.retryMilliseconds = retryMilliseconds;
        connection = open();
        try
        {
            var release = RadioProtocol.ReleasePtt();
            connection.Write(release, 0, release.Length);
        }
        catch { connection.Dispose(); throw; }
        loop = Task.Run(Loop);
    }
    public void Send(byte[] data, int offset, int length)
    {
        ISerialConnection? failed = null;
        lock (gate)
        {
            if (closed || connection == null) return;
            try { connection.Write(data, offset, length); } catch { failed = connection; }
        }
        if (failed != null) Lose(failed);
    }
    private void Lose(ISerialConnection failed)
    {
        lock (gate)
        {
            if (closed || !ReferenceEquals(connection, failed)) return;
            connection = null;
            try { failed.Dispose(); } catch { }
        }
        // Fuera del cerrojo: el cierre de TX puede intentar escribir ReleaseKey.
        disconnected?.Invoke();
        Vars.Err("Conexion serie perdida; reintentando sin reiniciar QDNH.");
    }
    private async Task Loop()
    {
        byte[] buffer = new byte[512];
        while (!stop.IsCancellationRequested)
        {
            ISerialConnection? current;
            lock (gate) current = connection;
            if (current == null)
            {
                try
                {
                    var replacement = open();
                    lock (gate)
                    {
                        if (closed) { replacement.Dispose(); return; }
                        // PTT OFF antes de publicar el puerto recuperado.
                        var release = RadioProtocol.ReleasePtt();
                        try { replacement.Write(release, 0, release.Length); }
                        catch { replacement.Dispose(); throw; }
                        connection = replacement;
                    }
                    Vars.Out("Puerto serie recuperado; PTT liberado.");
                }
                catch { try { await Task.Delay(retryMilliseconds, stop.Token); } catch (OperationCanceledException) { } }
                continue;
            }
            try
            {
                Interlocked.Exchange(ref lastReadCycle, Stopwatch.GetTimestamp());
                int count = current.Read(buffer);
                if (count <= 0) { Lose(current); continue; }
                try { receive(buffer.AsSpan(0, count).ToArray(), count); }
                catch (Exception ex) { Vars.Err($"Error procesando datos serie: {ex.Message}"); }
            }
            catch (TimeoutException) { }
            catch { Lose(current); }
        }
    }
    public void Close() => Dispose();
    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true; stop.Cancel();
            try { connection?.Dispose(); } catch { }
            connection = null;
        }
        try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
    private sealed class DeviceConnection : ISerialConnection
    {
        private readonly SerialPort? port;
        private readonly FileStream? file;
        public bool HasReadTimeout => port != null;
        public DeviceConnection(string name)
        {
            if (name.StartsWith("tty")) file = File.Open($"/dev/{name}", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            else
            {
                port = new SerialPort(name, 38400, Parity.None, 8, StopBits.One) { ReadTimeout = 500, WriteTimeout = 500 };
                try { port.Open(); } catch { port.Dispose(); throw; }
            }
        }
        public int Read(byte[] buffer) => port != null ? port.Read(buffer, 0, buffer.Length) : file!.Read(buffer);
        public void Write(byte[] buffer, int offset, int length)
        {
            if (port != null) port.Write(buffer, offset, length);
            else { file!.Write(buffer, offset, length); file.Flush(); }
        }
        public void Dispose() { port?.Dispose(); file?.Dispose(); }
    }
}

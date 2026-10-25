using QDNH.Serial;

namespace QDNH.SharedRadio;

/// <summary>Read-only capture of the firmware's EF + 1024-byte LCD dump.
/// UART writes are supplied only by SessionManager while its control/TX lock is held.
/// Raw dump bytes never enter legacy clients' semantic UI byte streams.</summary>
public sealed class ScreenSnapshotBroker : IDisposable
{
    private readonly object gate = new();
    private readonly Func<DateTime> now;
    private readonly Timer? timer;
    private readonly Dictionary<Guid, (long Id, Action<ScreenSnapshotStateMessage> Reply)> pending = new();
    private readonly Dictionary<Guid, DateTime> lastRequests = new();
    private DateTime requestedAt, capturedAt;
    private byte[]? cached;
    private readonly byte[] bitmap = new byte[1024];
    private int bitmapCount = -1, remaining, state, uiType, binaryLength, binaryIdLow;
    private bool disposed, readIssued;
    public ScreenSnapshotBroker(Func<DateTime>? clock = null, bool startTimer = true)
    {
        now = clock ?? (() => DateTime.UtcNow);
        if (startTimer) timer = new Timer(_ => Tick(), null, 250, 250);
    }
    public void Request(Guid session, long requestId, Action<ScreenSnapshotStateMessage> reply, Action<byte[]> write)
    {
        ScreenSnapshotStateMessage? immediate = null;
        lock (gate)
        {
            DateTime stamp = now();
            if (disposed) immediate = Failure(requestId, "serial-unavailable");
            else if (requestId <= 0) immediate = Failure(requestId, "invalid-request");
            else if (lastRequests.TryGetValue(session, out var last) && (stamp - last).TotalSeconds < 1)
                immediate = Failure(requestId, "rate-limited");
            else
            {
                lastRequests[session] = stamp;
                if (cached != null && (stamp - capturedAt).TotalMilliseconds <= 500)
                    immediate = Success(requestId, cached, capturedAt);
                else if ((pending.Count == 0 && readIssued) || (pending.Count >= 64 && !pending.ContainsKey(session))) immediate = Failure(requestId, "busy");
                else
                {
                    bool issue = pending.Count == 0;
                    pending[session] = (requestId, reply);
                    if (issue)
                    {
                        requestedAt = stamp; readIssued = true;
                        try { write(RadioProtocol.ReadScreen()); }
                        catch { pending.Remove(session); immediate = Failure(requestId, "serial-unavailable"); }
                    }
                }
            }
        }
        if (immediate != null) SafeReply(reply, immediate);
    }
    public static ScreenSnapshotStateMessage Failure(long id, string reason) => new(id, 128, 64, Array.Empty<byte>(), null, reason);
    private static ScreenSnapshotStateMessage Success(long id, byte[] data, DateTime stamp) => new(id, 128, 64, data.ToArray(), stamp.ToString("o"), null);
    private static void SafeReply(Action<ScreenSnapshotStateMessage> reply, ScreenSnapshotStateMessage value) { try { reply(value); } catch { } }
    public void InvalidateCache() { lock (gate) cached = null; }
    public void Forget(Guid session) { lock (gate) { pending.Remove(session); lastRequests.Remove(session); } }
    public void Reset()
    {
        List<(long Id, Action<ScreenSnapshotStateMessage> Reply)> failed;
        lock (gate)
        {
            failed = pending.Values.ToList(); pending.Clear(); lastRequests.Clear(); cached = null;
            bitmapCount = -1; state = remaining = 0; readIssued = false;
        }
        foreach (var item in failed) SafeReply(item.Reply, Failure(item.Id, "serial-unavailable"));
    }
    public void Tick()
    {
        List<(long Id, Action<ScreenSnapshotStateMessage> Reply)> failed = new();
        lock (gate)
        {
            DateTime stamp = now();
            if (pending.Count > 0 && (stamp - requestedAt).TotalSeconds >= 3)
            { failed = pending.Values.ToList(); pending.Clear(); }
            // A timed-out request may still return EF much later (including
            // after a paused UART callback resumes). Keep readIssued until
            // EF + 1024 bytes are drained, or UART loss resets framing.
            foreach (var id in lastRequests.Where(x => (stamp - x.Value).TotalMinutes > 1).Select(x => x.Key).ToArray()) lastRequests.Remove(id);
        }
        foreach (var item in failed) SafeReply(item.Reply, Failure(item.Id, "no-data"));
    }
    /// <returns>Unmodified ordinary UART bytes and whether a firmware scan reply was observed.</returns>
    public (byte[] Forward, bool ScanReply) Feed(byte[] data, int length)
    {
        if (length < 0 || length > data.Length) throw new ArgumentOutOfRangeException(nameof(length));
        List<byte> output = new(length);
        List<(long Id, Action<ScreenSnapshotStateMessage> Reply)> completed = new();
        byte[]? ready = null; DateTime stamp = default; bool scan = false;
        lock (gate)
        {
            for (int i = 0; i < length; i++)
            {
                byte b = data[i];
                if (bitmapCount >= 0)
                {
                    bitmap[bitmapCount++] = b;
                    if (bitmapCount == 1024)
                    {
                        ready = bitmap.ToArray(); cached = ready; capturedAt = stamp = now(); bitmapCount = -1;
                        completed.AddRange(pending.Values); pending.Clear(); readIssued = false;
                    }
                    continue;
                }
                if (remaining > 0) { output.Add(b); remaining--; continue; }
                // Header tracking prevents EF in binary/UI payloads being confused with a dump marker.
                if (state != 0)
                {
                    output.Add(b);
                    switch (state)
                    {
                        case 1: uiType = b; state = 2; break;
                        case 2: state = 3; break;
                        case 3: state = 4; break;
                        case 4: state = 5; break;
                        case 5: remaining = uiType == 6 ? 0 : b; state = 0; break;
                        case 6: state = b == 0xcd ? 7 : 0; break;
                        case 7: binaryLength = b; state = 8; break;
                        case 8:
                            binaryLength |= b << 8;
                            if (binaryLength > 4096 || binaryLength < 2) state = 0;
                            else state = 9;
                            break;
                        case 9: binaryIdLow = b ^ 0x16; state = 10; break;
                        case 10:
                            if ((binaryIdLow | ((b ^ 0x6c) << 8)) == 0x0908) scan = true;
                            remaining = binaryLength - 2 + 4; state = 0; break;
                    }
                    continue;
                }
                if (b == 0xef && readIssued)
                { bitmapCount = 0; continue; }
                output.Add(b);
                if (b == 0xb5) state = 1;
                else if (b == 0xab) state = 6;
            }
        }
        if (output.Count > 0) InvalidateCache(); // A live UI/telemetry update must not replay an earlier framebuffer.
        if (ready != null) foreach (var item in completed) SafeReply(item.Reply, Success(item.Id, ready, stamp));
        return (output.ToArray(), scan);
    }
    public void Dispose() { timer?.Dispose(); Reset(); lock (gate) disposed = true; }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QDNH.Serial;
using Xunit;
namespace QDNH.Tests;
public class UartRecoveryTests
{
    private sealed class FakePort : ISerialConnection
    {
        public readonly BlockingCollection<byte[]> Reads = new();
        public readonly ConcurrentQueue<byte[]> Writes = new();
        public bool Disposed;
        public int Read(byte[] buffer)
        {
            if (!Reads.TryTake(out var data, 100)) throw new TimeoutException();
            if (data.Length == 0) throw new IOException("USB perdido");
            data.CopyTo(buffer, 0); return data.Length;
        }
        public void Write(byte[] data, int offset, int length)
        {
            if (Disposed) throw new ObjectDisposedException("puerto");
            Writes.Enqueue(data.AsSpan(offset, length).ToArray());
        }
        public void Dispose() => Disposed = true;
    }
    [Fact]
    public async Task UsbLossReopensAndSendsReleaseBeforeUserData()
    {
        var first = new FakePort(); var second = new FakePort();
        int opens = 0, lost = 0, received = 0;
        using var uart = new UART(() => Interlocked.Increment(ref opens) == 1 ? first : second,
            (_, count) => Interlocked.Add(ref received, count), () => Interlocked.Increment(ref lost), 10);
        first.Reads.Add(Array.Empty<byte>());
        Assert.True(SpinWait.SpinUntil(() => !second.Writes.IsEmpty, 3000));
        uart.Send(new byte[] { 1, 2 }, 0, 2);
        Assert.True(second.Writes.TryDequeue(out var release));
        Assert.Equal(RadioProtocol.ReleasePtt(), release);
        Assert.Equal(1, lost); Assert.True(first.Disposed);
        second.Reads.Add(new byte[] { 9, 8 });
        Assert.True(SpinWait.SpinUntil(() => received == 2, 2000));
        uart.Close(); int previous = opens;
        await Task.Delay(50);
        Assert.Equal(previous, opens); Assert.False(uart.IsConnected);
    }
    [Fact]
    public async Task CloseDuringReopenDisposesUnpublishedPort()
    {
        var first = new FakePort(); var replacement = new FakePort();
        using var opening = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        int count = 0;
        using var uart = new UART(() =>
        {
            if (Interlocked.Increment(ref count) == 1) return first;
            opening.Set(); resume.Wait(); return replacement;
        }, (_, _) => { }, retryMilliseconds: 10);
        first.Reads.Add(Array.Empty<byte>());
        Assert.True(opening.Wait(2000));
        uart.Close();
        resume.Set();
        await Task.Delay(50);
        Assert.True(SpinWait.SpinUntil(() => replacement.Disposed, 2000)); Assert.Empty(replacement.Writes);
    }
}

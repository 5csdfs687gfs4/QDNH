using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QDNH.Hosting;
using Xunit;
namespace QDNH.Tests;
public class HostHeartbeatTests
{
    [Fact]
    public async Task HeartbeatAdvancesThroughProbeAndFreezesWhenProbeAuthorityBlocks()
    {
        string path = Path.Combine(Path.GetTempPath(), "qdnh-heartbeat-" + Guid.NewGuid() + ".json");
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        int count = 0;
        var worker = new ServerHeartbeat(path, () =>
        {
            if (Interlocked.Increment(ref count) >= 2) { entered.Set(); resume.Wait(); }
            return new("Running", false, false, false, false, false, false, 0, false);
        }, TimeSpan.FromMilliseconds(20));
        try
        {
            Assert.True(entered.Wait(2000));
            string first = await File.ReadAllTextAsync(path);
            var heartbeat = JsonSerializer.Deserialize<HostHeartbeat>(first);
            Assert.Equal(Environment.ProcessId, heartbeat!.ProcessId); Assert.Equal(1, heartbeat.Sequence);
            await Task.Delay(100);
            Assert.Equal(first, await File.ReadAllTextAsync(path));
            resume.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { return JsonSerializer.Deserialize<HostHeartbeat>(File.ReadAllText(path))!.Sequence > 1; } catch (IOException) { return false; }
            }, 2000));
        }
        finally { resume.Set(); worker.Dispose(); if (File.Exists(path)) File.Delete(path); }
    }
}

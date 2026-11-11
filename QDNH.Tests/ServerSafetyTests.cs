using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using QDNH.Serial;
using QDNH.SharedRadio;
using Xunit;
namespace QDNH.Tests;
public class ServerSafetyTests
{
    [Theory]
    [InlineData("control")]
    [InlineData("audio")]
    [InlineData("serial")]
    [InlineData("release")]
    [InlineData("lease")]
    [InlineData("maximum")]
    public void EveryTxLossReleasesPhysicalPttExactlyOnce(string cause)
    {
        int releases = 0;
        var now = DateTime.UtcNow;
        using var manager = new SessionManager(() => releases++, () => now);
        var id = Guid.NewGuid();
        Action<byte[]> control = _ => { };
        Action<byte[], int> audio = (_, _) => { };
        Action<byte[], int> serial = (_, _) => { };
        manager.RegisterOrAttach(id, "EA1TEST", true, control);
        manager.AttachAudio(id, audio); manager.AttachSerial(id, serial);
        manager.TryRequestControl(id); manager.TryRequestTx(id, 145.5, "FM");
        switch (cause)
        {
            case "control": manager.OnControlChannelClosed(id, control); break;
            case "audio": manager.OnAudioChannelClosed(id, audio); break;
            case "serial": manager.OnSerialChannelClosed(id, serial); break;
            case "release": manager.ReleaseControl(id); break;
            case "lease": now = now.AddSeconds(121); manager.CheckTxLease(); break;
            case "maximum":
                for (int i = 0; i < 1801; i++) { now = now.AddSeconds(1); manager.OnTxAudio(id, new byte[2], 2); }
                manager.TryRequestTx(id, 145.5, "FM"); // Repetir RequestTx no reinicia el límite.
                manager.CheckTxLease(); break;
        }
        Assert.Null(manager.TxOwnerSessionId);
        Assert.Equal(1, releases);
        manager.ReleaseTx(id, "otra vez");
        Assert.Equal(1, releases);
    }
    [Fact]
    public void SerialFailureDuringPhysicalReleaseDoesNotReenterRelease()
    {
        SessionManager? manager = null; int releases = 0;
        manager = new SessionManager(() => { releases++; manager!.ReleaseAnyTx("SERIAL_LOST"); });
        using (manager)
        {
            Guid id = Guid.NewGuid(); manager.RegisterOrAttach(id, "TEST", true, _ => { });
            manager.TryRequestControl(id); manager.TryRequestTx(id, 145.5, "FM");
            manager.ReleaseTx(id, "PTT_OFF");
            Assert.Equal(1, releases); Assert.Null(manager.TxOwnerSessionId);
        }
    }
    [Fact]
    public async System.Threading.Tasks.Task ReleaseWaitsForAcceptedAudioAndOldOwnerCannotWriteAfterward()
    {
        using var entered = new System.Threading.ManualResetEventSlim();
        using var resume = new System.Threading.ManualResetEventSlim();
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var manager = new SessionManager(() => order.Enqueue("release-and-clear"));
        Guid old = Guid.NewGuid(), next = Guid.NewGuid();
        manager.RegisterOrAttach(old, "OLD", true, _ => { }); manager.RegisterOrAttach(next, "NEXT", true, _ => { });
        manager.TryRequestControl(old); manager.TryRequestTx(old, 145.5, "FM");
        var forwarding = System.Threading.Tasks.Task.Run(() => manager.ForwardTxAudio(old, new byte[2], 2, (_, _) =>
        { entered.Set(); resume.Wait(); order.Enqueue("old-audio"); }));
        Assert.True(entered.Wait(2000));
        var releasing = System.Threading.Tasks.Task.Run(() => manager.ReleaseControl(old));
        await System.Threading.Tasks.Task.Delay(100);
        Assert.False(releasing.IsCompleted); // La cesión no puede adelantarse al sink admitido.
        resume.Set(); await forwarding; await releasing;
        manager.TryRequestControl(next); manager.TryRequestTx(next, 145.5, "FM");
        Assert.False(manager.ForwardTxAudio(old, new byte[2], 2, (_, _) => order.Enqueue("stale-audio")));
        Assert.True(manager.ForwardTxAudio(next, new byte[2], 2, (_, _) => order.Enqueue("new-audio")));
        Assert.Equal(new[] { "old-audio", "release-and-clear", "new-audio" }, order.ToArray());
    }
    [Fact]
    public void ReleaseOutputFailureBlocksNewTxUntilReconfiguration()
    {
        using var manager = new SessionManager(() => throw new System.IO.IOException("Fallo de salida simulado"));
        Guid id = Guid.NewGuid(); manager.RegisterOrAttach(id, "TEST", true, _ => { });
        manager.TryRequestControl(id); manager.TryRequestTx(id, 145.5, "FM");
        manager.ReleaseTx(id, "PTT_OFF"); manager.TryRequestTx(id, 145.5, "FM");
        Assert.Null(manager.TxOwnerSessionId);
        manager.ResetForReconfigure(false); manager.RegisterOrAttach(id, "TEST", true, _ => { });
        manager.TryRequestControl(id); manager.TryRequestTx(id, 145.5, "FM");
        Assert.Equal(id, manager.TxOwnerSessionId);
    }
    [Theory]
    [InlineData(16)]
    [InlineData(48)]
    [InlineData(80)]
    [InlineData(240)]
    [InlineData(272)]
    public void FragmentedPttNeedsTxPermissionAndControlCannotBeStolen(int key)
    {
        using var manager = new SessionManager();
        Guid owner = Guid.NewGuid(), listener = Guid.NewGuid();
        manager.RegisterOrAttach(owner, "OWNER", true, _ => { });
        manager.RegisterOrAttach(listener, "LISTENER", true, _ => { });
        manager.TryRequestControl(owner); manager.TryRequestControl(listener);
        Assert.True(manager.IsControlOwner(owner));
        var forwarded = new List<byte[]>();
        Action<byte[], int, int> send = (data, _, _) => forwarded.Add(data);
        byte[] ptt = RadioProtocol.Key((ushort)key);
        manager.ForwardSerial(owner, ptt[..5], 5, send);
        manager.ForwardSerial(owner, ptt[5..], ptt.Length - 5, send);
        Assert.Empty(forwarded);
        manager.TryRequestTx(owner, 145.5, "FM");
        manager.ForwardSerial(owner, ptt, ptt.Length, send);
        Assert.Single(forwarded);
        manager.ReleaseTx(owner, "timeout");
        manager.ForwardSerial(owner, ptt, ptt.Length, send);
        Assert.Single(forwarded);
        byte[] release = RadioProtocol.ReleasePtt();
        manager.ForwardSerial(owner, release, release.Length, send);
        Assert.Equal(2, forwarded.Count);
    }
    [Fact]
    public void MonitorExcludesTransmitterAndDoesNotMixRx()
    {
        using var manager = new SessionManager();
        Guid owner = Guid.NewGuid(), listener = Guid.NewGuid();
        int ownerAudio = 0, monitorAudio = 0;
        manager.RegisterOrAttach(owner, "OWNER", true, _ => { });
        manager.RegisterOrAttach(listener, "LISTENER", true, _ => { });
        manager.AttachAudio(owner, (_, _) => ownerAudio++);
        manager.AttachAudio(listener, (_, _) => monitorAudio++);
        manager.TryRequestControl(owner); manager.TryRequestTx(owner, 145.5, "FM");
        manager.FanOutRx(new byte[2], 2);
        manager.OnTxAudio(owner, new byte[2], 2);
        Assert.Equal(0, ownerAudio); Assert.Equal(1, monitorAudio);
        manager.ReleaseTx(owner, "PTT_OFF"); manager.FanOutRx(new byte[2], 2);
        Assert.Equal(1, ownerAudio); Assert.Equal(2, monitorAudio);
    }
    [Fact]
    public void LateMonitorReceivesCurrentTransmitter()
    {
        using var manager = new SessionManager(); Guid owner = Guid.NewGuid(), observer = Guid.NewGuid();
        manager.RegisterOrAttach(owner, "OWNER", true, _ => { }, 2);
        manager.TryRequestControl(owner); manager.TryRequestTx(owner, 145.5, "FM");
        manager.OnTxAudio(owner, new byte[2], 2);
        var frames = new List<byte[]>();
        manager.RegisterOrAttach(observer, "OBSERVER", false, frames.Add, 2);
        Assert.DoesNotContain(frames, f => f[0] == 11);
        manager.RegisterOrAttach(observer, "OBSERVER", true, frames.Add, 2);
        Assert.Contains(frames, f => f[0] == 11);
        Assert.Equal(owner, manager.TxOwnerSessionId);
    }
    [Fact]
    public void ExtensionsAreNegotiatedAndQueueDoesNotGrantControl()
    {
        using var manager = new SessionManager();
        var oldFrames = new List<byte[]>(); var modernFrames = new List<byte[]>();
        Guid old = Guid.NewGuid(), modern = Guid.NewGuid();
        manager.RegisterOrAttach(old, "OLD", false, oldFrames.Add);
        manager.RegisterOrAttach(modern, "NEW", true, modernFrames.Add, 2);
        manager.QueueControl(modern, true); manager.SendHealth(modern);
        Assert.DoesNotContain(oldFrames, f => f[0] >= 21);
        Assert.Contains(modernFrames, f => f[0] == 22);
        Assert.Null(manager.ControlOwnerSessionId);
        Assert.Equal(1, manager.GetOperators().Operators.Single(o => o.SessionId == modern).QueuePosition);
        manager.TryRequestControl(modern);
        Assert.Equal(0, manager.GetOperators().Operators.Single(o => o.SessionId == modern).QueuePosition);
    }
    [Fact]
    public void ReleasePacketMatchesIndependentClientCrcAndXor()
    {
        var packet = RadioProtocol.ReleasePtt();
        byte[] key = { 0x16, 0x6c, 0x14, 0xe6, 0x2e, 0x91, 0x0d, 0x40 };
        byte[] plain = packet.Skip(4).Take(8).Select((b, i) => (byte)(b ^ key[i])).ToArray();
        Assert.Equal(new byte[] { 1, 8, 2, 0, 19, 0 }, plain[..6]);
        ushort crc = 0;
        foreach (byte b in plain[..6])
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) == 0 ? crc << 1 : (crc << 1) ^ 0x1021);
        }
        Assert.Equal((byte)crc, plain[6]); Assert.Equal((byte)(crc >> 8), plain[7]);
    }
}

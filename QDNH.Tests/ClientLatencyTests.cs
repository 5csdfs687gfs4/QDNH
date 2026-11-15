using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using QDNH.SharedRadio;
using Xunit;

namespace QDNH.Tests;
public class ClientLatencyTests
{
    [Fact]
    public void TxResponsesEchoRequestIdentityAcrossReleaseAndNewGrant()
    {
        using var manager = new SessionManager(); var frames = new List<byte[]>(); var id = Guid.NewGuid();
        manager.RegisterOrAttach(id,"OWNER",true,frames.Add,2); manager.TryRequestControl(id);
        manager.TryRequestTx(id,145.5,"FM",41); manager.ReleaseTx(id,"PTT_OFF");
        manager.TryRequestTx(id,145.5,"FM",42);
        var grants=frames.Where(f=>f[0]==(byte)ControlMsgType.TxGranted).Select(f=>JsonSerializer.Deserialize<TxGrantedMessage>(f.AsSpan(5))!).ToArray();
        Assert.Equal(new long[]{41,42},grants.Select(g=>g.RequestId));
        var release=JsonSerializer.Deserialize<TxReleasedMessage>(frames.First(f=>f[0]==(byte)ControlMsgType.TxReleased).AsSpan(5))!;
        Assert.Equal(41,release.RequestId); Assert.Equal(id,release.SessionId);
        var other=Guid.NewGuid();var rejected=new List<byte[]>();manager.RegisterOrAttach(other,"OTHER",false,rejected.Add,2);
        manager.TryRequestTx(other,145.5,"FM",99);
        Assert.Equal(99,JsonSerializer.Deserialize<TxDeniedMessage>(rejected.Last(f=>f[0]==(byte)ControlMsgType.TxDenied).AsSpan(5))!.RequestId);
    }
    [Fact]
    public void ThreeClientsNegotiateIndependentlyAndDoNotChangeHardwareLatency()
    {
        using var manager = new SessionManager();
        var ids = Enumerable.Range(0,3).Select(_=>Guid.NewGuid()).ToArray();
        var replies = ids.ToDictionary(id=>id,_=>new List<byte[]>());
        foreach(var id in ids) manager.RegisterOrAttach(id,"TEST",true,f=>replies[id].Add(f),2);
        int original = QDNH.Settings.Vars.LatencyMils;
        for(int i=0;i<3;i++) manager.ReportPlayback(ids[i],new(1,100,80+i*20,30,0));
        for(int i=1;i<=8;i++) manager.ReportPlayback(ids[2],new(1+i,100+i,500,200,i));
        int Target(Guid id) => JsonSerializer.Deserialize<PlaybackTargetMessage>(replies[id].Last(f=>f[0]==(byte)ControlMsgType.PlaybackTarget).AsSpan(5))!.TargetMs;
        Assert.Equal(80,Target(ids[0])); Assert.Equal(100,Target(ids[1])); Assert.Equal(280,Target(ids[2]));
        Assert.Equal(original,QDNH.Settings.Vars.LatencyMils);
        Assert.Single(replies[ids[0]].Where(f=>f[0]==(byte)ControlMsgType.PlaybackTarget));
    }
    [Fact]
    public void LatencyBoundsAndGradualRecovery()
    {
        var policy=new ClientLatencyPolicy();
        Assert.Equal(500,policy.Observe(int.MaxValue,100,0));
        for(int i=0;i<5;i++) Assert.Equal(500,policy.Observe(40,50,0));
        Assert.Equal(490,policy.Observe(40,50,0));
        for(int i=0;i<10000;i++) policy.Observe(-100,0,0);
        Assert.Equal(40,policy.TargetMs);
    }
    [Fact]
    public void LegacyClientGetsNoNewUnsolicitedMessage()
    {
        using var manager=new SessionManager(); var frames=new List<byte[]>(); var id=Guid.NewGuid();
        manager.RegisterOrAttach(id,"LEGACY",false,frames.Add,1);
        manager.ReportPlayback(id,new(1,100,100,20,0));
        Assert.DoesNotContain(frames,f=>f[0]==(byte)ControlMsgType.PlaybackTarget);
    }
    [Fact]
    public void OldControlChannelCannotNegotiateForReplacement()
    {
        using var manager=new SessionManager(); var id=Guid.NewGuid();
        Action<byte[]> old=_=>{}, current=_=>{};
        manager.RegisterOrAttach(id,"TEST",true,old,2);
        manager.RegisterOrAttach(id,"TEST",true,current,2);
        bool executed=false;
        Assert.False(manager.ExecuteControl(id,old,()=>{executed=true;manager.ReportPlayback(id,new(1,0,500,500,1));}));
        Assert.False(executed);
    }
}

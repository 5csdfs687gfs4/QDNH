using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using QDNH.Serial;
using QDNH.SharedRadio;
using Xunit;

namespace QDNH.Tests;
public class ScreenSnapshotTests
{
    private static readonly byte[] Xor = { 0x16,0x6c,0x14,0xe6,0x2e,0x91,0x0d,0x40,0x21,0x35,0xd5,0x40,0x13,0x03,0xe9,0x80 };
    private static byte[] Frame(ushort command, int paramBytes = 0, ushort density = 0)
    {
        int length = 4 + paramBytes; byte[] frame = new byte[length + 8];
        frame[0]=0xab;frame[1]=0xcd;frame[2]=(byte)length;frame[3]=(byte)(length>>8);
        frame[4]=(byte)command;frame[5]=(byte)(command>>8);frame[6]=(byte)paramBytes;
        if(paramBytes>=10){frame[16]=(byte)density;frame[17]=(byte)(density>>8);}
        int crc=0;
        for(int i=4;i<length+4;i++){
            crc^=frame[i]<<8;for(int j=0;j<8;j++)crc=((crc<<1)^((crc&0x8000)!=0?0x1021:0))&0xffff;
            frame[i]^=Xor[(i-4)%16];
        }
        frame[length+4]=(byte)((byte)crc^Xor[length%16]);frame[length+5]=(byte)((crc>>8)^Xor[(length+1)%16]);
        frame[^2]=0xdc;frame[^1]=0xba;return frame;
    }
    [Fact] public void QueryMatchesExistingFirmwareFramingAndNeverContainsAKeyCommand()
    {
        Assert.Equal(Frame(0x0803,4),RadioProtocol.ReadScreen());
        Assert.True(RadioProtocol.IsValidFrame(RadioProtocol.ReadScreen()));
        Assert.Equal(0x0803,RadioProtocol.CommandId(RadioProtocol.ReadScreen()));
        Assert.False(RadioProtocol.IsPttOn(RadioProtocol.ReadScreen()));
    }
    [Theory][InlineData(1)][InlineData(7)][InlineData(512)][InlineData(2048)]
    public void FragmentedBitmapIsDeliveredOnlyToRequestersAndRemovedFromLegacySerial(int chunk)
    {
        var now=DateTime.UtcNow; using var broker=new ScreenSnapshotBroker(()=>now,false);
        var replies=new List<ScreenSnapshotStateMessage>();int writes=0;
        broker.Request(Guid.NewGuid(),4,replies.Add,_=>writes++);
        byte[] pixels=Enumerable.Range(0,1024).Select(i=>(byte)i).ToArray();
        byte[] before={0xb5,1,0,0,6,3,65,0xef,66}; // EF inside semantic text is not a dump.
        byte[] after={0xb5,6,132,0,0,216}; // battery's final byte is NOT a payload length.
        byte[] stream=before.Concat(new byte[]{0xef}).Concat(pixels).Concat(after).ToArray();
        var ordinary=new List<byte>();
        for(int i=0;i<stream.Length;i+=chunk){byte[] part=stream.Skip(i).Take(chunk).ToArray();ordinary.AddRange(broker.Feed(part,part.Length).Forward);}
        Assert.Equal(before.Concat(after),ordinary);Assert.Equal(1,writes);Assert.Single(replies);
        Assert.Equal(4,replies[0].RequestId);Assert.Null(replies[0].Reason);Assert.Equal(pixels,replies[0].Data);
    }
    [Fact] public void BinaryPayloadEfAndUnrequestedEfAreUnmodified()
    {
        var now=DateTime.UtcNow;using var broker=new ScreenSnapshotBroker(()=>now,false);
        var ordinary=new byte[]{1,0xef,2};Assert.Equal(ordinary,broker.Feed(ordinary,ordinary.Length).Forward);
        broker.Request(Guid.NewGuid(),1,_=>{},_=>{});
        byte[] framed=Frame(0x0951,10);framed[10]=0xef;
        Assert.Equal(framed,broker.Feed(framed,framed.Length).Forward);
    }
    [Fact] public void RequestsCoalesceAndOnlyFreshCacheCanBeReplayed()
    {
        var now=DateTime.UtcNow;using var broker=new ScreenSnapshotBroker(()=>now,false);
        var a=new List<ScreenSnapshotStateMessage>();var b=new List<ScreenSnapshotStateMessage>();int writes=0;
        broker.Request(Guid.NewGuid(),1,a.Add,_=>writes++);broker.Request(Guid.NewGuid(),2,b.Add,_=>writes++);
        byte[] dump=new byte[1025];dump[0]=0xef;broker.Feed(dump,dump.Length);
        Assert.Equal(1,writes);Assert.Single(a);Assert.Single(b);
        broker.Request(Guid.NewGuid(),3,a.Add,_=>writes++);Assert.Equal(1,writes);Assert.Equal(2,a.Count);
        now=now.AddMilliseconds(501);broker.Request(Guid.NewGuid(),4,a.Add,_=>writes++);Assert.Equal(2,writes);
    }
    [Fact] public void TimeoutKeepsLateBitmapOutOfLegacyAndDoesNotIssueOverlappingQueries()
    {
        var now=DateTime.UtcNow;using var broker=new ScreenSnapshotBroker(()=>now,false);
        var replies=new List<ScreenSnapshotStateMessage>();int writes=0;var id=Guid.NewGuid();
        broker.Request(id,1,replies.Add,_=>writes++);now=now.AddSeconds(3);broker.Tick();
        Assert.Equal("no-data",replies.Single().Reason);
        broker.Request(id,2,replies.Add,_=>writes++);Assert.Equal(1,writes);Assert.Equal("busy",replies.Last().Reason);
        byte[] dump=new byte[1025];dump[0]=0xef;Assert.Empty(broker.Feed(dump,dump.Length).Forward);
        Assert.Equal(2,replies.Count);
    }
    [Fact] public void RepeatedSessionRequestNeverDuplicatesInFlightUartWrite()
    {
        var now=DateTime.UtcNow;using var broker=new ScreenSnapshotBroker(()=>now,false);int writes=0;var id=Guid.NewGuid();
        var replies=new List<ScreenSnapshotStateMessage>();broker.Request(id,1,replies.Add,_=>writes++);
        broker.Request(id,2,replies.Add,_=>writes++);Assert.Equal("rate-limited",replies.Single().Reason);
        now=now.AddSeconds(1);broker.Request(id,3,replies.Add,_=>writes++);Assert.Equal(1,writes);
        byte[] dump=new byte[1025];dump[0]=0xef;broker.Feed(dump,dump.Length);Assert.Equal(3,replies.Last().RequestId);
    }
    [Fact] public void PartialTimedOutDumpRemainsBoundedAndDrainedWithoutLeakingOrReissuing()
    {
        var now=DateTime.UtcNow;using var broker=new ScreenSnapshotBroker(()=>now,false);int writes=0;var id=Guid.NewGuid();var replies=new List<ScreenSnapshotStateMessage>();
        broker.Request(id,1,replies.Add,_=>writes++);byte[] first=new byte[33];first[0]=0xef;Assert.Empty(broker.Feed(first,first.Length).Forward);
        now=now.AddSeconds(8);broker.Tick();broker.Request(id,2,replies.Add,_=>writes++);Assert.Equal(1,writes);Assert.Equal("busy",replies.Last().Reason);
        byte[] rest=Enumerable.Range(0,992).Select(i=>new byte[]{0xb5,0xab,0xef}[i%3]).ToArray();Assert.Empty(broker.Feed(rest,rest.Length).Forward);Assert.Equal(2,replies.Count);
        broker.Request(Guid.NewGuid(),3,replies.Add,_=>writes++);Assert.Equal(1,writes);Assert.Equal(3,replies.Last().RequestId);
    }
    [Fact] public void MarkerArrivingLongAfterTimeoutIsStillQuarantinedAndOrdinaryUiContinues()
    {
        var now=DateTime.UtcNow;using var broker=new ScreenSnapshotBroker(()=>now,false);int writes=0;var id=Guid.NewGuid();var replies=new List<ScreenSnapshotStateMessage>();
        broker.Request(id,1,replies.Add,_=>writes++);now=now.AddMinutes(5);broker.Tick();Assert.Equal("no-data",replies.Single().Reason);
        byte[] semantic={0xb5,1,0,0,6,3,0xb5,0xef,0xab};Assert.Equal(semantic,broker.Feed(semantic,semantic.Length).Forward);
        broker.Request(Guid.NewGuid(),2,replies.Add,_=>writes++);Assert.Equal("busy",replies.Last().Reason);Assert.Equal(1,writes);
        byte[] dump=new byte[]{0xef}.Concat(Enumerable.Range(0,1024).Select(i=>(byte)(i%3==0?0xb5:i%3==1?0xab:0xef))).ToArray();
        Assert.Empty(broker.Feed(dump,dump.Length).Forward);Assert.Equal(2,replies.Count);
        broker.Request(Guid.NewGuid(),3,replies.Add,_=>writes++);Assert.Equal(1,writes);Assert.Equal(3,replies.Last().RequestId);Assert.Null(replies.Last().Reason);
    }
    [Fact] public void AbandonedRequesterDoesNotReleaseOutstandingReadQuarantine()
    {
        var now=DateTime.UtcNow;using var broker=new ScreenSnapshotBroker(()=>now,false);int writes=0;var id=Guid.NewGuid();var abandoned=new List<ScreenSnapshotStateMessage>();var current=new List<ScreenSnapshotStateMessage>();
        broker.Request(id,1,abandoned.Add,_=>writes++);broker.Forget(id);now=now.AddHours(1);broker.Tick();
        broker.Request(Guid.NewGuid(),2,current.Add,_=>writes++);Assert.Equal("busy",current.Single().Reason);Assert.Equal(1,writes);
        byte[] dump=new byte[1025];dump[0]=0xef;Assert.Empty(broker.Feed(dump,dump.Length).Forward);Assert.Empty(abandoned);
        broker.Reset();broker.Request(Guid.NewGuid(),3,current.Add,_=>writes++);Assert.Equal(2,writes);
    }
    [Theory][InlineData(1)][InlineData(7)][InlineData(512)]
    public void FirmwareNullTelemetryPaddingCannotFakeBitmapMarkerOrLeakToLegacy(int chunk)
    {
        using var broker=new ScreenSnapshotBroker(startTimer:false);var replies=new List<ScreenSnapshotStateMessage>();
        broker.Request(Guid.NewGuid(),1,replies.Add,_=>{});
        byte[] header={0xb5,6,0x84,0,0,216};byte[] padding=new byte[216];
        // Only the non-personal ARM vector signature from the physical capture.
        new byte[]{0xf0,0x3f,0x00,0x20,0x0b,0x01,0x00,0x00}.CopyTo(padding,0);
        padding[50]=0xb5;padding[51]=1;padding[136]=0xef;padding[150]=0xab;
        byte[] pixels=Enumerable.Range(0,1024).Select(i=>(byte)(i*17)).ToArray();
        byte[] clear={0xb5,5,0,0,0,0};
        byte[] stream=header.Concat(padding).Concat(clear).Concat(new byte[]{0xef}).Concat(pixels).ToArray();
        var forwarded=new List<byte>();
        for(int i=0;i<stream.Length;i+=chunk){var part=stream.Skip(i).Take(chunk).ToArray();forwarded.AddRange(broker.Feed(part,part.Length).Forward);}
        Assert.Equal(header.Concat(clear),forwarded);Assert.Single(replies);Assert.Equal(pixels,replies[0].Data);
        Assert.Null(replies[0].Reason);
    }
    [Theory][InlineData(0xb5)][InlineData(0xab)][InlineData(0xef)]
    public void FixedFirmwareWithoutTelemetryPaddingKeepsFollowingUiBinaryOrBitmap(int marker)
    {
        using var broker=new ScreenSnapshotBroker(startTimer:false);var replies=new List<ScreenSnapshotStateMessage>();broker.Request(Guid.NewGuid(),1,replies.Add,_=>{});
        byte[] header={0xb5,6,0x84,0,0,216};byte[] following=marker==0xb5?new byte[]{0xb5,5,0,0,0,0}:marker==0xab?Frame(0x0951,4):new byte[]{0xef}.Concat(new byte[1024]).ToArray();
        byte[] stream=header.Concat(following).ToArray();var forwarded=new List<byte>();
        foreach(byte value in stream)forwarded.AddRange(broker.Feed(new[]{value},1).Forward);
        Assert.Equal(marker==0xef?header:stream,forwarded);
        Assert.Equal(marker==0xef?1:0,replies.Count);
    }
    [Fact] public void InvalidVectorCandidateIsReplayedInsteadOfDiscardingUnknownData()
    {
        using var broker=new ScreenSnapshotBroker(startTimer:false);
        byte[] header={0xb5,6,0,0,0,216};
        byte[] badStack={0xf0,0x3f,0,0x10,0x0b,1,0,0}; // Not SRAM.
        byte[] badReset={0xf0,0x3f,0,0x20,0x0a,1,0,0}; // ARM Reset vector must be Thumb.
        byte[] stream=header.Concat(badStack).Concat(header).Concat(badReset).ToArray();var forwarded=new List<byte>();
        foreach(byte value in stream)forwarded.AddRange(broker.Feed(new[]{value},1).Forward);
        Assert.Equal(stream,forwarded);
    }
    [Fact] public void ObserverCanReadWithoutControlButTxAndScanBlockQuery()
    {
        var now=DateTime.UtcNow;using var manager=new SessionManager(utcNow:()=>now);
        manager.HardwareStatus=()=>(true,true,true);int writes=0;manager.ReadScreenCommand=_=>writes++;
        var id=Guid.NewGuid();var frames=new List<byte[]>();manager.RegisterOrAttach(id,"OBSERVER",false,frames.Add,2);
        now=now.AddSeconds(4);manager.RequestScreenSnapshot(id,new(1));Assert.Equal(1,writes);Assert.Null(manager.ControlOwnerSessionId);
        byte[] dump=new byte[1025];dump[0]=0xef;Assert.Empty(manager.FilterScreenSnapshotSerial(dump,dump.Length));
        Assert.Single(frames.Where(f=>f[0]==30));
        var owner=Guid.NewGuid();manager.RegisterOrAttach(owner,"OWNER",false,_=>{},2);manager.TryRequestControl(owner);
        byte[] scan=Frame(0x0808,14,101);manager.ForwardSerial(owner,scan,scan.Length,(_,_,_)=>{});
        manager.RequestScreenSnapshot(id,new(2));Assert.Equal("busy",Read(frames).Reason);Assert.Equal(1,writes);
        byte[] stop=Frame(0x0809,14,0);manager.ForwardSerial(owner,stop,stop.Length,(_,_,_)=>{});
        manager.TryRequestTx(owner,145.5,"FM",1);manager.RequestScreenSnapshot(id,new(3));Assert.Equal("busy",Read(frames).Reason);Assert.Equal(1,writes);
    }
    [Fact] public void FirmwareScanReplyMakesCaptureBusyEvenAfterServerRestart()
    {
        var now=DateTime.UtcNow;using var manager=new SessionManager(utcNow:()=>now);manager.HardwareStatus=()=>(true,true,true);
        int writes=0;manager.ReadScreenCommand=_=>writes++;var frames=new List<byte[]>();var id=Guid.NewGuid();manager.RegisterOrAttach(id,"TEST",false,frames.Add,2);
        byte[] scanReply=Frame(0x0908,4);Assert.Equal(scanReply,manager.FilterScreenSnapshotSerial(scanReply,scanReply.Length));
        now=now.AddSeconds(5);manager.RequestScreenSnapshot(id,new(1));Assert.Equal(0,writes);Assert.Equal("busy",Read(frames).Reason);
    }
    [Fact] public void CorruptCommandCannotClearScanGuardAndUartLossKeepsItConservative()
    {
        var now=DateTime.UtcNow;using var manager=new SessionManager(utcNow:()=>now);manager.HardwareStatus=()=>(true,true,true);
        int writes=0;manager.ReadScreenCommand=_=>writes++;var frames=new List<byte[]>();var owner=Guid.NewGuid();manager.RegisterOrAttach(owner,"OWNER",false,frames.Add,2);manager.TryRequestControl(owner);
        var scan=Frame(0x0808,14,101);manager.ForwardSerial(owner,scan,scan.Length,(_,_,_)=>{});
        var corrupt=Frame(0x0851,4);corrupt[8]^=0x01;Assert.False(RadioProtocol.IsValidFrame(corrupt));manager.ForwardSerial(owner,corrupt,corrupt.Length,(_,_,_)=>{});
        manager.ScreenSerialDisconnected();now=now.AddSeconds(5);manager.RequestScreenSnapshot(owner,new(1));Assert.Equal("busy",Read(frames).Reason);Assert.Equal(0,writes);
        var stop=Frame(0x0809,14,0);manager.ForwardSerial(owner,stop,stop.Length,(_,_,_)=>{});manager.RequestScreenSnapshot(owner,new(2));Assert.Equal(1,writes);
    }
    [Fact] public void OldControlSocketCannotReceiveDelayedSnapshotOfReplacement()
    {
        var now=DateTime.UtcNow;using var manager=new SessionManager(utcNow:()=>now);manager.HardwareStatus=()=>(true,true,true);manager.ReadScreenCommand=_=>{};
        var old=new List<byte[]>();var current=new List<byte[]>();var id=Guid.NewGuid();manager.RegisterOrAttach(id,"TEST",false,old.Add,2);now=now.AddSeconds(5);manager.RequestScreenSnapshot(id,new(1));
        manager.RegisterOrAttach(id,"TEST",false,current.Add,2);byte[] dump=new byte[1025];dump[0]=0xef;manager.FilterScreenSnapshotSerial(dump,dump.Length);
        Assert.DoesNotContain(old,f=>f[0]==30);Assert.DoesNotContain(current,f=>f[0]==30);
    }
    [Fact] public void WarmupAndUnavailablePortNeverSendCommandsAndLegacyGetsNoExtension()
    {
        var now=DateTime.UtcNow;using var manager=new SessionManager(utcNow:()=>now);int writes=0;manager.ReadScreenCommand=_=>writes++;
        var frames=new List<byte[]>();var id=Guid.NewGuid();manager.RegisterOrAttach(id,"TEST",false,frames.Add,2);
        manager.RequestScreenSnapshot(id,new(1));Assert.Equal("busy",Read(frames).Reason);
        now=now.AddSeconds(4);manager.RequestScreenSnapshot(id,new(2));Assert.Equal("serial-unavailable",Read(frames).Reason);Assert.Equal(0,writes);
        var legacy=new List<byte[]>();var old=Guid.NewGuid();manager.RegisterOrAttach(old,"OLD",false,legacy.Add,1);manager.RequestScreenSnapshot(old,new(1));Assert.DoesNotContain(legacy,f=>f[0]==30);
    }
    private static ScreenSnapshotStateMessage Read(List<byte[]> frames)=>JsonSerializer.Deserialize<ScreenSnapshotStateMessage>(frames.Last(f=>f[0]==30).AsSpan(5))!;
}

using System;
using QDNH.SharedRadio;
using Xunit;

namespace QDNH.Tests;

public class SessionReconnectTests
{
    [Fact]
    public void ClosingOldControlSocketDoesNotReleaseNewConnection()
    {
        using var sessions = new SessionManager();
        Guid id = Guid.NewGuid();
        Action<byte[]> oldSocket = _ => { };
        Action<byte[]> newSocket = _ => { };

        sessions.RegisterOrAttach(id, "TEST", false, oldSocket);
        sessions.TryRequestControl(id);
        sessions.TryRequestTx(id, 145.5, "FM");
        sessions.RegisterOrAttach(id, "TEST", false, newSocket);

        sessions.OnControlChannelClosed(id, oldSocket);

        Assert.True(sessions.IsControlOwner(id));
        Assert.Equal(id, sessions.TxOwnerSessionId);

        sessions.OnControlChannelClosed(id, newSocket);

        Assert.False(sessions.IsControlOwner(id));
        Assert.Null(sessions.TxOwnerSessionId);
    }

    [Fact]
    public void ClosingOldAudioSocketDoesNotReleaseTx()
    {
        using var sessions = new SessionManager();
        Guid id = Guid.NewGuid();
        Action<byte[]> control = _ => { };
        Action<byte[], int> oldAudio = (_, _) => { };
        Action<byte[], int> newAudio = (_, _) => { };

        sessions.RegisterOrAttach(id, "TEST", false, control);
        sessions.AttachAudio(id, oldAudio);
        sessions.TryRequestControl(id);
        sessions.TryRequestTx(id, 145.5, "FM");
        sessions.AttachAudio(id, newAudio);

        sessions.OnAudioChannelClosed(id, oldAudio);
        Assert.Equal(id, sessions.TxOwnerSessionId);

        sessions.OnAudioChannelClosed(id, newAudio);
        Assert.Null(sessions.TxOwnerSessionId);
    }
    [Fact]
    public void OldConnectionsCannotSendCommandsAfterReplacement()
    {
        using var manager = new SessionManager();
        Guid id = Guid.NewGuid();
        Action<byte[]> controlOld = _ => { }, controlNew = _ => { };
        Action<byte[], int> dataOld = (_, _) => { }, dataNew = (_, _) => { };
        manager.RegisterOrAttach(id, "TEST", true, controlOld);
        manager.TryRequestControl(id); manager.TryRequestTx(id, 145.5, "FM");
        manager.RegisterOrAttach(id, "TEST", true, controlNew);
        bool ran = false;
        Assert.False(manager.ExecuteControl(id, controlOld, () => ran = true));
        Assert.False(ran);
        manager.AttachAudio(id, dataOld); manager.AttachAudio(id, dataNew);
        Assert.False(manager.OnTxAudio(id, new byte[2], 2, dataOld));
        manager.AttachSerial(id, dataOld); manager.AttachSerial(id, dataNew);
        byte[] frame = QDNH.Serial.RadioProtocol.Key(16);
        manager.ForwardSerial(id, frame, frame.Length, (_, _, _) => ran = true, dataOld);
        Assert.False(ran);
    }
}

using System;
using QDNH.SharedRadio;
using Xunit;

namespace QDNH.Tests;

public class SessionReconnectTests
{
    [Fact]
    public void ClosingOldControlSocketDoesNotReleaseNewConnection()
    {
        var sessions = new SessionManager();
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
        var sessions = new SessionManager();
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
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QDNH.Network;
using QDNH.Settings;
using QDNH.SharedRadio;
using Xunit;

namespace QDNH.Tests;

public class MultiListenIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealConnectionKeepsItsIdentityForInputAndDisconnect(bool audio)
    {
        // All sinks are memory-only: no UART, audio device or RF operation.
        using var manager = new SessionManager();
        Guid id = Guid.NewGuid();
        manager.RegisterOrAttach(id, "LOOPBACK", false, _ => { });
        manager.TryRequestControl(id);
        if (audio) manager.TryRequestTx(id, 145.5, "FM");
        Action<byte[], int>? attached = null;
        var received = new TaskCompletionSource<(bool Same, bool Accepted)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new MultiListen(0,
            (session, source, bytes, count) =>
            {
                bool accepted = false;
                if (audio) accepted = manager.ForwardTxAudio(session, bytes, count, (_, _) => { }, source);
                else manager.ForwardSerial(session, bytes, count, (_, _, _) => accepted = true, source);
                received.TrySetResult((ReferenceEquals(attached, source), accepted));
            },
            (session, source, principal) =>
            {
                attached = source;
                return audio ? manager.AttachAudio(session, source, principal) : manager.AttachSerial(session, source, principal);
            },
            (session, source) =>
            {
                if (audio) manager.OnAudioChannelClosed(session, source);
                else manager.OnSerialChannelClosed(session, source);
                disconnected.TrySetResult(ReferenceEquals(attached, source));
            }, dropOldest: audio);
        using var client = new TcpClient();
        try
        {
            var listener = (TcpListener)typeof(MultiListen).GetField("listener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var stream = client.GetStream();
            byte[] salt = new byte[32];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await stream.ReadExactlyAsync(salt, timeout.Token);
            byte[] secret = Encoding.ASCII.GetBytes(Vars.Password);
            await stream.WriteAsync(SHA256.HashData(secret.Concat(salt).ToArray()), timeout.Token);
            await stream.WriteAsync(id.ToByteArray(), timeout.Token);
            byte[] input = audio ? new byte[] { 1, 2, 3, 4 } : QDNH.Serial.RadioProtocol.Key(1);
            await stream.WriteAsync(input, timeout.Token);
            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Same, "Input must carry the exact delegate registered for this socket.");
            Assert.True(result.Accepted, "The legitimate control/TX owner must reach the in-memory sink.");
            client.Close();
            Assert.True(await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)), "Disconnect must carry that same socket identity.");
            var remaining = manager.GetOperators().Operators.Single();
            Assert.False(audio ? remaining.AudioConnected : remaining.SerialConnected);
            if (audio) Assert.Null(manager.TxOwnerSessionId);
        }
        finally { client.Close(); server.Close(); }
    }
}

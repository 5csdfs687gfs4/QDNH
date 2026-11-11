using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QDNH.Network;
using QDNH.Settings;
using QDNH.SharedRadio;
using Xunit;

namespace QDNH.Tests;

/// <summary>
/// Pruebas de la ruta real de MultiListen (TCP + reto de Authenticator), no
/// solo de SessionManager: es justo aqui donde se decide la identidad del
/// delegado de cada conexion. Un fallo en esa identidad deja las bajas de
/// audio/serie sin efecto, y el TX y los envios se quedan colgados de una
/// sesion que ya no existe.
/// </summary>
public class MultiListenTests
{
    [Fact]
    public void AudioDisconnectReleasesTxOfThatConnection()
    {
        string previousPassword = Vars.Password;
        Vars.Password = "prueba-" + Guid.NewGuid().ToString("N");
        int port = FreePort();
        var sessions = new SessionManager();
        Action<byte[], int>? attachedSend = null;
        Guid attachedId = Guid.Empty;

        var multi = new MultiListen(
            port,
            (_, _, _) => { },
            (sessionId, send) =>
            {
                attachedId = sessionId;
                attachedSend = send;
                return sessions.AttachAudio(sessionId, send);
            },
            (sessionId, send) => sessions.OnAudioChannelClosed(sessionId, send));

        try
        {
            Guid id = Guid.NewGuid();
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                NetworkStream stream = client.GetStream();

                byte[] salt = ReadExactly(stream, 32);
                byte[] hash = SHA256.HashData(
                    Encoding.ASCII.GetBytes(Vars.Password).Concat(salt).ToArray());
                stream.Write(hash, 0, hash.Length);
                stream.Write(id.ToByteArray(), 0, 16);
                stream.Flush();

                Assert.True(WaitFor(() => attachedSend != null && attachedId == id, TimeSpan.FromSeconds(5)),
                    "MultiListen no registro el canal de audio");

                sessions.TryRequestControl(id);
                sessions.TryRequestTx(id, 145.5, "FM");
                Assert.Equal(id, sessions.TxOwnerSessionId);
            }

            Assert.True(WaitFor(() => sessions.TxOwnerSessionId == null, TimeSpan.FromSeconds(5)),
                "al caer el canal de audio no se libero el TX de esa sesion");
        }
        finally
        {
            multi.Close();
            Vars.Password = previousPassword;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] ReadExactly(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = stream.Read(buffer, got, count - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
        return buffer;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}

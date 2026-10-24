using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QDNH.Network;
using QDNH.Settings;
using QDNH.SharedRadio;
using Xunit;
namespace QDNH.Tests;
public class ControlTransportTests
{
    private static async Task<byte[]> ReadExact(NetworkStream stream, int length)
    {
        var result = new byte[length]; int offset = 0;
        using var timeout = new CancellationTokenSource(5000);
        while (offset < length)
        {
            int n = await stream.ReadAsync(result.AsMemory(offset), timeout.Token);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
        return result;
    }
    [Fact]
    public async Task ConcurrentBroadcastsKeepFramesIntactAndServerCloseReleasesPtt()
    {
        int releases = 0;
        using var manager = new SessionManager(() => Interlocked.Increment(ref releases));
        var server = new ControlChannel(0, manager);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            var stream = client.GetStream();
            byte[] salt = await ReadExact(stream, 32);
            byte[] password = Encoding.ASCII.GetBytes(Vars.Password);
            byte[] challenge = new byte[password.Length + 32];
            password.CopyTo(challenge, 0); salt.CopyTo(challenge, password.Length);
            await stream.WriteAsync(SHA256.HashData(challenge));
            Guid id = Guid.NewGuid();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new HelloMessage(id, "TCPTEST", true, 2));
            byte[] hello = new byte[5 + json.Length]; hello[0] = 1;
            BitConverter.GetBytes(json.Length).CopyTo(hello, 1); json.CopyTo(hello, 5);
            // La fragmentación deliberada también debe conservar el framing.
            foreach (byte b in hello) await stream.WriteAsync(new byte[] { b });
            for (int i = 0; i < 2; i++)
            {
                byte[] header = await ReadExact(stream, 5);
                byte[] payload = await ReadExact(stream, BitConverter.ToInt32(header, 1));
                using var parsed = JsonDocument.Parse(payload);
                Assert.Contains(header[0], new byte[] { 2, 24 });
            }
            await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 20; i++) manager.SendHealth(id); }),
                Task.Run(() => { for (int i = 0; i < 20; i++) manager.SendOperators(id); }));
            int health = 0, operators = 0;
            for (int i = 0; i < 40; i++)
            {
                byte[] header = await ReadExact(stream, 5);
                byte[] payload = await ReadExact(stream, BitConverter.ToInt32(header, 1));
                using var parsed = JsonDocument.Parse(payload);
                if (header[0] == 22) health++;
                else if (header[0] == 24) operators++;
                else Assert.Fail("Tipo inesperado");
            }
            Assert.Equal(20, health); Assert.Equal(20, operators);
            manager.TryRequestControl(id); manager.TryRequestTx(id, 145.5, "FM");
            // ReHello se usa para activar monitor solo tras negociar capacidades.
            json = JsonSerializer.SerializeToUtf8Bytes(new HelloMessage(id, "TCPTEST", true, 2));
            hello = new byte[5 + json.Length]; hello[0] = 1;
            BitConverter.GetBytes(json.Length).CopyTo(hello, 1); json.CopyTo(hello, 5);
            await stream.WriteAsync(hello);
            bool gotAck = false;
            for (int i = 0; i < 6; i++)
            {
                var header = await ReadExact(stream, 5);
                await ReadExact(stream, BitConverter.ToInt32(header, 1));
                if (header[0] == 2) { gotAck = true; break; }
            }
            Assert.True(gotAck); Assert.True(manager.IsControlOwner(id)); Assert.Equal(id, manager.TxOwnerSessionId);
            server.Close();
            Assert.True(SpinWait.SpinUntil(() => releases == 1, 3000));
        }
        finally { server.Close(); }
    }
    [Fact]
    public async Task IndividualSecretAuthenticatesOnWireAndHelloCannotChangeIdentity()
    {
        const string secret = "only-test-fixture-secret-123";
        string config = "{\"SchemaVersion\":1,\"Operators\":[{\"UserId\":\"fixture\",\"Callsign\":\"AUTENTICADO\",\"Role\":\"Observer\",\"SecretEnvironmentVariable\":\"QDNH_TEST_SECRET\"}]}";
        var policy = QDNH.Security.OperatorAuthentication.Parse(config, _ => secret);
        using var manager = new SessionManager(); manager.ResetForReconfigure(true);
        var server = new ControlChannel(0, manager, policy);
        try
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            var stream = client.GetStream(); byte[] salt = await ReadExact(stream, 32);
            byte[] input = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(Encoding.ASCII.GetBytes(secret), salt));
            await stream.WriteAsync(SHA256.HashData(input));
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new HelloMessage(Guid.NewGuid(), "SUPLANTADO", true, 2));
            byte[] hello = new byte[json.Length + 5]; hello[0] = 1; BitConverter.GetBytes(json.Length).CopyTo(hello, 1); json.CopyTo(hello, 5);
            await stream.WriteAsync(hello);
            byte[] header = await ReadExact(stream, 5); Assert.Equal(2, header[0]);
            var ack = JsonSerializer.Deserialize<HelloAckMessage>(await ReadExact(stream, BitConverter.ToInt32(header, 1)));
            Assert.Equal("fixture", ack!.AuthenticatedUserId); Assert.Equal("Observer", ack.Role);
            Assert.Equal("AUTENTICADO", manager.GetOperators().Operators[0].Callsign);
            using var wrong = new TcpClient(); await wrong.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            var wrongStream = wrong.GetStream(); salt = await ReadExact(wrongStream, 32);
            input = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(Encoding.ASCII.GetBytes("old-shared-password"), salt));
            await wrongStream.WriteAsync(SHA256.HashData(input));
            using var timeout = new CancellationTokenSource(3000);
            Assert.Equal(0, await wrongStream.ReadAsync(new byte[1], timeout.Token));
        }
        finally { server.Close(); }
    }
    [Fact]
    public void LegacyHelloDoesNotOptIntoExtensions()
    {
        var hello = JsonSerializer.Deserialize<HelloMessage>("{\"SessionId\":\"aab789ff-851c-448a-9fef-57686353da78\",\"Callsign\":\"OLD\",\"SupportsTxMonitor\":false}");
        Assert.NotNull(hello); Assert.Equal(1, hello!.ProtocolVersion); Assert.Null(hello.Capabilities);
    }
}

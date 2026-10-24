using QDNH.Security;
using QDNH.SharedRadio;
using QDNH.Settings;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Concurrent;
namespace QDNH.Network
{
    /// <summary>
    /// Tercer canal de Shared Radio Mode (puerto NetworkPort+2): mensajes
    /// cortos y poco frecuentes de control/TX/TX-Monitor, separados de
    /// las tuberias de bytes crudos de audio y serie (Network/MultiListen.cs)
    /// para no arriesgar esos dos caminos criticos. A diferencia de
    /// Network/Listen.cs (que no se toca, sigue siendo el modo
    /// tradicional de un solo usuario), este admite VARIOS clientes
    /// conectados a la vez -- hace falta para poder avisar a todos los
    /// oyentes de un cambio de estado con una unica llamada.
    ///
    /// Esta clase es solo transporte + framing: toda la logica de quien
    /// puede tomar el control/TX vive en SharedRadio/SessionManager.cs.
    /// </summary>
    public class ControlChannel
    {
        private readonly TcpListener listener;
        public bool IsListening
        {
            get
            {
                try { return !closed && listener.Server.IsBound && loop != null && !loop.IsCompleted; }
                catch (ObjectDisposedException) { return false; }
                catch (SocketException) { return false; }
            }
        }
        private readonly OperatorAuthentication? authentication;
        private readonly SessionManager sessions;
        private volatile bool closed = false;
        private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
        private readonly Task loop;
        public int BoundPort => ((IPEndPoint)listener.LocalEndpoint).Port;
        public ControlChannel(int port, SessionManager sessions, OperatorAuthentication? authentication = null)
        {
            this.sessions = sessions;
            this.authentication = authentication;
            listener = new(IPAddress.Any, port);
            listener.Start();
            loop = Loop();
        }
        public void Close()
        {
            using (loop)
            {
                closed = true;
                try { listener.Stop(); } catch { }
                foreach (var client in clients.Keys) client.Dispose();
                loop.Wait();
            }
        }
        private async Task Loop()
        {
            while (!closed)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { continue; }
                // A diferencia de Listen.cs, NO se espera a que este
                // cliente termine antes de aceptar el siguiente.
                if (clients.Count >= 64) { client.Dispose(); continue; }
                clients.TryAdd(client, 0);
                _ = Task.Run(async () => { try { await HandleClient(client); } finally { clients.TryRemove(client, out _); } });
            }
        }
        private async Task HandleClient(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true; client.ReceiveTimeout = 10000;
                NetworkStream stream = client.GetStream();
                // Mismo reto salt+SHA256 que ya usan los otros dos
                // canales (Network/Authenticator.cs, sin tocar).
                Authenticator auth = new(Vars.Password);
                try { await stream.WriteAsync(auth.Salt); await stream.FlushAsync(); } catch { return; }
                byte[] chal = new byte[auth.Hash.Length];
                try
                {
                    int received = 0;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (received < chal.Length)
                    {
                        int count = await stream.ReadAsync(chal.AsMemory(received), timeout.Token);
                        if (count == 0) return;
                        received += count;
                    }
                }
                catch { return; }
                OperatorPrincipal? principal = authentication?.Authenticate(chal, auth.Salt);
                if (authentication != null ? principal == null : !auth.Challenge(chal)) return;
                SessionInfo? session = null;
                Action<byte[]>? sendToThis = null;
                // Every response for this connection shares one framed stream.
                // Serialize writes so concurrent broadcasts cannot interleave
                // their headers and payloads on the wire.
                var outgoing = Channel.CreateBounded<byte[]>(64);
                Task writer = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var frame in outgoing.Reader.ReadAllAsync())
                            await stream.WriteAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    catch { client.Dispose(); }
                });
                try
                {
                    while (!closed)
                    {
                        var msg = session == null ? await ReadMessage(stream).WaitAsync(TimeSpan.FromSeconds(10)) : await ReadMessage(stream);
                        if (msg == null) break;
                        var (type, payload) = msg.Value;
                        if (session == null)
                        {
                            // El primer mensaje de una conexion nueva
                            // TIENE que ser Hello -- cualquier otra cosa
                            // cierra la conexion.
                            if (type != ControlMsgType.Hello) break;
                            var hello = JsonSerializer.Deserialize<HelloMessage>(payload);
                            if (hello == null) break;
                            sendToThis = frame => { if (!outgoing.Writer.TryWrite(frame)) client.Dispose(); };
                            session = sessions.RegisterOrAttach(hello.SessionId, hello.Callsign, hello.SupportsTxMonitor, sendToThis, hello.ProtocolVersion, principal);
                            continue;
                        }
                        if (sendToThis == null) break;
                        bool current = sessions.ExecuteControl(session.Id, sendToThis, () =>
                        {
                            switch (type)
                            {
                                case ControlMsgType.Hello:
                                    var update = JsonSerializer.Deserialize<HelloMessage>(payload);
                                    if (update != null && update.SessionId == session.Id)
                                        sessions.RegisterOrAttach(session.Id, update.Callsign, update.SupportsTxMonitor, sendToThis, update.ProtocolVersion, principal);
                                    break;
                                case ControlMsgType.HealthRequest: sessions.SendHealth(session.Id); break;
                                case ControlMsgType.OperatorsRequest: sessions.SendOperators(session.Id); break;
                                case ControlMsgType.QueueJoin: sessions.QueueControl(session.Id, true); break;
                                case ControlMsgType.QueueLeave: sessions.QueueControl(session.Id, false); break;
                                case ControlMsgType.ControlRequest:
                                    sessions.TryRequestControl(session.Id);
                                    break;
                                case ControlMsgType.ControlRelease:
                                    sessions.ReleaseControl(session.Id);
                                    break;
                                case ControlMsgType.RequestTx:
                                    var req = JsonSerializer.Deserialize<RequestTxMessage>(payload);
                                    if (req != null) sessions.TryRequestTx(session.Id, req.FrequencyMHz, req.Modulation);
                                    break;
                                case ControlMsgType.TxRelease:
                                    sessions.ReleaseTx(session.Id, "PTT_OFF");
                                    break;
                            }
                        });
                        if (!current) break;
                    }
                }
                catch { }
                finally
                {
                    outgoing.Writer.TryComplete();
                    client.Dispose();
                    if (session != null && sendToThis != null)
                        sessions.OnControlChannelClosed(session.Id, sendToThis);
                    try { await writer; } catch { }
                }
            }
        }
        private static async Task<(ControlMsgType, byte[])?> ReadMessage(NetworkStream stream)
        {
            byte[] header = new byte[5];
            if (!await ReadExact(stream, header, 5)) return null;
            ControlMsgType type = (ControlMsgType)header[0];
            int len = BitConverter.ToInt32(header, 1);
            if (len < 0 || len > 65536) return null; // limite generoso, estos mensajes son pequenos
            byte[] payload = len == 0 ? Array.Empty<byte>() : new byte[len];
            if (len > 0 && !await ReadExact(stream, payload, len)) return null;
            return (type, payload);
        }
        private static async Task<bool> ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n;
                try { n = await stream.ReadAsync(buffer.AsMemory(got, count - got)); }
                catch { return false; }
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }
}

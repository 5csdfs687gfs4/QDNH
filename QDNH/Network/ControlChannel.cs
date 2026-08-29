using QDNH.SharedRadio;
using QDNH.Settings;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

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
        private readonly SessionManager sessions;
        private bool closed = false;
        private readonly Task loop;

        public ControlChannel(int port, SessionManager sessions)
        {
            this.sessions = sessions;
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
                _ = Task.Run(() => HandleClient(client));
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();

                // Mismo reto salt+SHA256 que ya usan los otros dos
                // canales (Network/Authenticator.cs, sin tocar).
                Authenticator auth = new(Vars.Password);
                try { await stream.WriteAsync(auth.Salt); await stream.FlushAsync(); } catch { return; }
                byte[] chal = new byte[auth.Hash.Length];
                for (int i = 0; i < chal.Length; i++)
                {
                    int b;
                    try { b = stream.ReadByte(); } catch { b = -1; }
                    if (b < 0) return;
                    chal[i] = (byte)b;
                }
                if (!auth.Challenge(chal)) return;

                SessionInfo? session = null;
                try
                {
                    while (!closed)
                    {
                        var msg = await ReadMessage(stream);
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

                            NetworkStream st = stream;
                            void SendToThis(byte[] frame) => _ = WriteMessage(st, frame);
                            session = sessions.RegisterOrAttach(hello.SessionId, hello.Callsign, hello.SupportsTxMonitor, SendToThis);
                            continue;
                        }

                        switch (type)
                        {
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
                    }
                }
                catch { }
                finally
                {
                    if (session != null)
                        sessions.OnControlChannelClosed(session.Id);
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

        private static async Task WriteMessage(NetworkStream stream, byte[] frame)
        {
            try { await stream.WriteAsync(frame); await stream.FlushAsync(); }
            catch { }
        }
    }
}

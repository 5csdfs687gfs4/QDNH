using QDNH.Settings;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QDNH.Network
{
    /// <summary>
    /// Version de Listen.cs (sin tocarla, que sigue siendo el modo
    /// tradicional de un solo usuario) que admite VARIOS clientes
    /// conectados a la vez, cada uno correlacionado con una sesion de
    /// Shared Radio Mode. Se usa tanto para el puerto de audio como para
    /// el de serie -- el propio Main.cs decide, con los delegados
    /// onConnected/onDisconnected, si eso significa registrar la sesion
    /// como audio o como serie (ver SharedRadio/SessionManager.cs
    /// AttachAudio/AttachSerial).
    ///
    /// Handshake de esta conexion, DESPUES del reto de contraseña de
    /// siempre (Authenticator, sin cambios): el cliente manda los 16
    /// bytes crudos de su SessionId (el mismo GUID que ya establecio en
    /// el canal de control, ver ControlChannel.cs) ANTES de empezar a
    /// mandar PCM/serie real. A partir de ahi todo lo que llega es
    /// exactamente el mismo flujo de bytes crudos que ya manejaba
    /// Listen.cs -- ningun cambio en como se interpreta el audio o el
    /// protocolo CAT, que QDNH sigue sin entender.
    /// </summary>
    public class MultiListen
    {
        private readonly TcpListener listener;
        private readonly Action<Guid, byte[], int> callback;
        private readonly Func<Guid, Action<byte[], int>, object> onConnected;
        private readonly Action<Guid> onDisconnected;
        private bool closed = false;
        private readonly Task loop;

        public MultiListen(
            int port,
            Action<Guid, byte[], int> callback,
            Func<Guid, Action<byte[], int>, object> onConnected,
            Action<Guid> onDisconnected)
        {
            this.callback = callback;
            this.onConnected = onConnected;
            this.onDisconnected = onDisconnected;
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
                _ = Task.Run(() => HandleClient(client));
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            using (client)
            {
                // TCP_NODELAY (ago-2026, recuperado de un fix huerfano que
                // nunca llego a mergearse en Listen.cs): sin esto Nagle
                // agrupa bloques cortos (audio/comandos) y los retrasa --
                // mas notable en WAN, donde puede dar la impresion de un
                // corte de conexion entero en vez de simple latencia.
                try { client.NoDelay = true; } catch { }
                NetworkStream stream = client.GetStream();

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

                byte[] idBytes = new byte[16];
                int got = 0;
                while (got < 16)
                {
                    int n;
                    try { n = await stream.ReadAsync(idBytes.AsMemory(got, 16 - got)); }
                    catch { n = 0; }
                    if (n <= 0) return;
                    got += n;
                }
                Guid sessionId = new(idBytes);

                // Herramienta de diagnostico (ago-2026, desconexiones
                // intermitentes de Shared Radio Mode sobre WAN): duracion
                // de esta conexion y bytes recibidos, para poder loguear
                // ambos en el momento del corte -- antes esta rama del
                // read loop se limitaba a "catch { br = -1; }", tragandose
                // la excepcion entera sin dejar ni rastro de si el corte
                // fue por una excepcion real (y cual) o un EOF limpio del
                // otro lado.
                DateTime connectedAtUtc = DateTime.UtcNow;
                long bytesInThisConn = 0;

                // Cola acotada propia de este cliente para lo que le
                // enviemos (RX real, telemetria serie o copia de TX
                // Monitor, segun el puerto). Descarta lo mas antiguo si
                // el cliente no puede con el ritmo -- nunca bloquea al
                // que escribe.
                var queue = Channel.CreateBounded<(byte[] Buffer, int Length)>(
                    new BoundedChannelOptions(16)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                    });

                void Send(byte[] data, int length)
                {
                    byte[] copy = new byte[length];
                    Array.Copy(data, copy, length);
                    if (!queue.Writer.TryWrite((copy, length)))
                        Vars.Out($"[SharedRadio] frame descartado (cola llena) sesion={sessionId}");
                }

                onConnected(sessionId, Send);

                Task sender = Task.Run(async () =>
                {
                    await foreach (var item in queue.Reader.ReadAllAsync())
                    {
                        try { await stream.WriteAsync(item.Buffer.AsMemory(0, item.Length)); await stream.FlushAsync(); }
                        catch { break; }
                    }
                });

                Exception? lastException = null;
                try
                {
                    while (!closed)
                    {
                        byte[] b = new byte[4096];
                        int br;
                        try { br = await stream.ReadAsync(b); }
                        catch (Exception ex) { lastException = ex; br = -1; }
                        if (br <= 0) break;
                        bytesInThisConn += br;
                        callback(sessionId, b, br);
                    }
                }
                finally
                {
                    double secs = (DateTime.UtcNow - connectedAtUtc).TotalSeconds;
                    string reason = lastException == null
                        ? "EOF limpio (FIN remoto, sin excepcion)"
                        : ExceptionChain(lastException);
                    Vars.Out($"[SharedRadio] cliente desconectado sesion={sessionId} " +
                             $"duracion={secs:F1}s recibidos={bytesInThisConn}B motivo={reason}");
                    queue.Writer.TryComplete();
                    try { await sender; } catch { }
                    onDisconnected(sessionId);
                }
            }
        }

        // Herramienta de diagnostico: recorre ex.InnerException (hasta 4
        // niveles) para sacar a la luz el SocketException real cuando lo
        // hay -- el mensaje externo (p.ej. de un IOException) casi nunca
        // dice la causa; su SocketErrorCode (ConnectionReset, TimedOut,
        // ConnectionAborted...) es lo que de verdad distingue un corte
        // iniciado por el otro lado de uno causado por el camino de red.
        private static string ExceptionChain(Exception ex)
        {
            StringBuilder sb = new();
            Exception? current = ex;
            int depth = 0;
            while (current != null && depth < 4)
            {
                if (depth > 0) sb.Append(" <- ");
                sb.Append(current.GetType().Name);
                if (current is SocketException se)
                    sb.Append($"({se.SocketErrorCode})");
                sb.Append(": ").Append(current.Message);
                current = current.InnerException;
                depth++;
            }
            return sb.ToString();
        }
    }
}

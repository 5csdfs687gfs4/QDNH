using QDNH.Audio;
using QDNH.Security;
using QDNH.Settings;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
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
        private readonly Action<Guid, Action<byte[], int>, byte[], int> callback;
        private readonly Func<Guid, Action<byte[], int>, OperatorPrincipal?, object> onConnected;
        private readonly Action<Guid, Action<byte[], int>> onDisconnected;
        private volatile bool closed = false;
        private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
        private readonly bool dropOldest;
        private readonly Task loop;
        public MultiListen(
            int port,
            Action<Guid, Action<byte[], int>, byte[], int> callback,
            Func<Guid, Action<byte[], int>, OperatorPrincipal?, object> onConnected,
            Action<Guid, Action<byte[], int>> onDisconnected, bool dropOldest = true, OperatorAuthentication? authentication = null)
        {
            this.callback = callback;
            this.authentication = authentication;
            this.dropOldest = dropOldest;
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
                if (clients.Count >= 64) { client.Dispose(); continue; }
                clients.TryAdd(client, 0);
                _ = Task.Run(async () => { try { await HandleClient(client); } finally { clients.TryRemove(client, out _); } });
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
                client.ReceiveTimeout = 10000;
                NetworkStream stream = client.GetStream();
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
                byte[] idBytes = new byte[16];
                int got = 0;
                while (got < 16)
                {
                    int n;
                    try { n = await stream.ReadAsync(idBytes.AsMemory(got, 16 - got)).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
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
                        FullMode = dropOldest ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
                        SingleReader = true,
                    });
                void Send(byte[] data, int length)
                {
                    byte[] copy = new byte[length];
                    Array.Copy(data, copy, length);
                    if (!queue.Writer.TryWrite((copy, length)))
                        client.Dispose(); // Nunca perder bytes serie silenciosamente: cerrar el cliente lento.
                }
                try { onConnected(sessionId, Send, principal); } catch (UnauthorizedAccessException) { return; }
                Task sender = Task.Run(async () =>
                {
                    await foreach (var item in queue.Reader.ReadAllAsync())
                    {
                        try { await stream.WriteAsync(item.Buffer.AsMemory(0, item.Length)).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch { client.Dispose(); break; }
                    }
                });
                var pcm = new Pcm16BlockAssembler();
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
                        if (dropOldest)
                        {
                            // El puerto de audio solo encola muestras completas: DropOldest conserva paridad.
                            byte[] complete = pcm.Push(b, br);
                            if (complete.Length > 0) callback(sessionId, Send, complete, complete.Length);
                        }
                        else callback(sessionId, Send, b, br);
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
                    onDisconnected(sessionId, Send);
                    client.Dispose();
                    // A peer can close its write half while no longer reading.
                    // Do not keep the session (and its TX lock) alive forever
                    // while the outgoing writer is blocked on that socket.
                    try { await sender.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
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

using QDNH.Security;
using QDNH.Settings;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QDNH.Network
{
    /// <summary>
    /// Cuarto puerto (NetworkPort+3), solo de salida: reparte a cada
    /// cliente conectado el PCM del receptor SDR remoto de MultiRX. Nunca
    /// lleva comandos ni audio de la radio local, y lo que el cliente
    /// escriba aqui se descarta (QDockX manda los 16 bytes de su SessionId
    /// tras autenticarse, igual que en los demas canales; no hacen falta
    /// para repartir audio).
    ///
    /// Mismo reto de contraseña que el resto de canales (o cuentas
    /// individuales si estan configuradas). Cada cliente tiene su propia
    /// cola acotada: uno lento pierde audio antiguo, pero no frena a los
    /// demas ni al receptor.
    /// </summary>
    public sealed class SdrListen
    {
        private readonly TcpListener listener;
        private readonly OperatorAuthentication? authentication;
        private readonly Action<int> listenersChanged;
        private readonly ConcurrentDictionary<TcpClient, Channel<byte[]>> clients = new();
        private readonly ConcurrentDictionary<TcpClient, byte> pending = new();
        private readonly CancellationTokenSource stop = new();
        private readonly object countGate = new();
        private readonly Task loop;

        public int BoundPort => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Listeners => clients.Count;

        public SdrListen(int port, Action<int> listenersChanged, OperatorAuthentication? authentication = null)
        {
            this.listenersChanged = listenersChanged;
            this.authentication = authentication;
            listener = new(IPAddress.Any, port);
            listener.Start();
            loop = Task.Run(Loop);
        }

        /// <summary>Reparte un bloque de PCM (longitud par) a todos los oyentes.</summary>
        public void Broadcast(byte[] data, int length)
        {
            if (length <= 0 || clients.IsEmpty) return;
            byte[] copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            foreach (var queue in clients.Values) queue.Writer.TryWrite(copy);
        }

        public void Close()
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            try { listener.Stop(); } catch { }
            foreach (var client in clients.Keys) client.Dispose();
            foreach (var client in pending.Keys) client.Dispose();
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        private async Task Loop()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { if (stop.IsCancellationRequested) break; await Task.Delay(100); continue; }
                if (clients.Count + pending.Count >= 64) { client.Dispose(); continue; }
                pending.TryAdd(client, 0);
                _ = Task.Run(() => HandleClient(client));
            }
        }

        private void NotifyCount()
        {
            // Serializado para que los avisos lleguen en orden.
            lock (countGate) { try { listenersChanged(clients.Count); } catch { } }
        }

        private async Task HandleClient(TcpClient client)
        {
            using (client)
            {
                bool registered = false;
                try
                {
                    try { client.NoDelay = true; } catch { }
                    NetworkStream stream = client.GetStream();
                    Authenticator auth = new(Vars.Password);
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        await stream.WriteAsync(auth.Salt, timeout.Token);
                        byte[] challenge = new byte[auth.Hash.Length];
                        int received = 0;
                        while (received < challenge.Length)
                        {
                            int count = await stream.ReadAsync(challenge.AsMemory(received), timeout.Token);
                            if (count == 0) return;
                            received += count;
                        }
                        OperatorPrincipal? principal = authentication?.Authenticate(challenge, auth.Salt);
                        if (authentication != null ? principal == null : !auth.Challenge(challenge)) return;
                    }
                    // ~1,5 s de audio como maximo en cola por cliente.
                    var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                    });
                    clients[client] = queue;
                    pending.TryRemove(client, out _);
                    registered = true;
                    NotifyCount();
                    Task sender = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (byte[] block in queue.Reader.ReadAllAsync(stop.Token))
                                await stream.WriteAsync(block).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                        }
                        catch { }
                        finally { client.Dispose(); }
                    });
                    // Lo que mande el cliente no se usa: se lee solo para
                    // enterarse de cuando cierra.
                    byte[] discard = new byte[256];
                    try { while (await stream.ReadAsync(discard, stop.Token) > 0) { } } catch { }
                    queue.Writer.TryComplete();
                    client.Dispose();
                    try { await sender.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                }
                catch { }
                finally
                {
                    pending.TryRemove(client, out _);
                    if (registered && clients.TryRemove(client, out _)) NotifyCount();
                }
            }
        }
    }
}

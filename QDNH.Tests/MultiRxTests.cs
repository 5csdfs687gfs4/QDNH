using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QDNH.Audio;
using QDNH.Network;
using QDNH.Settings;
using QDNH.SharedRadio;
using Xunit;

namespace QDNH.Tests;

/// <summary>
/// MultiRX: decodificador de audio de OpenWebRX, remuestreo, protocolo,
/// permisos de sintonia, puerto de audio SDR y el receptor completo contra
/// un OpenWebRX simulado en local.
///
/// Varias pruebas usan sockets reales con plazos; se ejecutan en una
/// coleccion sin paralelismo para no competir por CPU con el resto de la
/// suite en maquinas pequeñas (CI).
/// </summary>
[CollectionDefinition("MultiRX", DisableParallelization = true)]
public class MultiRxCollection { }

[Collection("MultiRX")]
public class MultiRxTests
{
    // ---------- utilidades ----------

    private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
        1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
        12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    /// <summary>Codificador IMA-ADPCM de referencia con el mismo formato de
    /// bloques "SYNC" que emite OpenWebRX. Devuelve tambien lo que un
    /// decodificador correcto debe reconstruir.</summary>
    private static (byte[] Stream, short[] Expected) Encode(short[] samples)
    {
        var stream = new List<byte>(); var expected = new List<short>();
        int predictor = 0, index = 0, pos = 0;
        const int perBlock = ImaAdpcmSyncDecoder.BlockBytes * 2;
        while (pos + perBlock <= samples.Length)
        {
            stream.AddRange(Encoding.ASCII.GetBytes("SYNC"));
            stream.AddRange(BitConverter.GetBytes((short)index));
            stream.AddRange(BitConverter.GetBytes((short)predictor));
            for (int i = 0; i < perBlock; i += 2)
            {
                int low = Nibble(samples[pos + i], ref predictor, ref index); expected.Add((short)predictor);
                int high = Nibble(samples[pos + i + 1], ref predictor, ref index); expected.Add((short)predictor);
                stream.Add((byte)(low | (high << 4)));
            }
            pos += perBlock;
        }
        return (stream.ToArray(), expected.ToArray());
    }

    private static int Nibble(short sample, ref int predictor, ref int index)
    {
        int step = StepTable[index], diff = sample - predictor, nibble = 0;
        if (diff < 0) { nibble = 8; diff = -diff; }
        if (diff >= step) { nibble |= 4; diff -= step; }
        if (diff >= step >> 1) { nibble |= 2; diff -= step >> 1; }
        if (diff >= step >> 2) nibble |= 1;
        int delta = step >> 3;
        if ((nibble & 1) != 0) delta += step >> 2;
        if ((nibble & 2) != 0) delta += step >> 1;
        if ((nibble & 4) != 0) delta += step;
        if ((nibble & 8) != 0) delta = -delta;
        predictor = Math.Clamp(predictor + delta, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[nibble], 0, 88);
        return nibble;
    }

    private static short[] Tone(int samples, int rate, double hz, double amplitude = 8000) =>
        Enumerable.Range(0, samples).Select(i => (short)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    private static bool WaitFor(Func<bool> condition, int milliseconds = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { if (condition()) return true; Thread.Sleep(20); }
        return condition();
    }

    private static List<(ControlMsgType Type, string Json)> Frames(List<byte[]> raw)
    {
        lock (raw) return raw.Select(f => ((ControlMsgType)f[0], Encoding.UTF8.GetString(f, 5, f.Length - 5))).ToList();
    }

    private static Action<byte[]> Collector(List<byte[]> into) => frame => { lock (into) into.Add(frame); };

    // ---------- decodificador ----------

    [Fact]
    public void AdpcmDecoderReconstructsStreamRegardlessOfChunkingAndLeadingGarbage()
    {
        var (stream, expected) = Encode(Tone(ImaAdpcmSyncDecoder.BlockBytes * 2 * 3, 12000, 700));
        byte[] withGarbage = new byte[] { 1, 2, (byte)'S', (byte)'Y', 9 }.Concat(stream).ToArray();

        var whole = new ImaAdpcmSyncDecoder();
        short[] output = new short[withGarbage.Length * 2];
        int count = whole.Decode(withGarbage, output);
        Assert.Equal(expected, output.Take(count).ToArray());

        var chunked = new ImaAdpcmSyncDecoder(); var collected = new List<short>(); var random = new Random(7);
        for (int offset = 0; offset < withGarbage.Length;)
        {
            int size = Math.Min(random.Next(1, 400), withGarbage.Length - offset);
            int n = chunked.Decode(withGarbage.AsSpan(offset, size), output);
            collected.AddRange(output.Take(n)); offset += size;
        }
        Assert.Equal(expected, collected.ToArray());
        // La reconstruccion debe parecerse de verdad a la señal original.
        short[] original = Tone(expected.Length, 12000, 700);
        double error = Enumerable.Range(500, expected.Length - 500).Average(i => Math.Abs(expected[i] - original[i]));
        Assert.True(error < 400, $"error medio {error}");
    }

    [Fact]
    public void AdpcmDecoderProducesNothingWithoutSyncWord()
    {
        var decoder = new ImaAdpcmSyncDecoder();
        byte[] noise = Enumerable.Range(0, 3000).Select(i => (byte)(i * 31 % 200)).ToArray();
        Assert.Equal(0, decoder.Decode(noise, new short[noise.Length * 2]));
    }

    // ---------- remuestreo ----------

    [Fact]
    public void ResamplerConvertsRateKeepsPitchAndIsIndependentOfChunking()
    {
        short[] input = Tone(12000, 12000, 1000);
        var whole = new PcmResampler(12000, 22050);
        short[] output = new short[whole.MaxOutput(input.Length)];
        int count = whole.Process(input, output);
        Assert.InRange(count, 22045, 22050);
        int crossings = Enumerable.Range(1, count - 1).Count(i => output[i - 1] < 0 && output[i] >= 0);
        Assert.InRange(crossings, 998, 1001); // sigue siendo un tono de 1 kHz durante 1 s

        var chunked = new PcmResampler(12000, 22050); var collected = new List<short>(); var random = new Random(3);
        short[] scratch = new short[2048];
        for (int offset = 0; offset < input.Length;)
        {
            int size = Math.Min(random.Next(1, 500), input.Length - offset);
            int n = chunked.Process(input.AsSpan(offset, size), scratch);
            collected.AddRange(scratch.Take(n)); offset += size;
        }
        Assert.Equal(output.Take(count).ToArray(), collected.ToArray());
    }

    // ---------- protocolo ----------

    [Theory]
    [InlineData("http://sdr.example.org:8073", "ws://sdr.example.org:8073/ws/")]
    [InlineData("sdr.example.org:8073/", "ws://sdr.example.org:8073/ws/")]
    [InlineData("https://sdr.example.org", "wss://sdr.example.org/ws/")]
    [InlineData("ws://sdr.example.org:8073/ws/", "ws://sdr.example.org:8073/ws/")]
    [InlineData("ws://sdr.example.org/otro/ws/", "ws://sdr.example.org/otro/ws/")]
    public void UrlIsNormalizedToWebSocketEndpoint(string input, string expected) =>
        Assert.Equal(expected, OpenWebRxProtocol.NormalizeUrl(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://sdr.example.org")]
    public void InvalidUrlIsRejected(string input) => Assert.Null(OpenWebRxProtocol.NormalizeUrl(input));

    [Fact]
    public void ServerMessagesAreParsedAndUnknownOnesIgnored()
    {
        var config = OpenWebRxProtocol.Parse("{\"type\":\"config\",\"value\":{\"center_freq\":145000000,\"samp_rate\":2400000.0,\"audio_compression\":\"adpcm\"}}");
        Assert.Equal(145000000, config!.Value.CenterFreq);
        Assert.Equal(2400000, config.Value.SampleRate);
        Assert.Equal("adpcm", config.Value.AudioCompression);
        Assert.Null(OpenWebRxProtocol.Parse("{\"type\":\"config\",\"value\":{\"waterfall_levels\":1}}")!.Value.CenterFreq);
        Assert.Equal(-71.5, OpenWebRxProtocol.Parse("{\"type\":\"smeter\",\"value\":-71.5}")!.Value.Smeter);
        Assert.Contains("ocupado", OpenWebRxProtocol.Parse("{\"type\":\"backoff\",\"reason\":\"too many clients\"}")!.Value.Error);
        Assert.Null(OpenWebRxProtocol.Parse("no es json"));
        Assert.Null(OpenWebRxProtocol.Parse("[1,2]"));
        using var dsp = JsonDocument.Parse(OpenWebRxProtocol.DspControl(-125000, "nfm", -5000, 5000));
        var p = dsp.RootElement.GetProperty("params");
        Assert.Equal("dspcontrol", dsp.RootElement.GetProperty("type").GetString());
        Assert.Equal(-125000, p.GetProperty("offset_freq").GetInt64());
        Assert.Equal("nfm", p.GetProperty("mod").GetString());
        Assert.Equal(5000, p.GetProperty("high_cut").GetInt32());
    }

    // ---------- permisos de sintonia (SessionManager) ----------

    [Fact]
    public void SdrTuneRequiresControlLockAndReportsHandlerReason()
    {
        using var manager = new SessionManager();
        var requests = new List<SdrTuneMessage>();
        string? handlerReason = null;
        Guid owner = Guid.NewGuid(), other = Guid.NewGuid();
        var ownerFrames = new List<byte[]>(); var otherFrames = new List<byte[]>();
        manager.RegisterOrAttach(owner, "EA7AAA", false, Collector(ownerFrames), 2);
        manager.RegisterOrAttach(other, "EA7BBB", false, Collector(otherFrames), 2);
        var tune = new SdrTuneMessage(145_500_000, "nfm");

        manager.TryRequestSdrTune(owner, tune); // MultiRX apagado
        Assert.Contains(Frames(ownerFrames), f => f.Type == ControlMsgType.SdrTuneDenied && f.Json.Contains("SDR_DISABLED"));

        manager.SdrTuneHandler = request => { requests.Add(request); return handlerReason; };
        manager.TryRequestControl(owner);
        manager.TryRequestSdrTune(other, tune); // sin el control
        Assert.Contains(Frames(otherFrames), f => f.Type == ControlMsgType.SdrTuneDenied && f.Json.Contains("CONTROL_REQUIRED"));
        Assert.Empty(requests);

        lock (ownerFrames) ownerFrames.Clear();
        manager.TryRequestSdrTune(owner, tune);
        Assert.Single(requests);
        Assert.DoesNotContain(Frames(ownerFrames), f => f.Type == ControlMsgType.SdrTuneDenied);

        handlerReason = "OUT_OF_RANGE";
        manager.TryRequestSdrTune(owner, tune with { FrequencyHz = 433_000_000 });
        Assert.Contains(Frames(ownerFrames), f => f.Type == ControlMsgType.SdrTuneDenied && f.Json.Contains("OUT_OF_RANGE"));
    }

    [Fact]
    public void SdrStatusReachesOnlyExtensionAwareClientsAndIsSentOnHello()
    {
        using var manager = new SessionManager();
        var status = new SdrStatusMessage(true, true, 145_500_000, "nfm", -70, null);
        var legacy = new List<byte[]>(); var modern = new List<byte[]>(); var late = new List<byte[]>();
        manager.RegisterOrAttach(Guid.NewGuid(), "EA7OLD", false, Collector(legacy), 1);
        manager.RegisterOrAttach(Guid.NewGuid(), "EA7NEW", false, Collector(modern), 2);
        Assert.DoesNotContain(Frames(modern), f => f.Type == ControlMsgType.SdrStatus); // MultiRX apagado: nada nuevo
        Assert.DoesNotContain(Frames(modern), f => f.Json.Contains("multirx-v1"));

        manager.BroadcastSdrStatus(status);
        Assert.Contains(Frames(modern), f => f.Type == ControlMsgType.SdrStatus && f.Json.Contains("145500000"));
        Assert.DoesNotContain(Frames(legacy), f => f.Type == ControlMsgType.SdrStatus);

        manager.SdrTuneHandler = _ => null;
        manager.SdrStatusProvider = () => status;
        manager.RegisterOrAttach(Guid.NewGuid(), "EA7LATE", false, Collector(late), 2);
        var frames = Frames(late);
        Assert.Contains(frames, f => f.Type == ControlMsgType.HelloAck && f.Json.Contains("multirx-v1"));
        Assert.Contains(frames, f => f.Type == ControlMsgType.SdrStatus);
    }

    // ---------- puerto de audio SDR ----------

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        return port;
    }

    private static void ReadExactly(NetworkStream stream, byte[] buffer)
    {
        for (int got = 0; got < buffer.Length;)
        {
            int n = stream.Read(buffer, got, buffer.Length - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
    }

    /// <summary>Conecta y se autentica como lo hace QDockX (reto + 16
    /// bytes de SessionId). Otras pruebas cambian Vars.Password en
    /// paralelo, asi que se reintenta si justo coincide.</summary>
    private static TcpClient ConnectSdr(int port, SdrListen server, int expectedListeners)
    {
        for (int attempt = 0; ; attempt++)
        {
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 5000;
            NetworkStream stream = client.GetStream();
            byte[] salt = new byte[32]; ReadExactly(stream, salt);
            stream.Write(SHA256.HashData(Encoding.ASCII.GetBytes(Vars.Password).Concat(salt).ToArray()));
            stream.Write(Guid.NewGuid().ToByteArray());
            if (WaitFor(() => server.Listeners >= expectedListeners, 1500) || attempt >= 4) return client;
            client.Dispose();
        }
    }

    [Fact]
    public void SdrPortServesPcmToAuthenticatedClientsOnlyAndReportsListeners()
    {
        var counts = new List<int>();
        int port = FreePort();
        var server = new SdrListen(port, n => { lock (counts) counts.Add(n); });
        try
        {
            using var intruder = new TcpClient();
            intruder.Connect(IPAddress.Loopback, port);
            intruder.ReceiveTimeout = 3000;
            var bad = intruder.GetStream();
            byte[] salt = new byte[32]; ReadExactly(bad, salt);
            bad.Write(new byte[32]); // respuesta incorrecta al reto
            Assert.True(WaitFor(() => { try { return bad.Read(new byte[1], 0, 1) == 0; } catch (IOException) { return true; } }));
            Assert.Equal(0, server.Listeners);

            using var first = ConnectSdr(port, server, 1);
            using var second = ConnectSdr(port, server, 2);
            Assert.Equal(2, server.Listeners);

            byte[] block = Enumerable.Range(0, 400).Select(i => (byte)i).ToArray();
            server.Broadcast(block, block.Length);
            foreach (var client in new[] { first, second })
            {
                byte[] received = new byte[block.Length];
                ReadExactly(client.GetStream(), received);
                Assert.Equal(block, received);
            }

            first.Dispose();
            Assert.True(WaitFor(() => server.Listeners == 1));
            second.Dispose();
            Assert.True(WaitFor(() => server.Listeners == 0));
            lock (counts) Assert.Equal(new[] { 1, 2, 1, 0 }, counts.ToArray());
        }
        finally { server.Close(); }
    }

    // ---------- receptor completo contra un OpenWebRX simulado ----------

    /// <summary>Servidor WebSocket minimo que se comporta como OpenWebRX:
    /// espera el saludo, anuncia la configuracion y, cuando le arrancan el
    /// DSP, manda audio ADPCM, FFT (que debe ignorarse) y s-meter.</summary>
    private sealed class FakeOpenWebRx : IDisposable
    {
        private readonly HttpListener http = new();
        private readonly CancellationTokenSource stop = new();
        public readonly List<string> Received = new();
        public int Connections;
        public string Url { get; }
        public long CenterFreq = 145_000_000, SampleRate = 2_400_000;
        public byte[] Audio = Array.Empty<byte>();

        public FakeOpenWebRx()
        {
            int port = FreePort();
            http.Prefixes.Add($"http://127.0.0.1:{port}/");
            http.Start();
            Url = $"http://127.0.0.1:{port}";
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await http.GetContextAsync(); } catch { return; }
                if (!context.Request.IsWebSocketRequest) { context.Response.StatusCode = 400; context.Response.Close(); continue; }
                _ = Task.Run(() => Serve(context));
            }
        }

        private async Task Serve(HttpListenerContext context)
        {
            try
            {
                WebSocket ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                Interlocked.Increment(ref Connections);
                byte[] buffer = new byte[8192];
                bool streaming = false;
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    lock (Received) Received.Add(text);
                    if (text.StartsWith("SERVER DE CLIENT"))
                        await Send(ws, $"{{\"type\":\"config\",\"value\":{{\"center_freq\":{CenterFreq},\"samp_rate\":{SampleRate},\"audio_compression\":\"adpcm\"}}}}");
                    if (text.Contains("\"action\":\"start\"") && !streaming)
                    {
                        streaming = true;
                        _ = Task.Run(async () =>
                        {
                            await ws.SendAsync(new byte[] { 1, 9, 9, 9 }, WebSocketMessageType.Binary, true, stop.Token); // FFT
                            await Send(ws, "{\"type\":\"smeter\",\"value\":-63.25}");
                            for (int offset = 0; offset < Audio.Length; offset += 500)
                            {
                                byte[] frame = new byte[] { 2 }.Concat(Audio.Skip(offset).Take(500)).ToArray();
                                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, stop.Token);
                            }
                        });
                    }
                }
            }
            catch { }
        }

        private Task Send(WebSocket ws, string text) => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, stop.Token);

        public List<string> Snapshot() { lock (Received) return Received.ToList(); }

        public void Dispose() { stop.Cancel(); try { http.Stop(); } catch { } }
    }

    [Fact]
    public void ReceiverTunesDecodesResamplesRetunesAndFreesTheSdrWhenIdle()
    {
        using var sdr = new FakeOpenWebRx();
        var (adpcm, decoded) = Encode(Tone(ImaAdpcmSyncDecoder.BlockBytes * 2 * 6, 12000, 1000));
        sdr.Audio = adpcm;
        var pcm = new List<byte>(); var statuses = new List<SdrStatusMessage>();
        using var receiver = new OpenWebRxReceiver(sdr.Url, 145_500_000, "nfm",
            (data, length) => { lock (pcm) pcm.AddRange(data.Take(length)); },
            status => { lock (statuses) statuses.Add(status); },
            null, 22050, TimeSpan.FromMilliseconds(200));

        Thread.Sleep(300);
        Assert.Equal(0, sdr.Connections); // sin oyentes no ocupa plaza en el SDR
        Assert.False(receiver.Status.Connected);

        receiver.SetActive(true);
        int expectedBytes = (int)(decoded.Length * 22050L / 12000) * 2;
        Assert.True(WaitFor(() => { lock (pcm) return pcm.Count >= expectedBytes - 8; }), "no llego todo el audio");
        lock (pcm)
        {
            Assert.Equal(0, pcm.Count % 2);
            Assert.InRange(pcm.Count, expectedBytes - 8, expectedBytes + 8);
            short[] samples = Enumerable.Range(0, pcm.Count / 2).Select(i => BitConverter.ToInt16(pcm.ToArray(), i * 2)).ToArray();
            int crossings = Enumerable.Range(1, samples.Length - 1).Count(i => samples[i - 1] < 0 && samples[i] >= 0);
            double seconds = samples.Length / 22050.0;
            Assert.InRange(crossings / seconds, 990, 1010); // el tono de 1 kHz sale a la tasa correcta
        }
        var sent = sdr.Snapshot();
        Assert.Equal(OpenWebRxProtocol.Handshake, sent[0]);
        Assert.Contains(sent, t => t.Contains("connectionproperties") && t.Contains("12000"));
        Assert.Contains(sent, t => t.Contains("\"offset_freq\":500000") && t.Contains("\"mod\":\"nfm\""));
        Assert.Single(sent, t => t.Contains("\"action\":\"start\""));
        Assert.True(WaitFor(() => receiver.Status is { Connected: true, DspStarted: true, SmeterRaw: -63.25 }));
        lock (statuses) Assert.Contains(statuses, s => s.Connected && s.DspStarted && s.FrequencyHz == 145_500_000);

        // Resintonia dentro del perfil: nuevo offset, sin volver a arrancar el DSP.
        Assert.Null(receiver.Tune(144_800_000, "FM", -5000, 5000));
        Assert.True(WaitFor(() => sdr.Snapshot().Any(t => t.Contains("\"offset_freq\":-200000"))));
        Assert.Single(sdr.Snapshot(), t => t.Contains("\"action\":\"start\""));
        Assert.Equal(144_800_000, receiver.Status.FrequencyHz);

        // Fuera de lo que cubre el perfil activo, o peticiones absurdas: se rechazan y no se toca nada.
        Assert.Equal("OUT_OF_RANGE", receiver.Tune(433_500_000, "nfm", -5000, 5000));
        Assert.Equal("INVALID_MODULATION", receiver.Tune(145_000_000, "wfm", -5000, 5000));
        Assert.Equal("INVALID_FILTER", receiver.Tune(145_000_000, "nfm", 5000, -5000));
        Assert.Equal("INVALID_FREQUENCY", receiver.Tune(0, "nfm", -5000, 5000));
        Assert.Equal(144_800_000, receiver.Status.FrequencyHz);

        // Sin oyentes: tras el margen suelta la conexion y no vuelve a conectar.
        receiver.SetActive(false);
        Assert.True(WaitFor(() => !receiver.Status.Connected));
        int connections = sdr.Connections;
        Thread.Sleep(600);
        Assert.Equal(connections, sdr.Connections);
        Assert.Equal(1, connections);
    }

    [Fact]
    public void ReceiverReportsUnreachableSdrWithoutThrowing()
    {
        var statuses = new List<SdrStatusMessage>();
        using var receiver = new OpenWebRxReceiver($"http://127.0.0.1:{FreePort()}", 145_500_000, "nfm",
            (_, _) => { }, status => { lock (statuses) statuses.Add(status); });
        receiver.SetActive(true);
        Assert.True(WaitFor(() => receiver.Status.LastError != null));
        Assert.False(receiver.Status.Connected);
    }
}

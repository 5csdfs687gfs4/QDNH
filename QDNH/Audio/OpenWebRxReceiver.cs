using QDNH.SharedRadio;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QDNH.Audio
{
    /// <summary>
    /// Parte pura (sin red) del protocolo de cliente de OpenWebRX que usa
    /// MultiRX: textos que se envian y lectura de los mensajes JSON que
    /// llegan. Separada para poder probarla sin un servidor real.
    /// </summary>
    public static class OpenWebRxProtocol
    {
        public const string Handshake = "SERVER DE CLIENT client=openwebrx.js type=receiver";
        public const int AudioRate = 12000;
        public const byte FrameAudio = 2;
        public static readonly string[] Modulations = { "nfm", "am", "lsb", "usb", "cw" };

        /// <summary>Acepta lo que un operador escribiria (http://host:8073,
        /// host:8073/ws/, ws://...) y devuelve la URL WebSocket real, o
        /// null si no es valida.</summary>
        public static string? NormalizeUrl(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (!text.Contains("://")) text = "ws://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return null;
            string scheme = uri.Scheme switch { "http" or "ws" => "ws", "https" or "wss" => "wss", _ => "" };
            if (scheme.Length == 0 || string.IsNullOrEmpty(uri.Host)) return null;
            string path = uri.AbsolutePath;
            if (path.Length == 0 || path == "/") path = "/ws/";
            return new UriBuilder(scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, path).Uri.ToString();
        }

        public static string ConnectionProperties() =>
            JsonSerializer.Serialize(new { type = "connectionproperties", @params = new { output_rate = AudioRate, hd_output_rate = 48000 } });

        public static string DspControl(long offsetHz, string modulation, int lowCut, int highCut) =>
            JsonSerializer.Serialize(new
            {
                type = "dspcontrol",
                @params = new { low_cut = lowCut, high_cut = highCut, offset_freq = offsetHz, mod = modulation, dmr_filter = 3, audio_service_id = 0, secondary_mod = false },
            });

        public static string DspStart() => JsonSerializer.Serialize(new { type = "dspcontrol", action = "start" });

        /// <summary>Filtro por defecto de cada modo cuando nadie indica otro.</summary>
        public static (int Low, int High) DefaultFilter(string modulation) => modulation switch
        {
            "am" => (-4000, 4000),
            "usb" => (300, 3000),
            "lsb" => (-3000, -300),
            "cw" => (700, 900),
            _ => (-5000, 5000),
        };

        public readonly record struct ServerMessage(string Type, long? CenterFreq, long? SampleRate, string? AudioCompression, double? Smeter, string? Error);

        public static ServerMessage? Parse(string text)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement typeEl) || typeEl.ValueKind != JsonValueKind.String) return null;
                string type = typeEl.GetString() ?? "";
                bool hasValue = root.TryGetProperty("value", out JsonElement value);
                switch (type)
                {
                    case "config" when hasValue && value.ValueKind == JsonValueKind.Object:
                        return new(type, Number(value, "center_freq"), Number(value, "samp_rate"),
                            value.TryGetProperty("audio_compression", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null, null, null);
                    case "smeter" when hasValue && value.ValueKind == JsonValueKind.Number:
                        return new(type, null, null, null, value.GetDouble(), null);
                    case "backoff":
                        return new(type, null, null, null, null, "SDR ocupado: " + (root.TryGetProperty("reason", out JsonElement r) ? r.ToString() : "sin plazas"));
                    case "sdr_error":
                    case "demodulator_error":
                        return new(type, null, null, null, null, hasValue ? value.ToString() : type);
                    default:
                        return new(type, null, null, null, null, null);
                }
            }
            catch (JsonException) { return null; }
        }

        private static long? Number(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? (long)Math.Round(e.GetDouble()) : null;
    }

    /// <summary>
    /// MultiRX: segundo receptor opcional. Se conecta como un cliente web
    /// mas a un OpenWebRX remoto, le pide una frecuencia y entrega el
    /// audio demodulado como PCM mono de 16 bits a 22050 Hz (lo mismo que
    /// el resto de canales de audio de QDNH).
    ///
    /// Reglas de diseño:
    ///  - Solo mantiene la conexion mientras haya algun oyente (SetActive),
    ///    para no ocupar una plaza del SDR sin necesidad.
    ///  - Nunca cambia de perfil en el SDR: si la frecuencia pedida cae
    ///    fuera de lo que el perfil activo cubre, se deniega/avisa.
    ///  - Es totalmente independiente de la radio local: un fallo aqui no
    ///    toca audio, serie, TX ni la salud del servidor.
    /// </summary>
    public sealed class OpenWebRxReceiver : IDisposable
    {
        private readonly Uri uri;
        private readonly Action<byte[], int> pcmSink;
        private readonly Action<SdrStatusMessage> statusSink;
        private readonly Action<string> log;
        private readonly int targetRate;
        private readonly TimeSpan linger;
        private readonly object gate = new();
        private readonly CancellationTokenSource disposeCts = new();
        private readonly SemaphoreSlim wake = new(0, 1);
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly Task loop;

        // Estado protegido por 'gate'.
        private long frequencyHz;
        private string modulation;
        private int lowCut, highCut;
        private bool active, connected, dspStarted;
        private long? centerFreq, sampleRate;
        private double? smeter;
        private string? lastError;
        private long activeVersion;
        private ClientWebSocket? socket;
        private CancellationTokenSource? connectionCts;
        private long lastSmeterNotifyTick;

        public OpenWebRxReceiver(string url, long frequencyHz, string modulation, Action<byte[], int> pcmSink,
            Action<SdrStatusMessage> statusSink, Action<string>? log = null, int targetRate = 22050, TimeSpan? linger = null)
        {
            uri = new Uri(OpenWebRxProtocol.NormalizeUrl(url) ?? throw new ArgumentException("URL de OpenWebRX no valida.", nameof(url)));
            this.frequencyHz = frequencyHz;
            this.modulation = Array.IndexOf(OpenWebRxProtocol.Modulations, modulation) >= 0 ? modulation : "nfm";
            (lowCut, highCut) = OpenWebRxProtocol.DefaultFilter(this.modulation);
            this.pcmSink = pcmSink;
            this.statusSink = statusSink;
            this.log = log ?? (_ => { });
            this.targetRate = targetRate;
            this.linger = linger ?? TimeSpan.FromSeconds(5);
            loop = Task.Run(RunAsync);
        }

        public SdrStatusMessage Status
        {
            get { lock (gate) return new(connected, dspStarted, frequencyHz, modulation, smeter, lastError); }
        }

        /// <summary>Hay (o deja de haber) oyentes del audio SDR.</summary>
        public void SetActive(bool value)
        {
            long version;
            lock (gate)
            {
                if (active == value) return;
                active = value;
                version = ++activeVersion;
            }
            if (value) { try { wake.Release(); } catch (SemaphoreFullException) { } return; }
            // Sin oyentes: se da un margen por si es una reconexion y
            // despues se suelta la plaza del SDR.
            _ = Task.Delay(linger, disposeCts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                CancellationTokenSource? cts;
                lock (gate) cts = !active && activeVersion == version ? connectionCts : null;
                try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            }, TaskScheduler.Default);
        }

        /// <summary>Pide otra frecuencia/modo. Devuelve null si se acepta o
        /// el motivo (codigo corto) si se rechaza.</summary>
        public string? Tune(long newFrequencyHz, string? newModulation, int newLowCut, int newHighCut)
        {
            string mod = (newModulation ?? "").Trim().ToLowerInvariant();
            if (mod.Length == 0) mod = "nfm";
            if (mod == "fm") mod = "nfm";
            if (Array.IndexOf(OpenWebRxProtocol.Modulations, mod) < 0) return "INVALID_MODULATION";
            if (newFrequencyHz <= 0 || newFrequencyHz > 30_000_000_000L) return "INVALID_FREQUENCY";
            if (newLowCut >= newHighCut || newLowCut < -20000 || newHighCut > 20000) return "INVALID_FILTER";
            bool send;
            lock (gate)
            {
                if (centerFreq is long center && sampleRate is long rate && !InRange(newFrequencyHz, center, rate)) return "OUT_OF_RANGE";
                frequencyHz = newFrequencyHz; modulation = mod; lowCut = newLowCut; highCut = newHighCut;
                lastError = null;
                send = connected && centerFreq != null;
            }
            if (send) _ = SendDspAsync();
            Notify();
            return null;
        }

        private static bool InRange(long frequency, long center, long rate) => Math.Abs(frequency - center) < rate / 2;

        private void Notify()
        {
            try { statusSink(Status); } catch { }
        }

        private async Task RunAsync()
        {
            CancellationToken stop = disposeCts.Token;
            int failures = 0;
            while (!stop.IsCancellationRequested)
            {
                bool isActive;
                lock (gate) isActive = active;
                if (!isActive)
                {
                    try { await wake.WaitAsync(stop); } catch (OperationCanceledException) { break; }
                    continue;
                }
                bool gotAudio = false;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                try
                {
                    lock (gate) { socket = ws; connectionCts = cts; centerFreq = null; sampleRate = null; smeter = null; }
                    using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                    {
                        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        await ws.ConnectAsync(uri, connectTimeout.Token);
                    }
                    await SendTextAsync(ws, OpenWebRxProtocol.Handshake, cts.Token);
                    await SendTextAsync(ws, OpenWebRxProtocol.ConnectionProperties(), cts.Token);
                    lock (gate) { connected = true; lastError = null; }
                    log($"[MultiRX] conectado a {uri}");
                    Notify();
                    gotAudio = await ReceiveAsync(ws, cts.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (OperationCanceledException)
                {
                    bool stillActive;
                    lock (gate) stillActive = active;
                    if (stillActive) lock (gate) lastError = "sin respuesta del SDR";
                }
                catch (Exception ex)
                {
                    lock (gate) lastError = ex is WebSocketException or IOException ? "no se pudo conectar con el SDR" : ex.Message;
                    log($"[MultiRX] error de conexion: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    bool wasConnected;
                    lock (gate) { wasConnected = connected; socket = null; connectionCts = null; connected = false; dspStarted = false; smeter = null; }
                    if (wasConnected) log("[MultiRX] desconectado del SDR");
                    Notify();
                }
                if (stop.IsCancellationRequested) break;
                failures = gotAudio ? 0 : Math.Min(failures + 1, 4);
                lock (gate) isActive = active;
                if (isActive)
                {
                    // Reintentos espaciados (4, 8, 16, 30 s): es un SDR
                    // compartido y no hay que martillearlo si rechaza.
                    int seconds = Math.Min(30, 2 << failures) / (gotAudio ? 2 : 1);
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), stop); } catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task<bool> ReceiveAsync(ClientWebSocket ws, CancellationToken token)
        {
            var decoder = new ImaAdpcmSyncDecoder();
            var resampler = new PcmResampler(OpenWebRxProtocol.AudioRate, targetRate);
            bool compressed = true, gotAudio = false;
            byte[] buffer = new byte[32768];
            short[] decoded = new short[4096], resampled = new short[8192];
            byte[] pcm = new byte[16384];
            using var message = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(), token);
                    if (result.MessageType == WebSocketMessageType.Close) return gotAudio;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("mensaje del SDR demasiado grande");
                } while (!result.EndOfMessage);
                ReadOnlySpan<byte> data = message.GetBuffer().AsSpan(0, (int)message.Length);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (OpenWebRxProtocol.Parse(Encoding.UTF8.GetString(data)) is { } msg && await HandleTextAsync(msg) is bool newCompression)
                        compressed = newCompression;
                    continue;
                }
                if (data.Length < 2 || data[0] != OpenWebRxProtocol.FrameAudio) continue; // FFT y demas: no interesan
                ReadOnlySpan<byte> audio = data[1..];
                int samples;
                if (compressed)
                {
                    if (decoded.Length < audio.Length * 2) decoded = new short[audio.Length * 2];
                    samples = decoder.Decode(audio, decoded);
                }
                else
                {
                    samples = audio.Length / 2;
                    if (decoded.Length < samples) decoded = new short[samples];
                    for (int i = 0; i < samples; i++) decoded[i] = BinaryPrimitives.ReadInt16LittleEndian(audio.Slice(i * 2, 2));
                }
                if (samples == 0) continue;
                int need = resampler.MaxOutput(samples);
                if (resampled.Length < need) resampled = new short[need];
                int produced = resampler.Process(decoded.AsSpan(0, samples), resampled);
                if (produced == 0) continue;
                if (pcm.Length < produced * 2) pcm = new byte[produced * 2];
                for (int i = 0; i < produced; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), resampled[i]);
                gotAudio = true;
                try { pcmSink(pcm, produced * 2); } catch { }
            }
            return gotAudio;
        }

        /// <summary>Devuelve el nuevo estado de compresion si el mensaje lo cambia.</summary>
        private async Task<bool?> HandleTextAsync(OpenWebRxProtocol.ServerMessage msg)
        {
            bool? compression = msg.AudioCompression switch { "adpcm" => true, "none" => false, _ => null };
            bool retune = false, notify = false;
            lock (gate)
            {
                if (msg.CenterFreq is long center && center != centerFreq) { centerFreq = center; retune = true; }
                if (msg.SampleRate is long rate && rate != sampleRate) { sampleRate = rate; retune = true; }
                if (msg.Error != null && msg.Error != lastError) { lastError = msg.Error; notify = true; }
                if (msg.Smeter is double level)
                {
                    smeter = level;
                    long now = Environment.TickCount64;
                    if (now - lastSmeterNotifyTick >= 1000) { lastSmeterNotifyTick = now; notify = true; }
                }
                retune &= centerFreq != null;
            }
            if (msg.Error != null) log($"[MultiRX] aviso del SDR: {msg.Error}");
            if (retune) await SendDspAsync();
            else if (notify) Notify();
            return compression;
        }

        /// <summary>Manda la sintonia actual y arranca el DSP si todavia
        /// no lo estaba.</summary>
        private async Task SendDspAsync()
        {
            ClientWebSocket? ws; CancellationToken token; string control; bool start;
            lock (gate)
            {
                ws = socket;
                if (ws == null || connectionCts == null || centerFreq is not long center) return;
                token = connectionCts.Token;
                if (sampleRate is long rate && !InRange(frequencyHz, center, rate))
                {
                    lastError = "frecuencia fuera del perfil activo del SDR";
                    control = "";
                }
                else
                {
                    if (lastError == "frecuencia fuera del perfil activo del SDR") lastError = null;
                    control = OpenWebRxProtocol.DspControl(frequencyHz - center, modulation, lowCut, highCut);
                }
                start = control.Length > 0 && !dspStarted;
            }
            try
            {
                if (control.Length > 0)
                {
                    await SendTextAsync(ws, control, token);
                    if (start)
                    {
                        await SendTextAsync(ws, OpenWebRxProtocol.DspStart(), token);
                        lock (gate) if (ReferenceEquals(socket, ws)) dspStarted = true;
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
            Notify();
        }

        private async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken token)
        {
            await sendLock.WaitAsync(token);
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token); }
            finally { sendLock.Release(); }
        }

        public void Dispose()
        {
            try { disposeCts.Cancel(); } catch (ObjectDisposedException) { }
            try { loop.Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
    }
}

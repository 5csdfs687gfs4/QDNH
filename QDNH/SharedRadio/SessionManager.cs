using QDNH.Security;
using QDNH.Settings;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;

namespace QDNH.SharedRadio
{
    /// <summary>
    /// Estado central de Shared Radio Mode (ago-2026): que sesiones estan
    /// conectadas, quien tiene el control (ControlOwnerSessionId) y quien
    /// tiene el TX (TxOwnerSessionId). Es el UNICO sitio que decide estas
    /// dos cosas -- Network/ControlChannel.cs solo transporta mensajes y
    /// llama aqui; Main.cs solo llama a OnTxAudio/IsControlOwner para
    /// decidir si un bloque entrante va a la radio o se descarta. Nadie
    /// mas debe tocar ControlOwnerSessionId/TxOwnerSessionId directamente.
    ///
    /// Regla acordada con el usuario: solo quien tiene el control puede
    /// pedir el TX -- TryRequestTx rechaza a cualquier sesion que no sea
    /// ademas la ControlOwner. Evita arbitrar el TX entre sesiones que ni
    /// siquiera tienen el control de la estacion.
    /// </summary>
    public class SessionManager : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, SessionInfo> sessions = new();
        private readonly object sync = new();

        public Guid? ControlOwnerSessionId { get; private set; }
        public Guid? TxOwnerSessionId { get; private set; }

        // Failsafe de lease de TX (si el titular del TX pierde la
        // conexion a mitad de una transmision, este vigilante corta el
        // TX sin esperar a que el propio cliente avise). Se renueva cada
        // vez que llega un bloque de audio TX real (ver OnTxAudio) -- si
        // pasan mas de TxLeaseTimeout sin audio nuevo, se asume perdida.
        private DateTime txLeaseExpiresUtc = DateTime.MaxValue;
        private bool monitorAnnounced;
        private TimeSpan TxLeaseTimeout => TimeSpan.FromSeconds(Vars.TxLeaseSeconds);
        private DateTime txStartedUtc;
        private bool txOutputFault;
        private readonly Func<DateTime> utcNow;
        private readonly Action releasePtt;
        public Func<(bool Serial, bool Capture, bool Playback)> HardwareStatus { get; set; } = () => (false, false, false);
        public static readonly string[] Capabilities = { "health-v1", "operators-v1", "control-queue-v1", "tx-monitor-v1" };
        private readonly List<Guid> controlQueue = new();
        private readonly Dictionary<Guid, List<byte>> serialPending = new();
        public bool AuthenticationRequired { get; private set; }
        private string[] AdvertisedCapabilities => AuthenticationRequired ? Capabilities.Concat(new[] { "operator-auth-v1" }).ToArray() : Capabilities;
        private readonly Timer txWatchdog;

        // Throttle del aviso "audio TX descartado": un cliente SIN Fase B
        // (todavia no implementa "tomar el control"/TX Request, p.ej. el
        // QDockX actual) manda su stream de microfono de forma CONTINUA
        // por el canal de audio -- igual que en el modo tradicional, donde
        // el PTT real lo decide el hardware, no el canal de audio -- asi
        // que aqui cada bloque (uno cada ~20ms) se rechaza y, sin este
        // throttle, generaba decenas de lineas de log por segundo. Eso no
        // solo ensucia la consola: en Windows, si la consola tiene
        // QuickEdit Mode activo y alguien hace clic o selecciona texto,
        // Console.Write se BLOQUEA hasta que se suelta la seleccion --
        // bloqueando este mismo hilo (CaptureCallback), acumulando
        // backlog y pudiendo acabar pareciendo un cuelgue/desconexion del
        // cliente. Se avisa como mucho una vez cada 5s por sesion.
        private readonly ConcurrentDictionary<Guid, DateTime> lastAudioRejectLogUtc = new();
        private static readonly TimeSpan AudioRejectLogInterval = TimeSpan.FromSeconds(5);

        public SessionManager(Action? releasePtt = null, Func<DateTime>? utcNow = null)
        {
            this.releasePtt = releasePtt ?? (() => { });
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            txWatchdog = new Timer(_ => CheckTxLease(), null, 1000, 1000);
        }

        public void ResetForReconfigure(bool authenticationRequired)
        {
            lock (sync)
            {
                ReleaseAnyTx("SERVER_RECONFIGURED"); ControlOwnerSessionId = null;
                sessions.Clear(); controlQueue.Clear(); serialPending.Clear(); lastAudioRejectLogUtc.Clear();
                AuthenticationRequired = authenticationRequired;
                txOutputFault = false;
            }
        }
        private SessionInfo BindSession(Guid id, OperatorPrincipal? principal, string callsign, bool monitor)
        {
            if (id == Guid.Empty || (AuthenticationRequired && principal == null)) throw new UnauthorizedAccessException("Sesión no autenticada.");
            if (sessions.TryGetValue(id, out var existing))
            {
                if (existing.Principal?.UserId != principal?.UserId) throw new UnauthorizedAccessException("La sesión pertenece a otra identidad.");
                return existing;
            }
            var session = new SessionInfo(id, principal?.Callsign ?? callsign, monitor) { Principal = principal };
            sessions[id] = session; return session;
        }

        // ------------------------------------------------------------
        // Alta / baja de sesiones
        // ------------------------------------------------------------

        /// <summary>Primer mensaje (Hello) del canal de control de una
        /// conexion nueva: crea la sesion si no existia (o la reutiliza
        /// si el audio/serie ya habian llegado antes) y contesta con el
        /// estado actual de control/TX.</summary>
        public SessionInfo RegisterOrAttach(Guid sessionId, string callsign, bool supportsTxMonitor, Action<byte[]> sendControl, int protocolVersion = 1, OperatorPrincipal? principal = null)
        {
            lock (sync)
            {
                SessionInfo session = BindSession(sessionId, principal, callsign, supportsTxMonitor);
                callsign = principal?.Callsign ?? callsign;
                session.Callsign = callsign;
                session.SupportsTxMonitor = supportsTxMonitor;
                session.SendControl = sendControl;
                session.SupportsExtensions = protocolVersion >= 2;
                Vars.Out($"[SharedRadio] sesion conectada: {callsign} ({sessionId})");

                string? controlCallsign = ControlOwnerSessionId != null && sessions.TryGetValue(ControlOwnerSessionId.Value, out var co) ? co.Callsign : null;
                string? txCallsign = TxOwnerSessionId != null && sessions.TryGetValue(TxOwnerSessionId.Value, out var to) ? to.Callsign : null;
                SendTo(session, ControlMsgType.HelloAck,
                    new HelloAckMessage(ControlOwnerSessionId != null, controlCallsign, TxOwnerSessionId != null, txCallsign, 2, Vars.Version, AdvertisedCapabilities, principal?.UserId, principal?.Role.ToString()));
                BroadcastOperators();
                if (supportsTxMonitor && monitorAnnounced && TxOwnerSessionId != sessionId &&
                    TxOwnerSessionId is Guid ownerId && sessions.TryGetValue(ownerId, out var txOwner))
                    SendTo(session, ControlMsgType.TxMonitorStarted,
                        new TxMonitorStartedMessage(txOwner.Id, txOwner.Callsign, txOwner.LastFrequencyMHz, txOwner.LastModulation));

                return session;
            }
        }

        /// <summary>Asocia el canal de audio de MultiListen a una sesion.
        /// Si todavia no existe (el cliente abrio primero el audio que el
        /// control), se crea con un indicativo provisional que el Hello
        /// del canal de control completara enseguida.</summary>
        public SessionInfo AttachAudio(Guid sessionId, Action<byte[], int> sendAudio, OperatorPrincipal? principal = null)
        {
            lock (sync)
            {
                SessionInfo session = BindSession(sessionId, principal, "?", false);
                session.SendAudio = sendAudio;
                BroadcastOperators();
                return session;
            }
        }

        public SessionInfo AttachSerial(Guid sessionId, Action<byte[], int> sendSerial, OperatorPrincipal? principal = null)
        {
            lock (sync)
            {
                SessionInfo session = BindSession(sessionId, principal, "?", false);
                session.SendSerial = sendSerial;
                BroadcastOperators();
                return session;
            }
        }

        public void OnAudioChannelClosed(Guid sessionId, Action<byte[], int> sendAudio)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var s) || !ReferenceEquals(s.SendAudio, sendAudio)) return;
                s.SendAudio = null;
                BroadcastOperators();
                // Sin audio no hay TX posible: si esta sesion tenia el TX, se
                // suelta igual que si hubiera soltado el PTT.
                if (TxOwnerSessionId == sessionId) ReleaseTx(sessionId, "SESSION_LOST");
                RemoveOrphan(sessionId);
            }
        }

        public void OnSerialChannelClosed(Guid sessionId, Action<byte[], int> sendSerial)
        {
            lock (sync)
            {
                if (sessions.TryGetValue(sessionId, out var s) && ReferenceEquals(s.SendSerial, sendSerial))
                { s.SendSerial = null; serialPending.Remove(sessionId); if (TxOwnerSessionId == sessionId) ReleaseTx(sessionId, "SERIAL_LOST"); BroadcastOperators(); RemoveOrphan(sessionId); }
            }
        }

        private void RemoveOrphan(Guid id)
        {
            if (sessions.TryGetValue(id, out var s) && s.SendControl == null && s.SendAudio == null && s.SendSerial == null)
                sessions.TryRemove(id, out _);
        }

        public void OnControlChannelClosed(Guid sessionId, Action<byte[]> sendControl)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var current) ||
                    !ReferenceEquals(current.SendControl, sendControl)) return;

                lastAudioRejectLogUtc.TryRemove(sessionId, out _);
                controlQueue.Remove(sessionId); serialPending.Remove(sessionId);
                current.SendControl = null;
                Vars.Out($"[SharedRadio] sesion desconectada: {current.Callsign} ({sessionId})");

                // A stale socket must not clear the replacement connection's
                // control or TX ownership after a fast reconnect.
                if (TxOwnerSessionId == sessionId) ReleaseTx(sessionId, "SESSION_LOST");
                if (ControlOwnerSessionId == sessionId) ReleaseControlInternal("SESSION_LOST");
                RemoveOrphan(sessionId);
                BroadcastOperators();
            }
        }

        // ------------------------------------------------------------
        // Control Lock
        // ------------------------------------------------------------

        public void TryRequestControl(Guid sessionId)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var session)) return;

                if (session.Principal?.Role == OperatorRole.Observer)
                {
                    SendTo(session, ControlMsgType.ControlDenied, new ControlDeniedMessage("ROLE_OBSERVER")); return;
                }
                if (ControlOwnerSessionId != null)
                {
                    SendTo(session, ControlMsgType.ControlDenied, new ControlDeniedMessage("CONTROL_BUSY"));
                    return;
                }

                ControlOwnerSessionId = sessionId;
                controlQueue.Remove(sessionId);
                Vars.Out($"[SharedRadio] control tomado por {session.Callsign}");
                Broadcast(ControlMsgType.ControlGranted, new ControlGrantedMessage(sessionId, session.Callsign));
                BroadcastOperators();
            }
        }

        public void ReleaseControl(Guid sessionId)
        {
            lock (sync)
            {
                if (ControlOwnerSessionId != sessionId) return;
                ReleaseControlInternal("RELEASED");
            }
        }

        private void ReleaseControlInternal(string reason)
        {
            if (ControlOwnerSessionId == null) return;

            // Sin control no hay TX: si la sesion que suelta el control
            // estaba ademas transmitiendo, el TX se suelta primero.
            if (TxOwnerSessionId == ControlOwnerSessionId)
                ReleaseTx(ControlOwnerSessionId.Value, reason);

            serialPending.Clear();
            ControlOwnerSessionId = null;
            Vars.Out($"[SharedRadio] control liberado (motivo={reason})");
            Broadcast(ControlMsgType.ControlReleased, null);
            BroadcastOperators();
        }

        public bool IsControlOwner(Guid sessionId) => ControlOwnerSessionId == sessionId;
        public bool ExecuteControl(Guid id, Action<byte[]> source, Action action)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(id, out var s) || !ReferenceEquals(s.SendControl, source)) return false;
                action(); return true;
            }
        }

        // ------------------------------------------------------------
        // TX Lock
        // ------------------------------------------------------------

        public void TryRequestTx(Guid sessionId, double frequencyMHz, string modulation)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var session)) return;

                if (session.Principal?.Role == OperatorRole.Observer)
                {
                    SendTo(session, ControlMsgType.TxDenied, new TxDeniedMessage("ROLE_OBSERVER")); return;
                }
                if (txOutputFault)
                {
                    SendTo(session, ControlMsgType.TxDenied, new TxDeniedMessage("TX_OUTPUT_UNAVAILABLE")); return;
                }
                if (ControlOwnerSessionId != sessionId)
                {
                    SendTo(session, ControlMsgType.TxDenied, new TxDeniedMessage("NOT_IN_CONTROL"));
                    return;
                }
                if (TxOwnerSessionId != null && TxOwnerSessionId != sessionId)
                {
                    SendTo(session, ControlMsgType.TxDenied, new TxDeniedMessage("TX_BUSY"));
                    return;
                }

                if (TxOwnerSessionId == sessionId) return; // No reiniciar el límite máximo con peticiones repetidas.
                session.LastFrequencyMHz = frequencyMHz;
                session.LastModulation = modulation;
                TxOwnerSessionId = sessionId;
                monitorAnnounced = false;
                txLeaseExpiresUtc = utcNow() + TxLeaseTimeout;
                txStartedUtc = utcNow();
                Vars.Out($"[SharedRadio] TX concedido a {session.Callsign} ({frequencyMHz:F4} MHz {modulation})");
                Broadcast(ControlMsgType.TxGranted, new TxGrantedMessage(sessionId, session.Callsign));
                BroadcastOperators();
            }
        }

        public void ReleaseTx(Guid sessionId, string reason)
        {
            lock (sync)
            {
                if (TxOwnerSessionId != sessionId) return;
                // Reservar la transición antes del callback: si falla USB puede volver a pedir el cierre.
                // El cerrojo impide conceder otro TX hasta terminar la escritura de PTT OFF.
                TxOwnerSessionId = null;
                try { releasePtt(); } catch (Exception ex) { txOutputFault = true; Vars.Err($"No se pudo liberar PTT: {ex.Message}"); }
                monitorAnnounced = false;
                txLeaseExpiresUtc = DateTime.MaxValue;
                Vars.Out($"[SharedRadio] TX_MONITOR_STOPPED reason={reason}");
                Broadcast(ControlMsgType.TxReleased, new TxReleasedMessage(reason));
                Broadcast(ControlMsgType.TxMonitorStopped, null, s => s.SupportsTxMonitor && s.Id != sessionId);
                BroadcastOperators();
            }
        }

        public void CheckTxLease()
        {
            lock (sync)
            {
                if (TxOwnerSessionId is not Guid owner) return;
                if (utcNow() - txStartedUtc >= TimeSpan.FromSeconds(Vars.MaxTxSeconds)) ReleaseTx(owner, "TX_MAX_DURATION");
                else if (utcNow() > txLeaseExpiresUtc) ReleaseTx(owner, "TX_LEASE_EXPIRED");
            }
        }
        public void ReleaseAnyTx(string reason)
        {
            lock (sync) if (TxOwnerSessionId is Guid owner) ReleaseTx(owner, reason);
        }
        public void Dispose() { txWatchdog.Dispose(); ReleaseAnyTx("SERVER_STOPPED"); }

        // ------------------------------------------------------------
        // Audio TX real: punto de entrada UNICO desde Main.cs para cada
        // bloque de PCM TX que llega por red. Decide si es legitimo,
        // renueva el lease, anuncia TX_MONITOR_STARTED la primera vez y
        // reparte la copia a los oyentes -- todo antes de que Main.cs
        // decida si lo manda al AIOC (que sigue siendo su decision, no la
        // de esta clase: ver el valor de retorno).
        // ------------------------------------------------------------

        /// <returns>true si el bloque viene de quien tiene el TX legitimo
        /// y debe seguir su camino critico hacia el AIOC; false si hay
        /// que descartarlo (no es el TxOwner).</returns>
        public bool ForwardTxAudio(Guid sessionId, byte[] data, int length, Action<byte[], int> sink, Action<byte[], int>? source = null)
        {
            lock (sync)
            {
                if (!OnTxAudio(sessionId, data, length, source)) return false;
                sink(data, length);
                return true;
            }
        }

        public bool OnTxAudio(Guid sessionId, byte[] data, int length, Action<byte[], int>? source = null)
        {
            lock (sync)
            {
                if (source != null && (!sessions.TryGetValue(sessionId, out var audioSession) || !ReferenceEquals(audioSession.SendAudio, source))) return false;
                if (TxOwnerSessionId != sessionId)
                {
                    DateTime now = DateTime.UtcNow;
                    if (!lastAudioRejectLogUtc.TryGetValue(sessionId, out var last) || now - last > AudioRejectLogInterval)
                    {
                        lastAudioRejectLogUtc[sessionId] = now;
                        Vars.Out($"[SharedRadio] audio TX descartado (sesion {sessionId} no tiene el TX) -- repitiendo cada {AudioRejectLogInterval.TotalSeconds:F0}s mientras dure");
                    }
                    return false;
                }
                lastAudioRejectLogUtc.TryRemove(sessionId, out _);

                txLeaseExpiresUtc = utcNow() + TxLeaseTimeout;

                if (!monitorAnnounced)
                {
                    monitorAnnounced = true;
                    if (sessions.TryGetValue(sessionId, out var owner))
                    {
                        Vars.Out($"[SharedRadio] TX_MONITOR_STARTED operator={owner.Callsign} session={sessionId} freq={owner.LastFrequencyMHz:F4}");
                        Broadcast(ControlMsgType.TxMonitorStarted,
                            new TxMonitorStartedMessage(owner.Id, owner.Callsign, owner.LastFrequencyMHz, owner.LastModulation),
                            s => s.SupportsTxMonitor && s.Id != sessionId);
                    }
                }

                FanOutTxMonitor(sessionId, data, length);
                return true;
            }
        }

        private void FanOutTxMonitor(Guid txOwnerSessionId, byte[] data, int length)
        {
            foreach (var s in sessions.Values)
            {
                if (s.Id == txOwnerSessionId) continue;   // nunca al propio operador (eco/feedback)
                if (!s.SupportsTxMonitor) continue;
                if (s.SendAudio == null) continue;
                try { s.SendAudio(data, length); }
                catch { Vars.Out($"[SharedRadio] TX_MONITOR_DROPPED_FRAMES client={s.Callsign}"); }
            }
        }

        // ------------------------------------------------------------
        // RX real de la radio y telemetria/LCD por serie: fan-out a
        // TODAS las sesiones conectadas (nadie queda excluido aqui, a
        // diferencia del TX Monitor).
        // ------------------------------------------------------------

        public void FanOutRx(byte[] data, int length)
        {
            lock (sync)
            {
                foreach (var s in sessions.Values)
                {
                    if (TxOwnerSessionId != null && (s.Id == TxOwnerSessionId || s.SupportsTxMonitor)) continue;
                    if (s.SendAudio == null) continue;
                    try { s.SendAudio(data, length); } catch { }
                }
            }
        }

        public void FanOutSerialRx(byte[] data, int length)
        {
            foreach (var s in sessions.Values)
            {
                if (s.SendSerial == null) continue;
                try { s.SendSerial(data, length); }
                catch { }
            }
        }

        // Solo se envían extensiones a clientes que anuncian protocolo 2.
        public (int Operators, bool TxActive) ReadLocalState()
        {
            lock (sync) return (sessions.Values.Count(s => s.SendControl != null), TxOwnerSessionId != null);
        }
        public OperatorsStateMessage GetOperators()
        {
            lock (sync) return new(sessions.Values.Where(s => s.SendControl != null).Select(s => new OperatorInfo(s.Id, s.Callsign,
                s.SendAudio != null, s.SendSerial != null, ControlOwnerSessionId == s.Id, TxOwnerSessionId == s.Id,
                controlQueue.IndexOf(s.Id) + 1, s.Principal?.Role.ToString() ?? "Operator")).OrderBy(s => s.Callsign).ToArray());
        }
        private void BroadcastOperators() => Broadcast(ControlMsgType.OperatorsState, GetOperators(), s => s.SupportsExtensions);
        public void SendOperators(Guid id) { lock (sync) if (sessions.TryGetValue(id, out var s) && s.SupportsExtensions) SendTo(s, ControlMsgType.OperatorsState, GetOperators()); }
        public void QueueControl(Guid id, bool join)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(id, out var s) || s.SendControl == null) return;
                controlQueue.Remove(id);
                if (join && ControlOwnerSessionId != id && s.Principal?.Role != OperatorRole.Observer) controlQueue.Add(id);
                BroadcastOperators();
            }
        }
        public void SendHealth(Guid id)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(id, out var s) || !s.SupportsExtensions) return;
                var h = HardwareStatus();
                SendTo(s, ControlMsgType.HealthState, new HealthStateMessage(Vars.Version, 2, h.Serial, h.Capture, h.Playback,
                    sessions.Values.Count(x => x.SendControl != null), Vars.TxLeaseSeconds, Vars.MaxTxSeconds, AdvertisedCapabilities));
            }
        }
        // Los límites de TCP no son límites de paquetes CAT. Reensamblar antes de autorizar PTT.
        public void ForwardSerial(Guid id, byte[] data, int length, Action<byte[], int, int> send, Action<byte[], int>? source = null)
        {
            lock (sync)
            {
                if (ControlOwnerSessionId != id) return;
                if (source != null && (!sessions.TryGetValue(id, out var serialSession) || !ReferenceEquals(serialSession.SendSerial, source))) return;
                if (!serialPending.TryGetValue(id, out var pending)) serialPending[id] = pending = new();
                pending.AddRange(data.Take(length));
                while (pending.Count >= 4)
                {
                    if (pending[0] != 0xab || pending[1] != 0xcd) { pending.RemoveAt(0); continue; }
                    int size = pending[2] | pending[3] << 8;
                    if (size > 4096 || size < 4) { pending.Clear(); return; }
                    size += 8;
                    if (pending.Count < size) return;
                    byte[] frame = pending.Take(size).ToArray(); pending.RemoveRange(0, size);
                    if (frame[^2] != 0xdc || frame[^1] != 0xba) continue;
                    if (QDNH.Serial.RadioProtocol.IsPttOn(frame) && TxOwnerSessionId != id) continue;
                    send(frame, 0, frame.Length);
                }
            }
        }

        // ------------------------------------------------------------
        // Envio de mensajes de control
        // ------------------------------------------------------------

        private static void SendTo(SessionInfo session, ControlMsgType type, object? payload)
        {
            if (session.SendControl == null) return;
            try { session.SendControl(Frame(type, payload)); } catch { }
        }

        private void Broadcast(ControlMsgType type, object? payload, Func<SessionInfo, bool>? filter = null)
        {
            byte[] frame = Frame(type, payload);
            foreach (var s in sessions.Values)
            {
                if (filter != null && !filter(s)) continue;
                if (s.SendControl == null) continue;
                try { s.SendControl(frame); } catch { }
            }
        }

        private static byte[] Frame(ControlMsgType type, object? payload)
        {
            byte[] json = payload == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType());
            byte[] frame = new byte[1 + 4 + json.Length];
            frame[0] = (byte)type;
            BitConverter.GetBytes(json.Length).CopyTo(frame, 1);
            json.CopyTo(frame, 5);
            return frame;
        }
    }
}

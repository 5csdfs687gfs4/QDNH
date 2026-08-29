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
    public class SessionManager
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
        private static readonly TimeSpan TxLeaseTimeout = TimeSpan.FromSeconds(10);
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

        public SessionManager()
        {
            txWatchdog = new Timer(_ => CheckTxLease(), null, 1000, 1000);
        }

        // ------------------------------------------------------------
        // Alta / baja de sesiones
        // ------------------------------------------------------------

        /// <summary>Primer mensaje (Hello) del canal de control de una
        /// conexion nueva: crea la sesion si no existia (o la reutiliza
        /// si el audio/serie ya habian llegado antes) y contesta con el
        /// estado actual de control/TX.</summary>
        public SessionInfo RegisterOrAttach(Guid sessionId, string callsign, bool supportsTxMonitor, Action<byte[]> sendControl)
        {
            SessionInfo session = sessions.GetOrAdd(sessionId, _ => new SessionInfo(sessionId, callsign, supportsTxMonitor));
            session.Callsign = callsign;
            session.SupportsTxMonitor = supportsTxMonitor;
            session.SendControl = sendControl;
            Vars.Out($"[SharedRadio] sesion conectada: {callsign} ({sessionId})");

            string? controlCallsign = ControlOwnerSessionId != null && sessions.TryGetValue(ControlOwnerSessionId.Value, out var co) ? co.Callsign : null;
            string? txCallsign = TxOwnerSessionId != null && sessions.TryGetValue(TxOwnerSessionId.Value, out var to) ? to.Callsign : null;
            SendTo(session, ControlMsgType.HelloAck,
                new HelloAckMessage(ControlOwnerSessionId != null, controlCallsign, TxOwnerSessionId != null, txCallsign));

            return session;
        }

        /// <summary>Asocia el canal de audio de MultiListen a una sesion.
        /// Si todavia no existe (el cliente abrio primero el audio que el
        /// control), se crea con un indicativo provisional que el Hello
        /// del canal de control completara enseguida.</summary>
        public SessionInfo AttachAudio(Guid sessionId, Action<byte[], int> sendAudio)
        {
            SessionInfo session = sessions.GetOrAdd(sessionId, _ => new SessionInfo(sessionId, "?", false));
            session.SendAudio = sendAudio;
            return session;
        }

        public SessionInfo AttachSerial(Guid sessionId, Action<byte[], int> sendSerial)
        {
            SessionInfo session = sessions.GetOrAdd(sessionId, _ => new SessionInfo(sessionId, "?", false));
            session.SendSerial = sendSerial;
            return session;
        }

        public void OnAudioChannelClosed(Guid sessionId)
        {
            if (sessions.TryGetValue(sessionId, out var s)) s.SendAudio = null;
            // Sin audio no hay TX posible: si esta sesion tenia el TX, se
            // suelta igual que si hubiera soltado el PTT.
            if (TxOwnerSessionId == sessionId) ReleaseTx(sessionId, "SESSION_LOST");
        }

        public void OnSerialChannelClosed(Guid sessionId)
        {
            if (sessions.TryGetValue(sessionId, out var s)) s.SendSerial = null;
        }

        public void OnControlChannelClosed(Guid sessionId)
        {
            lastAudioRejectLogUtc.TryRemove(sessionId, out _);
            if (sessions.TryRemove(sessionId, out var s))
                Vars.Out($"[SharedRadio] sesion desconectada: {s.Callsign} ({sessionId})");

            // Failsafe: perder el canal de control implica perder tanto
            // el TX como el control, sin excepciones -- nunca se asume
            // que la sesion sigue teniendo cualquiera de los dos solo
            // porque el audio/serie sigan abiertos un instante mas.
            if (TxOwnerSessionId == sessionId) ReleaseTx(sessionId, "SESSION_LOST");
            if (ControlOwnerSessionId == sessionId) ReleaseControlInternal("SESSION_LOST");
        }

        // ------------------------------------------------------------
        // Control Lock
        // ------------------------------------------------------------

        public void TryRequestControl(Guid sessionId)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var session)) return;

                if (ControlOwnerSessionId != null)
                {
                    SendTo(session, ControlMsgType.ControlDenied, new ControlDeniedMessage("CONTROL_BUSY"));
                    return;
                }

                ControlOwnerSessionId = sessionId;
                Vars.Out($"[SharedRadio] control tomado por {session.Callsign}");
                Broadcast(ControlMsgType.ControlGranted, new ControlGrantedMessage(sessionId, session.Callsign));
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

            ControlOwnerSessionId = null;
            Vars.Out($"[SharedRadio] control liberado (motivo={reason})");
            Broadcast(ControlMsgType.ControlReleased, null);
        }

        public bool IsControlOwner(Guid sessionId) => ControlOwnerSessionId == sessionId;

        // ------------------------------------------------------------
        // TX Lock
        // ------------------------------------------------------------

        public void TryRequestTx(Guid sessionId, double frequencyMHz, string modulation)
        {
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var session)) return;

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

                session.LastFrequencyMHz = frequencyMHz;
                session.LastModulation = modulation;
                TxOwnerSessionId = sessionId;
                monitorAnnounced = false;
                txLeaseExpiresUtc = DateTime.UtcNow + TxLeaseTimeout;
                Vars.Out($"[SharedRadio] TX concedido a {session.Callsign} ({frequencyMHz:F4} MHz {modulation})");
                Broadcast(ControlMsgType.TxGranted, new TxGrantedMessage(sessionId, session.Callsign));
            }
        }

        public void ReleaseTx(Guid sessionId, string reason)
        {
            lock (sync)
            {
                if (TxOwnerSessionId != sessionId) return;
                TxOwnerSessionId = null;
                monitorAnnounced = false;
                txLeaseExpiresUtc = DateTime.MaxValue;
                Vars.Out($"[SharedRadio] TX_MONITOR_STOPPED reason={reason}");
                Broadcast(ControlMsgType.TxReleased, new TxReleasedMessage(reason));
                Broadcast(ControlMsgType.TxMonitorStopped, null, s => s.SupportsTxMonitor && s.Id != sessionId);
            }
        }

        private void CheckTxLease()
        {
            if (TxOwnerSessionId != null && DateTime.UtcNow > txLeaseExpiresUtc)
            {
                Guid owner = TxOwnerSessionId.Value;
                Vars.Out($"[SharedRadio] TX lease expirado, cerrando TX de la sesion {owner}");
                ReleaseTx(owner, "TX_LEASE_EXPIRED");
            }
        }

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
        public bool OnTxAudio(Guid sessionId, byte[] data, int length)
        {
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

            txLeaseExpiresUtc = DateTime.UtcNow + TxLeaseTimeout;

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
            foreach (var s in sessions.Values)
            {
                if (s.SendAudio == null) continue;
                try { s.SendAudio(data, length); }
                catch { }
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

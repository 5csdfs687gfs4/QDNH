using System;

namespace QDNH.SharedRadio
{
    /// <summary>
    /// Tipos de mensaje del canal de control de Shared Radio Mode (ver
    /// Network/ControlChannel.cs). Framing en el cable: 1 byte de tipo +
    /// int32 little-endian con la longitud del payload + payload JSON
    /// UTF8 (longitud 0 si el mensaje no lleva datos).
    ///
    /// Este canal es un tercer puerto (NetworkPort+2), separado de audio
    /// (NetworkPort) y serie (NetworkPort+1) a proposito: esos dos siguen
    /// siendo tuberias de bytes crudos sin tocar, y este solo lleva
    /// mensajes cortos y poco frecuentes (peticiones de control/TX,
    /// cambios de estado). Nada de PCM viaja por aqui.
    /// </summary>
    public enum ControlMsgType : byte
    {
        // Cliente -> servidor
        Hello = 1,
        ControlRequest = 3,
        ControlRelease = 13,
        RequestTx = 7,
        TxRelease = 14,

        // Servidor -> cliente(s)
        HelloAck = 2,
        ControlGranted = 4,
        ControlDenied = 5,
        ControlReleased = 6,
        TxGranted = 8,
        TxDenied = 9,
        TxReleased = 10,
        TxMonitorStarted = 11,
        TxMonitorStopped = 12,
    }

    public record HelloMessage(Guid SessionId, string Callsign, bool SupportsTxMonitor);

    public record HelloAckMessage(bool ControlHeld, string? ControlCallsign, bool TxHeld, string? TxCallsign);

    public record ControlGrantedMessage(Guid SessionId, string Callsign);

    public record ControlDeniedMessage(string Reason);

    // FrequencyMHz/Modulation: QDNH no interpreta el protocolo CAT (ver
    // Serial/UART.cs, un relay ciego), asi que no tiene forma de saber la
    // frecuencia/modo actuales por si mismo. Es el propio QDockX del
    // ControlOwner quien los conoce (su VFO en pantalla) y los manda
    // aqui al pedir el TX; QDNH solo los reenvia tal cual en
    // TxMonitorStarted para que los demas clientes los muestren.
    public record RequestTxMessage(double FrequencyMHz, string Modulation);

    public record TxGrantedMessage(Guid SessionId, string Callsign);

    public record TxDeniedMessage(string Reason);

    public record TxReleasedMessage(string Reason);

    public record TxMonitorStartedMessage(Guid SessionId, string Callsign, double FrequencyMHz, string Modulation);
}

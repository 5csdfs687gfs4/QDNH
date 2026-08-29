using System;

namespace QDNH.SharedRadio
{
    /// <summary>
    /// Una sesion conectada en Shared Radio Mode: un QDockX con sus tres
    /// canales correlacionados por el mismo SessionId (audio y serie via
    /// Network/MultiListen.cs, control via Network/ControlChannel.cs).
    /// SessionManager.cs es el unico que crea/destruye instancias de esta
    /// clase; nadie mas debe construirlas.
    /// </summary>
    public class SessionInfo
    {
        public Guid Id { get; }
        public string Callsign { get; set; }
        public DateTime ConnectedUtc { get; } = DateTime.UtcNow;
        public bool SupportsTxMonitor { get; set; }

        // Ultima frecuencia/modulacion que esta sesion declaro al pedir
        // el TX (ver RequestTxMessage en ControlMessage.cs) -- QDNH no
        // las conoce por si mismo, solo las guarda para poder incluirlas
        // en TxMonitorStarted cuando empiece a repartir su audio.
        public double LastFrequencyMHz { get; set; }
        public string LastModulation { get; set; } = "FM";

        // Delegados de envio de cada canal, rellenados por
        // Network/ControlChannel.cs y Network/MultiListen.cs cuando cada
        // conexion de esta sesion queda establecida. Pueden ser null si
        // ese canal en concreto todavia no se ha conectado (p.ej. el
        // cliente abre primero el canal de control y un instante despues
        // el de audio) o ya se ha cerrado.
        public Action<byte[]>? SendControl { get; set; }
        public Action<byte[], int>? SendAudio { get; set; }
        public Action<byte[], int>? SendSerial { get; set; }

        public SessionInfo(Guid id, string callsign, bool supportsTxMonitor)
        {
            Id = id;
            Callsign = callsign;
            SupportsTxMonitor = supportsTxMonitor;
        }
    }
}

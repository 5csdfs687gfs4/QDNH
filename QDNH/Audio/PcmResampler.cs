using System;

namespace QDNH.Audio
{
    /// <summary>
    /// Remuestreador lineal por flujo para PCM mono de 16 bits. MultiRX
    /// lo usa para llevar el audio de OpenWebRX (12000 Hz) a la tasa que
    /// espera QDockX en todos los canales de audio (22050 Hz, ver
    /// Settings/Vars.WaveFormat). La interpolacion lineal es suficiente
    /// para voz en banda estrecha y no añade latencia apreciable.
    /// </summary>
    public sealed class PcmResampler
    {
        private readonly double step; // muestras de entrada por muestra de salida
        private double position;      // posicion fraccionaria respecto a la muestra anterior
        private short previous;
        private bool primed;

        public PcmResampler(int sourceRate, int targetRate)
        {
            if (sourceRate <= 0 || targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
            step = sourceRate / (double)targetRate;
        }

        public void Reset() { position = 0; previous = 0; primed = false; }

        /// <summary>Capacidad de salida suficiente para una entrada de ese tamaño.</summary>
        public int MaxOutput(int inputSamples) => (int)Math.Ceiling(inputSamples / step) + 2;

        public int Process(ReadOnlySpan<short> input, short[] output)
        {
            int count = 0;
            foreach (short current in input)
            {
                if (!primed) { previous = current; primed = true; position = 0; continue; }
                // 'position' recorre el tramo [previous, current] en [0,1).
                while (position < 1.0)
                {
                    output[count++] = (short)Math.Round(previous + (current - previous) * position);
                    position += step;
                }
                position -= 1.0;
                previous = current;
            }
            return count;
        }
    }
}

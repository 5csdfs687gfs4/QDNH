using System;

namespace QDNH.Audio
{
    /// <summary>
    /// Decodificador IMA-ADPCM con la palabra de sincronismo que usa
    /// OpenWebRX en sus frames de audio (MultiRX). Cada bloque empieza con
    /// los cuatro bytes ASCII "SYNC" seguidos de stepIndex y predictor
    /// (int16 little-endian cada uno), que REINICIAN el estado en vez de
    /// arrastrar el del bloque anterior; despues vienen 1001 bytes de
    /// nibbles (primero el nibble bajo) y vuelta a buscar "SYNC".
    ///
    /// Reconstruido a partir de tools/rfcapture/openwebrx_capture.py del
    /// repositorio de QDockX, que era un porte literal del decodificador
    /// original del servidor (validado con audio RF real).
    /// </summary>
    public sealed class ImaAdpcmSyncDecoder
    {
        private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        private static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
            34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
            157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
            724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
            3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
        };
        private static readonly byte[] SyncWord = { (byte)'S', (byte)'Y', (byte)'N', (byte)'C' };
        /// <summary>Bytes de nibbles que siguen a cada cabecera SYNC.</summary>
        public const int BlockBytes = 1001;

        private int predictor, stepIndex, step = 7;
        private int phase; // 0 = buscando SYNC, 1 = leyendo stepIndex/predictor, 2 = nibbles
        private int synchronized, headerIndex, remaining;
        private readonly byte[] header = new byte[4];

        public void Reset()
        {
            predictor = 0; stepIndex = 0; step = 7;
            phase = 0; synchronized = 0; headerIndex = 0; remaining = 0;
        }

        private short DecodeNibble(int nibble)
        {
            stepIndex = Math.Clamp(stepIndex + IndexTable[nibble], 0, 88);
            int diff = step >> 3;
            if ((nibble & 1) != 0) diff += step >> 2;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 4) != 0) diff += step;
            if ((nibble & 8) != 0) diff = -diff;
            predictor = Math.Clamp(predictor + diff, short.MinValue, short.MaxValue);
            step = StepTable[stepIndex];
            return (short)predictor;
        }

        /// <summary>Decodifica un trozo del flujo. Escribe en
        /// <paramref name="output"/> (capacidad minima: 2 muestras por byte
        /// de entrada) y devuelve cuantas muestras ha producido.</summary>
        public int Decode(ReadOnlySpan<byte> data, short[] output)
        {
            int count = 0;
            foreach (byte b in data)
            {
                switch (phase)
                {
                    case 0:
                        if (b == SyncWord[synchronized]) synchronized++;
                        else synchronized = b == SyncWord[0] ? 1 : 0;
                        if (synchronized == 4) { headerIndex = 0; phase = 1; }
                        break;
                    case 1:
                        header[headerIndex++] = b;
                        if (headerIndex == 4)
                        {
                            stepIndex = Math.Clamp(BitConverter.ToInt16(header, 0), (short)0, (short)88);
                            predictor = BitConverter.ToInt16(header, 2);
                            step = StepTable[stepIndex];
                            remaining = BlockBytes;
                            phase = 2;
                        }
                        break;
                    default:
                        output[count++] = DecodeNibble(b & 0x0F);
                        output[count++] = DecodeNibble((b >> 4) & 0x0F);
                        if (--remaining == 0) { synchronized = 0; phase = 0; }
                        break;
                }
            }
            return count;
        }
    }
}

namespace QDNH.Audio;

/// <summary>Una instancia por conexión PCM16. Los límites de lectura TCP nunca cortan muestras en las colas.</summary>
public sealed class Pcm16BlockAssembler
{
    private byte? pending;
    public byte[] Push(byte[] data, int length)
    {
        if (length < 0 || length > data.Length) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return Array.Empty<byte>();
        int prefix = pending.HasValue ? 1 : 0;
        int complete = (length + prefix) & ~1;
        byte[] result = new byte[complete];
        if (complete > 0)
        {
            if (pending.HasValue) result[0] = pending.Value;
            Array.Copy(data, 0, result, prefix, complete - prefix);
        }
        pending = ((length + prefix) & 1) != 0 ? data[length - 1] : null;
        return result;
    }
    public void Reset() => pending = null;
}

namespace QDNH.Serial;
// Framing, XOR y CRC de QDockX Radio/Serial.SendCommand. KeyPress=0x0801; ReleaseKey=19.
public static class RadioProtocol
{
    private static readonly byte[] xor = { 0x16, 0x6c, 0x14, 0xe6, 0x2e, 0x91, 0x0d, 0x40, 0x21, 0x35, 0xd5, 0x40, 0x13, 0x03, 0xe9, 0x80 };
    public static byte[] ReleasePtt() => Key(19);
    public static byte[] Key(ushort key)
    {
        byte[] data = { 0xab, 0xcd, 6, 0, 1, 8, 2, 0, (byte)key, (byte)(key >> 8), 0, 0, 0xdc, 0xba };
        int crc = 0;
        for (int i = 4; i < 10; i++)
        {
            crc ^= data[i] << 8;
            for (int bit = 0; bit < 8; bit++) crc = ((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0)) & 0xffff;
            data[i] ^= xor[i - 4];
        }
        data[10] = (byte)((byte)crc ^ xor[6]);
        data[11] = (byte)((crc >> 8) ^ xor[7]);
        return data;
    }
    public static bool IsPttOn(byte[] frame) => frame.Length >= 14 &&
        (frame[4] ^ xor[0]) == 1 && (frame[5] ^ xor[1]) == 8 &&
        // Firmware CMD_0801 aplica Key & 0x1f; bit 5 es click y el byte alto es padding.
        ((frame[8] ^ xor[4]) & 0x1f) == 16;
}

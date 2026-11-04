using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VenueAudio.Core;

/// <summary>Room audio encryption independent of the transport. The key stays in the guest invite.</summary>
public sealed class AudioCipher : IDisposable
{
    private static readonly byte[] Context = "VenueAudio/Opus/v1"u8.ToArray();
    private readonly AesGcm cipher;
    private readonly byte[] prefix = RandomNumberGenerator.GetBytes(4);
    private ulong sequence, received;
    private readonly object gate = new();
    public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public AudioCipher(string key)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new FormatException("Invalid room audio key.");
        var bytes = Convert.FromHexString(key);
        try { cipher = new AesGcm(bytes, 16); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public byte[] Encrypt(byte[] audio)
    {
        lock (gate)
        {
            var packet = new byte[29 + audio.Length]; packet[0] = 1;
            prefix.CopyTo(packet, 1);
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(5, 8), checked(++sequence));
            cipher.Encrypt(packet.AsSpan(1, 12), audio, packet.AsSpan(29), packet.AsSpan(13, 16), Context);
            return packet;
        }
    }
    public byte[] Decrypt(byte[] packet)
    {
        lock (gate)
        {
            if (packet.Length < 30 || packet[0] != 1) throw new CryptographicException("Invalid encrypted audio.");
            ulong next = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(5, 8));
            if (next <= received) throw new CryptographicException("Repeated or out-of-order encrypted audio.");
            var audio = new byte[packet.Length - 29];
            cipher.Decrypt(packet.AsSpan(1, 12), packet.AsSpan(29), packet.AsSpan(13, 16), audio, Context);
            received = next; return audio;
        }
    }
    public void Dispose() { lock (gate) cipher.Dispose(); }
}

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VenueAudio.Core;

public sealed record Hello(string Role, string Room, string Key = "", int Version = 1);
public sealed record Notice(string Type, string Message = "", int Listeners = 0);

public static class Wire
{
    public const int Port = 45831;
    public const int MaxPacket = 8192;
    public static byte[] Json<T>(T value) => [1, .. JsonSerializer.SerializeToUtf8Bytes(value)];
    public static T Parse<T>(byte[] data) => data.Length > 1 && data[0] == 1
        ? JsonSerializer.Deserialize<T>(data.AsSpan(1)) ?? throw new IOException("Empty message")
        : throw new IOException("Expected control message");
    public static async Task WriteAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        if (data.Length is < 1 or > MaxPacket) throw new IOException("Invalid packet size");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(data, ct);
    }
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaxPacket) throw new IOException("Invalid packet size");
        var result = new byte[length];
        await stream.ReadExactlyAsync(result, ct);
        return result;
    }
    public static bool ValidSecret(string? value) => value != null && Regex.IsMatch(value, "\\A[a-f0-9]{32}\\z");
}

public sealed record Invite(string Host, int Port, string Room, bool Tls = false, bool WebSocket = false, string AudioKey = "")
{
    public override string ToString() => $"{(WebSocket ? (Tls ? "venueaudiowss" : "venueaudiows") : (Tls ? "venueaudios" : "venueaudio"))}://{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}/{Room}{(string.IsNullOrEmpty(AudioKey) ? "" : "#k=" + AudioKey)}";
    public Uri WebSocketEndpoint => new UriBuilder(Tls ? "wss" : "ws", Host, Port, "/audio").Uri;
    public static Invite Hosted(string server, string room)
    {
        string input = server.Trim();
        if (!input.Contains("://")) input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "wss") ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new FormatException("Enter the hosted relay's https:// address, without a room path.");
        return new Invite(uri.Host, uri.Port, room, true, true);
    }
    public static Invite Parse(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("venueaudio" or "venueaudios" or "venueaudiows" or "venueaudiowss") || string.IsNullOrEmpty(uri.Host) ||
            uri.Port is < 1 or > 65535 || !Wire.ValidSecret(uri.AbsolutePath.Trim('/')) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new FormatException("Paste a complete Venue Audio invite copied by the DJ.");
        string audioKey = "";
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            if (!uri.Fragment.StartsWith("#k=", StringComparison.Ordinal) || uri.Fragment.Length != 67 || !uri.Fragment[3..].All(Uri.IsHexDigit))
                throw new FormatException("The invite's audio key is incomplete. Copy the full guest invite again.");
            audioKey = uri.Fragment[3..];
        }
        return new Invite(uri.Host.Trim('[', ']'), uri.Port, uri.AbsolutePath.Trim('/'),
            uri.Scheme is "venueaudios" or "venueaudiowss", uri.Scheme is "venueaudiows" or "venueaudiowss", audioKey);
    }
}

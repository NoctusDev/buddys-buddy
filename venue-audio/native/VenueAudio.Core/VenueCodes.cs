using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace VenueAudio.Core;

/// <summary>
/// Six-character stand-ins for guest invites. The directory stores only the code's hash and the invite
/// sealed under a key stretched from the code, so its rows are opaque without guessing each code.
/// </summary>
public static class VenueCodes
{
    // No 0/O, 1/I/L: codes are read aloud and retyped from screenshots.
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int Length = 6;
    private static readonly byte[] KeySalt = "VenueAudio/code/key/v1"u8.ToArray();

    public static string NewCode() => RandomNumberGenerator.GetString(Alphabet, Length);
    public static string Display(string code) => code.Length == Length ? code[..3] + "-" + code[3..] : code;

    /// <summary>The code typed by a guest, or null when the text is something else (such as a full invite).</summary>
    public static string? Normalize(string text)
    {
        string compact = new(text.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
        return compact.Length == Length && compact.All(Alphabet.Contains) ? compact : null;
    }

    public static string LookupId(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("VenueAudio/code/id/v1:" + code)));

    public static string Seal(string invite, string code)
    {
        var key = Key(code);
        try
        {
            using var aes = new AesGcm(key, 16);
            byte[] plain = Encoding.UTF8.GetBytes(invite);
            var sealedBytes = new byte[12 + 16 + plain.Length];
            RandomNumberGenerator.Fill(sealedBytes.AsSpan(0, 12));
            aes.Encrypt(sealedBytes.AsSpan(0, 12), plain, sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16));
            return Convert.ToBase64String(sealedBytes);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static string Open(string blob, string code)
    {
        byte[] sealedBytes = Convert.FromBase64String(blob);
        if (sealedBytes.Length < 29) throw new CryptographicException("Damaged venue code record.");
        var key = Key(code);
        try
        {
            using var aes = new AesGcm(key, 16);
            var plain = new byte[sealedBytes.Length - 28];
            aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] Key(string code) => Rfc2898DeriveBytes.Pbkdf2(code, KeySalt, 200_000, HashAlgorithmName.SHA256, 32);
}

/// <summary>Client for the code directory that lives beside the Buddy's Jobs board.</summary>
public sealed class VenueDirectory(string server, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient http = new(handler ?? new HttpClientHandler()) { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
    public string OwnerToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private sealed record Publish(string blob, string? location, string? code);
    private sealed record Resolved(string blob);
    private sealed record Located(string[] codes);

    /// <summary>Publishes the invite under a fresh code, retrying on the rare collision. Returns the code.</summary>
    public async Task<string> PublishAsync(string invite, string? location, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string code = VenueCodes.NewCode();
            if (await PutAsync(code, invite, location, ct)) return code;
        }
        throw new IOException("Could not reserve a venue code. Share the full invite instead.");
    }

    /// <summary>Keeps a published code alive; false means another host now owns it.</summary>
    public Task<bool> RefreshAsync(string code, string invite, string? location, CancellationToken ct) => PutAsync(code, invite, location, ct);

    private async Task<bool> PutAsync(string code, string invite, string? location, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "v1/venue/codes/" + VenueCodes.LookupId(code))
        {
            Content = JsonContent.Create(new Publish(VenueCodes.Seal(invite, code), location, location == null ? null : code)),
        };
        request.Headers.Authorization = new("Bearer", OwnerToken);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("the venue directory isn't switched on at this server yet");
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<string> ResolveAsync(string code, CancellationToken ct)
    {
        using var response = await http.GetAsync("v1/venue/codes/" + VenueCodes.LookupId(code), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("No venue is using that code right now. Check it with the host.");
        response.EnsureSuccessStatusCode();
        var found = await response.Content.ReadFromJsonAsync<Resolved>(ct) ?? throw new IOException("Empty directory reply.");
        return VenueCodes.Open(found.blob, code);
    }

    public async Task<string[]> AtAsync(string location, CancellationToken ct)
    {
        var found = await http.GetFromJsonAsync<Located>("v1/venue/at/" + location, ct);
        return found?.codes.Where(c => VenueCodes.Normalize(c) == c).ToArray() ?? [];
    }

    public async Task DeleteAsync(string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "v1/venue/codes/" + VenueCodes.LookupId(code));
        request.Headers.Authorization = new("Bearer", OwnerToken);
        using var response = await http.SendAsync(request, ct);
    }

    public void Dispose() => http.Dispose();
}

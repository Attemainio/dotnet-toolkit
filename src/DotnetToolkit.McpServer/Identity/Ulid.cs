using System.Security.Cryptography;

namespace DotnetToolkit.McpServer.Identity;

/// <summary>
/// Minimal ULID generator: 48-bit millisecond timestamp + 80 bits of randomness,
/// Crockford base32, 26 chars. Chosen over UUIDv4 so identifiers sort chronologically
/// in SQLite (spec §6). Monotonic within a millisecond is not required by the spec, so
/// each call draws fresh randomness.
/// </summary>
public static class Ulid
{
    // Crockford base32 alphabet (excludes I, L, O, U).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Returns a new 26-character ULID for the current UTC time.</summary>
    public static string NewString()
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        Span<char> chars = stackalloc char[26];

        // 48-bit timestamp → first 10 chars (10 * 5 = 50 bits, top 2 padded to 0).
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(timestamp & 0x1F)];
            timestamp >>= 5;
        }

        // 80-bit randomness → remaining 16 chars, encoded 5 bits at a time.
        ulong hi = ((ulong)random[0] << 32) | ((ulong)random[1] << 24) | ((ulong)random[2] << 16)
                   | ((ulong)random[3] << 8) | random[4];
        ulong lo = ((ulong)random[5] << 32) | ((ulong)random[6] << 24) | ((ulong)random[7] << 16)
                   | ((ulong)random[8] << 8) | random[9];
        for (var i = 7; i >= 0; i--)
        {
            chars[10 + i] = Alphabet[(int)(hi & 0x1F)];
            hi >>= 5;
        }
        for (var i = 7; i >= 0; i--)
        {
            chars[18 + i] = Alphabet[(int)(lo & 0x1F)];
            lo >>= 5;
        }

        return new string(chars);
    }
}

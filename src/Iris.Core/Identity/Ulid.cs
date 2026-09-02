using System.Security.Cryptography;

namespace Iris.Core.Identity;

/// <summary>
/// A dependency-free <see href="https://github.com/ulid/spec">ULID</see> (Universally Unique Lexicographically
/// sortable Identifier) generator: a 128-bit value — a 48-bit millisecond timestamp and 80 bits of
/// entropy — encoded as 26 characters of Crockford base32 (uppercase).
/// </summary>
/// <remarks>
/// ULIDs are unguessable (80 bits of entropy) and lexicographically sortable by creation time, which makes
/// them suitable as the server-minted identity for ActivityPub objects (decision 055): an object id the
/// authoring client cannot predict, collide with, or choose. Unlike a random GUID, a ULID is stable under
/// time ordering and has no version/variant nibbles to leak.
/// </remarks>
/// <remarks>
/// Monotonicity: within a single millisecond, a <see cref="MonotonicUlid"/> instance emits strictly
/// increasing values (the trailing entropy is incremented from the previous value in that millisecond,
/// with a fresh random draw each new millisecond). This guarantees two ids minted in the same tick never
/// collide even under high throughput.
/// </remarks>
public static class Ulid
{
    /// <summary>
    /// The Crockford base32 alphabet (no I, L, O, U).
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// The Unix epoch in ticks for the 48-bit timestamp component.
    /// </summary>
    private static readonly DateTimeOffset UnixEpoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Generates a new random ULID (a fresh 80-bit entropy draw; no monotonicity guarantee across calls).
    /// </summary>
    /// <returns>A 26-character, uppercase Crockford base32 ULID.</returns>
    public static string New()
        => New(DateTimeOffset.UtcNow, RandomNumberGenerator.GetBytes(10));

    /// <summary>
    /// Generates a ULID from an explicit timestamp and entropy draw (used by tests for determinism and by
    /// <see cref="MonotonicUlid"/> for per-call construction).
    /// </summary>
    /// <param name="timestamp">The creation instant (its Unix milliseconds fill the 48-bit prefix).</param>
    /// <param name="entropy">Exactly 10 random bytes (the 80-bit suffix).</param>
    /// <exception cref="ArgumentException">When <paramref name="entropy"/> is not exactly 10 bytes.</exception>
    /// <returns>A 26-character, uppercase Crockford base32 ULID.</returns>
    public static string New(DateTimeOffset timestamp, byte[] entropy)
    {
        if (entropy.Length != 10)
        {
            throw new ArgumentException("Entropy must be exactly 10 bytes.", nameof(entropy));
        }

        var ms = (ulong)(timestamp - UnixEpoch).TotalMilliseconds;
        // 48-bit timestamp (high) + 80-bit entropy (low) = 128 bits, packed into 26 base32 digits
        // (26 * 5 = 130 bits; the top 2 bits are always zero because the timestamp fits in 48 bits).
        Span<byte> bytes = stackalloc byte[16];
        // Big-endian into the 16-byte buffer: timestamp in the first 6 bytes, entropy in the last 10.
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        for (var i = 0; i < 10; i++)
        {
            bytes[6 + i] = entropy[i];
        }

        return EncodeBase32(bytes);
    }

    /// <summary>
    /// Encodes a 16-byte big-endian value as a 26-character Crockford base32 ULID (uppercase).
    /// </summary>
    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[26];
        // 16 bytes = 128 bits. 26 base32 digits cover 130 bits, so the top 2 bits are zero and the first
        // digit always encodes a 3-bit value. We read bit-groups of 5 from the top (MSB) down.
        // Total bits to emit: 26 * 5 = 130. The value occupies the low 128 bits, so the first group takes
        // the top 3 bits of byte[0] (bits 127..125) and the next 2 bits are zero padding.
        // Simpler: treat the 16 bytes as a 130-bit space by prepending 2 zero bits and read 5-bit groups.
        var index = 0;
        var bitBuffer = 0;
        var bitCount = 0;
        for (var i = 0; i < 26; i++)
        {
            // Accumulate 5 bits from the big-endian byte stream, MSB-first.
            while (bitCount < 5)
            {
                if (index < bytes.Length)
                {
                    bitBuffer = (bitBuffer << 8) | bytes[index];
                    index++;
                    bitCount += 8;
                }
                else
                {
                    // Past the 128 bits: pad with zero (the 2-bit headroom + the final partial group).
                    bitBuffer <<= 1;
                    bitCount++;
                }
            }

            bitCount -= 5;
            chars[i] = Alphabet[(bitBuffer >> bitCount) & 0x1F];
            bitBuffer &= (1 << bitCount) - 1;
        }

        return new string(chars);
    }
}

/// <summary>
/// A thread-safe, monotonic <see cref="Ulid"/> source: within any single millisecond it emits strictly
/// increasing ULIDs (so two ids minted in the same tick never collide), and across millisecond boundaries
/// it draws fresh entropy. Use one instance per minting site for high-throughput id generation.
/// </summary>
public sealed class MonotonicUlid
{
    private readonly object _gate = new();
    private ulong _lastMs;
    private byte[] _lastEntropy = new byte[10];
    private bool _hasLast;

    /// <summary>
    /// Generates the next monotonically increasing ULID (monotonic within a millisecond).
    /// </summary>
    /// <param name="now">The current instant (injected for testability; defaults to <see cref="DateTimeOffset.UtcNow"/>).</param>
    /// <returns>A 26-character, uppercase Crockford base32 ULID, strictly greater than the previous one returned in the same millisecond.</returns>
    public string Next(DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        var ms = (ulong)(ts - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMilliseconds;

        lock (_gate)
        {
            byte[] entropy;
            if (_hasLast && ms == _lastMs)
            {
                // Same millisecond: increment the previous entropy (big-endian 80-bit counter). Wraparound
                // (all-bits-set) is astronomically unlikely; if it happens, fall back to a fresh random draw.
                entropy = (byte[])_lastEntropy.Clone();
                for (var i = entropy.Length - 1; i >= 0; i--)
                {
                    entropy[i]++;
                    if (entropy[i] != 0)
                    {
                        break;
                    }
                }

                if (entropy.SequenceEqual(_lastEntropy))
                {
                    entropy = RandomNumberGenerator.GetBytes(10);
                }
            }
            else
            {
                entropy = RandomNumberGenerator.GetBytes(10);
            }

            _lastMs = ms;
            _lastEntropy = entropy;
            _hasLast = true;
            return Ulid.New(ts, entropy);
        }
    }
}

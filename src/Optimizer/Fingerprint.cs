using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Mod2Xgm.Optimizer;

/// <summary>
/// A SHA-256 fingerprint: 32 bytes that stand for data of any length, so a dictionary can match long keys without
/// keeping them (docs/adr/0072-pre-mix-candidates.md). Two different inputs sharing one is not a practical concern.
/// </summary>
/// <param name="Low">The first 16 bytes of the hash, little-endian.</param>
/// <param name="High">The last 16 bytes.</param>
internal readonly record struct Fingerprint(UInt128 Low, UInt128 High)
{
    /// <summary>Bytes in a fingerprint.</summary>
    public const int Size = 32;

    /// <summary>The fingerprint of <paramref name="data"/>.</summary>
    public static Fingerprint Of(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[Size];
        SHA256.HashData(data, hash);
        return From(hash);
    }

    /// <summary>The fingerprint whose hash is <paramref name="hash"/> (<see cref="Size"/> bytes).</summary>
    public static Fingerprint From(ReadOnlySpan<byte> hash) =>
        new(BinaryPrimitives.ReadUInt128LittleEndian(hash), BinaryPrimitives.ReadUInt128LittleEndian(hash[16..]));

    /// <summary>The hash as 64 lowercase hex digits, for file names.</summary>
    public string ToHex()
    {
        Span<byte> hash = stackalloc byte[Size];
        WriteTo(hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Writes the hash to <paramref name="destination"/> (<see cref="Size"/> bytes).</summary>
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt128LittleEndian(destination, Low);
        BinaryPrimitives.WriteUInt128LittleEndian(destination[16..], High);
    }
}

/// <summary>Builds a <see cref="Fingerprint"/> from numbers added one at a time, each written little-endian.</summary>
internal sealed class Hasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>An empty hasher.</summary>
    public Hasher()
    {
    }

    /// <summary>A hasher that starts from <paramref name="prefix"/>, so keys that share a beginning needn't hash it again.</summary>
    public Hasher(Fingerprint prefix) => Add(prefix);

    /// <summary>Adds <paramref name="value"/>.</summary>
    public void Add(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    /// <summary>Adds <paramref name="value"/>, exactly (its bits).</summary>
    public void Add(double value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    /// <summary>Adds <paramref name="value"/>, exactly (its bits).</summary>
    public void Add(float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    /// <summary>Adds <paramref name="value"/> as 1 or 0.</summary>
    public void Add(bool value) => Add(value ? 1 : 0);

    /// <summary>Adds whether <paramref name="value"/> is there, then its bits.</summary>
    public void Add(double? value)
    {
        Add(value.HasValue);
        Add(value ?? 0);
    }

    /// <summary>Adds whether <paramref name="value"/> is there, then it.</summary>
    public void Add(int? value)
    {
        Add(value.HasValue);
        Add(value ?? 0);
    }

    /// <summary>Adds <paramref name="data"/>'s length, then its bytes.</summary>
    public void Add(ReadOnlySpan<byte> data)
    {
        Add(data.Length);
        _hash.AppendData(data);
    }

    /// <summary>Adds <paramref name="value"/> as UTF-8, with its length.</summary>
    public void Add(string value) => Add(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>Adds <paramref name="value"/>.</summary>
    public void Add(Fingerprint value)
    {
        Span<byte> bytes = stackalloc byte[Fingerprint.Size];
        value.WriteTo(bytes);
        _hash.AppendData(bytes);
    }

    /// <summary>The fingerprint of everything added so far; more can still be added.</summary>
    public Fingerprint Finish()
    {
        Span<byte> hash = stackalloc byte[Fingerprint.Size];
        _hash.GetCurrentHash(hash);
        return Fingerprint.From(hash);
    }

    /// <inheritdoc/>
    public void Dispose() => _hash.Dispose();
}

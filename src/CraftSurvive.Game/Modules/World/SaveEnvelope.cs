using System.Buffers.Binary;

namespace CraftSurvive.Game.Modules.World;

/// <summary>The world a save belongs to: the generator version and the seed.</summary>
internal readonly record struct SaveIdentity(uint GeneratorVersion, ulong Seed);

/// <summary>How many records a stored form may hold, and how large each record is.</summary>
internal readonly record struct SaveBounds(int MaximumRecords, int RecordBytes)
{
    internal int MaximumBytes => checked(SaveEnvelope.HeaderBytes + (MaximumRecords * RecordBytes));
}

/// <summary>
/// The header and record discipline every saved key shares. A stored form is a 32-byte header -
/// magic, schema, generator version, seed, record count and a fingerprint over the records - then
/// fixed-size little-endian records.
///
/// Decoding refuses loudly: a wrong magic, schema, generator version or seed, a count past the
/// bound, a length that is not exactly the header plus the counted records, and a fingerprint that
/// does not match all throw. That is safe on the load path, where the owner discards the save and
/// the world regenerates; a silent partial read would invent a history the player never had.
/// </summary>
internal static class SaveEnvelope
{
    internal static SaveIdentity IdentityOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes) throw new InvalidOperationException("Save header is incomplete.");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes[GeneratorVersionOffset..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[SeedOffset..]));
    }
    internal const int HeaderBytes = 32;

    private const int SchemaOffset = sizeof(uint);
    private const int GeneratorVersionOffset = SchemaOffset + sizeof(int);
    private const int SeedOffset = GeneratorVersionOffset + sizeof(uint);
    private const int CountOffset = SeedOffset + sizeof(ulong);
    private const int FingerprintOffset = CountOffset + sizeof(int);

    /// <summary>
    /// A stored form with its header written and room for <paramref name="count"/> records. The
    /// caller writes the records after <see cref="HeaderBytes"/> and then calls <see cref="Seal"/>.
    /// </summary>
    internal static byte[] Allocate(SaveKey key, SaveIdentity identity, SaveBounds bounds, int count)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (count < 0 || count > bounds.MaximumRecords)
        {
            throw new InvalidOperationException(
                $"{key.Key} holds at most {bounds.MaximumRecords} records, not {count}.");
        }

        byte[] bytes = new byte[checked(HeaderBytes + (count * bounds.RecordBytes))];
        Span<byte> header = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(header, key.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[SchemaOffset..], key.Schema);
        BinaryPrimitives.WriteUInt32LittleEndian(header[GeneratorVersionOffset..], identity.GeneratorVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(header[SeedOffset..], identity.Seed);
        BinaryPrimitives.WriteInt32LittleEndian(header[CountOffset..], count);
        return bytes;
    }

    /// <summary>Writes the fingerprint over the records the caller wrote.</summary>
    internal static void Seal(byte[] bytes, ulong fingerprint) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(FingerprintOffset), fingerprint);

    /// <summary>The schema a stored form says it was written in, or null when it is too short to say.</summary>
    internal static int? SchemaOf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= SchemaOffset + sizeof(int) ? BinaryPrimitives.ReadInt32LittleEndian(bytes[SchemaOffset..]) : null;

    /// <summary>
    /// Checks everything the header can say about a stored form and returns its record count. The
    /// caller reads the records, then checks them with <see cref="Verify"/>.
    /// </summary>
    internal static int Open(SaveKey key, SaveIdentity identity, SaveBounds bounds, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (bytes.Length > bounds.MaximumBytes)
        {
            throw new InvalidOperationException($"{key.Key} must not exceed {bounds.MaximumBytes} bytes.");
        }

        if (bytes.Length < HeaderBytes)
        {
            throw new InvalidOperationException($"{key.Key} is incomplete.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != key.Magic)
        {
            throw new InvalidOperationException($"{key.Key} has an unrecognised format.");
        }

        int schema = BinaryPrimitives.ReadInt32LittleEndian(bytes[SchemaOffset..]);
        if (schema != key.Schema)
        {
            throw new InvalidOperationException($"{key.Key} uses unsupported schema {schema}.");
        }

        uint generatorVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes[GeneratorVersionOffset..]);
        if (generatorVersion != identity.GeneratorVersion)
        {
            throw new InvalidOperationException(
                $"{key.Key} was written for generator version {generatorVersion}, not {identity.GeneratorVersion}.");
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes[SeedOffset..]) != identity.Seed)
        {
            throw new InvalidOperationException($"{key.Key} belongs to a different world.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[CountOffset..]);
        if (count < 0 || count > bounds.MaximumRecords)
        {
            throw new InvalidOperationException($"{key.Key} declares {count} records.");
        }

        if (bytes.Length != HeaderBytes + (count * bounds.RecordBytes))
        {
            throw new InvalidOperationException($"{key.Key} is {bytes.Length} bytes but declares {count} records.");
        }

        return count;
    }

    /// <summary>Refuses a stored form whose records do not match the fingerprint it was sealed with.</summary>
    internal static void Verify(SaveKey key, ReadOnlySpan<byte> bytes, ulong fingerprint)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes[FingerprintOffset..]) != fingerprint)
        {
            throw new InvalidOperationException($"{key.Key} does not match its own fingerprint.");
        }
    }

    /// <summary>A writer over the records of an allocated stored form.</summary>
    internal static SaveWriter Records(byte[] bytes) => new(bytes.AsSpan(HeaderBytes));

    /// <summary>A reader over the records of an opened stored form.</summary>
    internal static SaveReader Records(ReadOnlySpan<byte> bytes) => new(bytes[HeaderBytes..]);
}

/// <summary>
/// The fingerprint every stored form is sealed with: FNV-1a-style mixing of whole 64-bit values,
/// started from the world's seed so a record set moved between worlds does not verify.
/// </summary>
internal struct SaveFingerprint
{
    private const ulong OffsetBasis = 0xCBF2_9CE4_8422_2325UL;
    private const ulong Prime = 0x0000_0100_0000_01B3UL;

    private SaveFingerprint(ulong value) => Value = value;

    internal ulong Value { get; private set; }

    internal static SaveFingerprint Start(ulong seed) => new(OffsetBasis ^ seed);

    internal void Mix(ulong value) => Value = unchecked((Value ^ value) * Prime);

    internal void Mix(long value) => Mix(unchecked((ulong)value));

    internal void Mix(double value) => Mix(BitConverter.DoubleToUInt64Bits(value));
}

/// <summary>Writes fixed-size little-endian fields one after another, so no record carries offsets.</summary>
internal ref struct SaveWriter(Span<byte> destination)
{
    private Span<byte> remaining = destination;

    internal void Int64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Take(sizeof(long)), value);

    internal void Int32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(sizeof(int)), value);

    internal void UInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(sizeof(ushort)), value);

    internal void Byte(byte value) => Take(sizeof(byte))[0] = value;

    internal void Double(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Take(sizeof(double)), value);

    internal void Single(float value) => BinaryPrimitives.WriteSingleLittleEndian(Take(sizeof(float)), value);

    private Span<byte> Take(int length)
    {
        Span<byte> field = remaining[..length];
        remaining = remaining[length..];
        return field;
    }
}

/// <summary>Reads fields in the order <see cref="SaveWriter"/> wrote them.</summary>
internal ref struct SaveReader(ReadOnlySpan<byte> source)
{
    private ReadOnlySpan<byte> remaining = source;

    internal long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(sizeof(long)));

    internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

    internal ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));

    internal byte Byte() => Take(sizeof(byte))[0];

    internal double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Take(sizeof(double)));

    internal float Single() => BinaryPrimitives.ReadSingleLittleEndian(Take(sizeof(float)));

    private ReadOnlySpan<byte> Take(int length)
    {
        ReadOnlySpan<byte> field = remaining[..length];
        remaining = remaining[length..];
        return field;
    }
}

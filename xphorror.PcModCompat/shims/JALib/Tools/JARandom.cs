namespace JALib.Tools;

public class JARandom : Random
{
    public static readonly JARandom Instance = new();

    public JARandom()
    {
    }

    public JARandom(int seed)
        : base(seed)
    {
    }

    public short NextShort() => (short)Next();

    public ushort NextUShort() => (ushort)Next();

    public int NextInt() => Next();

    public uint NextUInt() => (uint)Next();

    public long NextLong() => (long)Next() << 32 | (uint)Next();

    public ulong NextULong() => (ulong)NextLong();

    public float NextFloat() => (float)NextDouble();

    public float NextAllFloat() => BitConverter.Int32BitsToSingle(Next());

    public double NextAllDouble() => BitConverter.Int64BitsToDouble(NextLong());

    public decimal NextDecimal() => new([Next(), Next(), Next(), Next()]);

    public override void NextBytes(byte[] buffer) => base.NextBytes(buffer);

    public byte[] NextBytes(int count)
    {
        var buffer = new byte[count];
        NextBytes(buffer);
        return buffer;
    }
}

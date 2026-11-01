using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace L2Launcher.Core;

/// <summary>
/// Reads/writes the client's encrypted system/l2.ini (Lineage2Ver413, l2encdec key pair used by patched Interlude
/// clients: RSA blocks of 128 bytes over a zlib stream). Used to point the client at the configured login server.
/// </summary>
public static class L2Ini
{
    public const string RelPath = "system/l2.ini";
    private const string Header = "Lineage2Ver413";
    private static readonly BigInteger N = BigInteger.Parse("0075b4d6de5c016544068a1acf125869f43d2e09fc55b8b1e289556daf9b8757635593446288b3653da1ce91c87bb1a5c18f16323495c55d7d72c0890a83f69bfd1fd9434eb1c02f3e4679edfa43309319070129c267c85604d87bb65bae205de3707af1d2108881abb567c3b3d069ae67c3a4c6a3aa93d26413d4c66094ae2039", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger Dec = 0x1d;
    private static readonly BigInteger Enc = BigInteger.Parse("0030b4c2d798d47086145c75063c8e841e719776e400291d7838d3e6c4405b504c6a07f8fca27f32b86643d2649d1d5f124cdd0bf272f0909dd7352fe10a77b34d831043d9ae541f8263c6fe3d1c14c2f04e43a7253a6dda9a8c1562cbd493c1b631a1957618ad5dfe5ca28553f746e2fc6f2db816c7db223ec91e955081c1de65", System.Globalization.NumberStyles.HexNumber);
    private static readonly Regex AddrRx = new(@"(?m)^ServerAddr=[^\r\n]*", RegexOptions.CultureInvariant);

    private static byte[] Block(byte[] b, BigInteger e)
    {
        var r = BigInteger.ModPow(new BigInteger(b, isUnsigned: true, isBigEndian: true), e, N).ToByteArray(isUnsigned: true, isBigEndian: true);
        var o = new byte[128];
        Buffer.BlockCopy(r, 0, o, 128 - r.Length, r.Length);
        return o;
    }

    public static bool IsEncrypted(byte[] raw) =>
        raw.Length > 28 && Encoding.Unicode.GetString(raw, 0, 28) == Header;

    public static byte[] Decode(byte[] raw)
    {
        if (!IsEncrypted(raw)) return raw;
        using var packed = new MemoryStream();
        for (int i = 28; i + 128 <= raw.Length - 20 + 20 && i + 128 <= raw.Length; i += 128)
        {
            if (raw.Length - i < 128) break;
            var b = Block(raw.AsSpan(i, 128).ToArray(), Dec);
            int sz = b[3];
            if (sz == 0 || sz > 124) break;
            packed.Write(b, 128 - ((sz + 3) & ~3), sz);
        }
        var p = packed.ToArray();
        using var z = new ZLibStream(new MemoryStream(p, 4, p.Length - 4), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    public static byte[] Encode(byte[] plain)
    {
        using var packed = new MemoryStream();
        packed.Write(BitConverter.GetBytes((uint)plain.Length));
        using (var z = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(plain);
        var p = packed.ToArray();
        using var o = new MemoryStream();
        o.Write(Encoding.Unicode.GetBytes(Header));
        for (int i = 0; i < p.Length; i += 124)
        {
            int n = Math.Min(124, p.Length - i);
            var blk = new byte[128];
            blk[3] = (byte)n;
            Buffer.BlockCopy(p, i, blk, 128 - ((n + 3) & ~3), n);
            o.Write(Block(blk, Enc));
        }
        var body = o.ToArray();
        var tail = new byte[20];
        BitConverter.GetBytes(Crc32(body)).CopyTo(tail, 12);
        o.Write(tail);
        return o.ToArray();
    }

    public static string? ReadServerAddr(byte[] plain)
    {
        var m = AddrRx.Match(Encoding.Latin1.GetString(plain));
        return m.Success ? m.Value.Substring("ServerAddr=".Length).Trim() : null;
    }

    /// <summary>Sets ServerAddr in gamePath/system/l2.ini. Returns true when the file was rewritten.</summary>
    public static bool SetServerAddr(string gamePath, string host)
    {
        var path = Path.Combine(gamePath, "system", "l2.ini");
        if (!File.Exists(path)) throw new LauncherException("l2ini_missing", $"{path} not found");
        var raw = File.ReadAllBytes(path);
        var enc = IsEncrypted(raw);
        var text = Encoding.Latin1.GetString(enc ? Decode(raw) : raw);
        if (!AddrRx.IsMatch(text)) throw new LauncherException("l2ini_bad", "ServerAddr not found in l2.ini");
        var next = AddrRx.Replace(text, "ServerAddr=" + host, 1);
        if (next == text) return false;
        var bytes = Encoding.Latin1.GetBytes(next);
        File.WriteAllBytes(path, enc ? Encode(bytes) : bytes);
        return true;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        }
        return ~c;
    }
}
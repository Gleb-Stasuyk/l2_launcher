// Derived from okuzpe/Lineage2Launcher (MIT, Copyright (c) 2025-2026 L2Titan).
using System.Security.Cryptography;

namespace L2Launcher.Core;

public static class FileIntegrity
{
    /// <summary>Lowercase hex SHA-256. Must match tools/build_manifest.py.</summary>
    public static string Sha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool IsSha256Hex(string? s) =>
        s != null && s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
}

// Derived from okuzpe/Lineage2Launcher Services/ManifestSecurity.cs (MIT, Copyright (c) 2025-2026 L2Titan).
using System.Security.Cryptography;

namespace L2Launcher.Core;

/// <summary>
/// Verifies manifest.json against manifest.json.sig (base64 RSA PKCS#1 v1.5 / SHA-256 over the exact bytes,
/// compatible with `openssl dgst -sha256 -sign`). Only the PUBLIC key is embedded (PublicKey.cs, written by
/// tools/keygen.py). The private key must never be on the web server.
/// </summary>
public static class ManifestSecurity
{
    public static bool Verify(byte[] data, string? signatureBase64, string? publicKeyPem = null)
    {
        if (data == null || data.Length == 0 || string.IsNullOrWhiteSpace(signatureBase64)) return false;
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64.Trim()); } catch { return false; }
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem ?? PublicKey.Pem);
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }
}

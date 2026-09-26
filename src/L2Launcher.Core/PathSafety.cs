// Derived from okuzpe/Lineage2Launcher Services/PathSafety.cs (MIT, Copyright (c) 2025-2026 L2Titan).
namespace L2Launcher.Core;

public static class PathSafety
{
    /// <summary>Resolves a manifest-relative path ("system/x.dat") inside gamePath; rejects rooted paths and "..".</summary>
    public static string ResolveSafePath(string gamePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new PathTraversalException("empty path");
        var rel = relativePath.Replace('\\', '/');
        if (rel.StartsWith('/') || rel.Contains(':') || rel.Split('/').Any(s => s == ".." || s == "."))
            throw new PathTraversalException($"Manifest path not allowed: {relativePath}");
        var baseDir = Path.GetFullPath(gamePath);
        if (!baseDir.EndsWith(Path.DirectorySeparatorChar)) baseDir += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            throw new PathTraversalException($"Manifest path escapes install directory: {relativePath}");
        return full;
    }

    public static string Normalize(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    public static bool LooksLikeLineageClient(string path)
    {
        try { return File.Exists(Path.Combine(path, "system", "l2.exe")) || File.Exists(Path.Combine(path, "system", "L2.exe")); }
        catch { return false; }
    }
}

public sealed class PathTraversalException : Exception
{
    public PathTraversalException(string message) : base(message) { }
}

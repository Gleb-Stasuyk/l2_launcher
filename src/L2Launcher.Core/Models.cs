// Portions derived from okuzpe/Lineage2Launcher (MIT, Copyright (c) 2025-2026 L2Titan).
using Newtonsoft.Json;

namespace L2Launcher.Core;

/// <summary>Signed update manifest (manifest.json + manifest.json.sig).</summary>
public sealed class Manifest
{
    [JsonProperty("format")] public int Format { get; set; }
    [JsonProperty("product")] public string Product { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("generatedAt")] public string GeneratedAt { get; set; } = "";
    /// <summary>Optional absolute https base for file downloads. Default: "&lt;manifest folder&gt;/files/".</summary>
    [JsonProperty("baseUrl")] public string? BaseUrl { get; set; }
    [JsonProperty("files")] public List<ManifestFile> Files { get; set; } = new();
    /// <summary>User-editable files the launcher must never overwrite (informational + enforced).</summary>
    [JsonProperty("userConfig")] public List<string> UserConfig { get; set; } = new();
    [JsonProperty("launcher")] public LauncherRelease? Launcher { get; set; }
    /// <summary>Language-dependent files (see LanguageService). Null = single-language client.</summary>
    [JsonProperty("languages")] public LanguageInfo? Languages { get; set; }
    /// <summary>Files from older layouts that the launcher deletes if present (e.g. Korea-slot *-k.dat).</summary>
    [JsonProperty("obsolete")] public List<string> Obsolete { get; set; } = new();
}

/// <summary>
/// Each language set lives in "lang/&lt;code&gt;/&lt;path&gt;" (normal manifest entries); "files" lists the target
/// paths in the client (e.g. "system/itemname-e.dat") that receive a copy of the selected set.
/// </summary>
public sealed class LanguageInfo
{
    [JsonProperty("default")] public string Default { get; set; } = "en";
    [JsonProperty("sets")] public List<string> Sets { get; set; } = new();
    [JsonProperty("files")] public List<string> Files { get; set; } = new();
}

public sealed class ManifestFile
{
    /// <summary>Relative path with forward slashes, e.g. "system/L2.exe".</summary>
    [JsonProperty("path")] public string Path { get; set; } = "";
    [JsonProperty("size")] public long Size { get; set; }
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
}

public sealed class LauncherRelease
{
    [JsonProperty("version")] public string Version { get; set; } = "";
    /// <summary>Path relative to the files base URL, e.g. "launcher/L2Launcher.exe".</summary>
    [JsonProperty("path")] public string Path { get; set; } = "";
    [JsonProperty("size")] public long Size { get; set; }
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>User-facing error; Message is shown as-is (already short). Code lets the UI localize.</summary>
public class LauncherException : Exception
{
    public string Code { get; }
    public LauncherException(string code, string message, Exception? inner = null) : base(message, inner) { Code = code; }
}

public enum GameLanguage { English, Russian }

public enum UpdateStage { FetchingManifest, Verifying, Downloading, Done }

public sealed record UpdateProgress(UpdateStage Stage, int Done, int Total, long Bytes, long BytesTotal, string? CurrentFile, double BytesPerSecond = 0);

public sealed class VerifyResult
{
    public List<ManifestFile> Missing { get; } = new();
    public List<ManifestFile> Changed { get; } = new();
    public int Ok { get; set; }
    public IEnumerable<ManifestFile> ToDownload => Missing.Concat(Changed);
    public long BytesToDownload => ToDownload.Sum(f => f.Size);
}

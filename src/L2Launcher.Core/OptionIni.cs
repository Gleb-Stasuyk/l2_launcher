using System.Text;

namespace L2Launcher.Core;

/// <summary>
/// Forces [Game] IsNative=False in system\Option.ini (English slot) before launch. Only that value is changed;
/// every other byte of the file is preserved (single-byte files are processed as Latin-1, UTF-16LE with BOM as UTF-16).
/// </summary>
public static class OptionIni
{
    public const string RelPath = "system/Option.ini";

    /// <summary>Returns true if the file was changed (or created).</summary>
    public static bool EnsureIsNativeFalse(string gamePath)
    {
        var path = PathSafety.ResolveSafePath(gamePath, RelPath);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "[Game]\r\nIsNative=False\r\n", Encoding.ASCII);
            return true;
        }
        var bytes = File.ReadAllBytes(path);
        bool utf16 = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        Encoding enc = utf16 ? new UnicodeEncoding(false, true) : Encoding.Latin1;
        var text = utf16 ? enc.GetString(bytes, 2, bytes.Length - 2) : enc.GetString(bytes);
        var patched = Patch(text);
        if (patched == text) return false;
        var outBytes = enc.GetBytes(patched);
        if (utf16) outBytes = new byte[] { 0xFF, 0xFE }.Concat(outBytes).ToArray();
        var a = File.GetAttributes(path);
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, outBytes);
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    /// <summary>Pure text transform (exposed for tests).</summary>
    public static string Patch(string text)
    {
        string nl = text.Contains('\n') && !text.Contains("\r\n") ? "\n" : "\r\n";
        int pos = 0; bool inGame = false; int gameHeaderEnd = -1; int gameSectionEnd = -1;
        while (pos <= text.Length)
        {
            int eol = text.IndexOf('\n', pos);
            int lineEnd = eol < 0 ? text.Length : eol;
            int contentEnd = lineEnd > pos && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            var line = text.Substring(pos, contentEnd - pos);
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                if (inGame && gameSectionEnd < 0) gameSectionEnd = pos;
                inGame = string.Equals(trimmed, "[Game]", StringComparison.OrdinalIgnoreCase);
                if (inGame) gameHeaderEnd = eol < 0 ? text.Length : eol + 1;
            }
            else if (inGame)
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), "IsNative", StringComparison.OrdinalIgnoreCase))
                {
                    int vStart = eq + 1;
                    while (vStart < line.Length && (line[vStart] == ' ' || line[vStart] == '\t')) vStart++;
                    int vEnd = line.Length;
                    while (vEnd > vStart && (line[vEnd - 1] == ' ' || line[vEnd - 1] == '\t')) vEnd--;
                    if (line.Substring(vStart, vEnd - vStart) == "False") return text;
                    return text.Substring(0, pos + vStart) + "False" + text.Substring(pos + vEnd);
                }
            }
            if (eol < 0) break;
            pos = eol + 1;
        }
        if (gameHeaderEnd >= 0)
        {
            // [Game] exists without IsNative: insert right after the header line.
            var prefix = text.Substring(0, gameHeaderEnd);
            if (!prefix.EndsWith("\n")) prefix += nl;
            return prefix + "IsNative=False" + nl + text.Substring(gameHeaderEnd);
        }
        var sep = text.Length == 0 || text.EndsWith("\n") ? "" : nl;
        return text + sep + "[Game]" + nl + "IsNative=False" + nl;
    }
}

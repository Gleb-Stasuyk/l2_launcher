using System.Text;

namespace L2Launcher.Core;

/// <summary>
/// EN/RU switch for the enru_switch client layout (see /workspace/l2ru/enru_switch README):
///  - localization.ini runs the client in the "Korea" slot (Language=0, EnableEngSelection=true, Rus4la font).
///  - system\Option.ini [Game] IsNative=True  -> engine loads *-k.dat = RUSSIAN
///  - system\Option.ini [Game] IsNative=False -> engine loads *-e.dat = ENGLISH
/// Option.ini is a per-player file: we only change the IsNative key and keep every other byte/line as is.
/// </summary>
public static class GameLanguageService
{
    public const string OptionIniRelative = "system/Option.ini";

    /// <summary>Files that must exist for Russian to work (same list install_finish.bat checks).</summary>
    public static readonly string[] RussianRequiredFiles =
    {
        "system/localization.ini",
        "system/itemname-k.dat",
        "system/skillname-k.dat",
        "system/systemmsg-k.dat",
        "system/sysstring-k.dat",
        "system/SmallFont-r.gly",
        "systextures/L2Font-r.utx",
    };

    public static string IsNativeValue(GameLanguage lang) => lang == GameLanguage.Russian ? "True" : "False";

    public static List<string> MissingRussianFiles(string gamePath) =>
        RussianRequiredFiles.Where(f => !File.Exists(PathSafety.ResolveSafePath(gamePath, f))).ToList();

    /// <summary>Reads the current IsNative value, or null if file/key is missing.</summary>
    public static GameLanguage? Read(string gamePath)
    {
        var path = PathSafety.ResolveSafePath(gamePath, OptionIniRelative);
        if (!File.Exists(path)) return null;
        var (text, _) = ReadText(path);
        string? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            if (!string.Equals(section, "Game", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals("IsNative", StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? GameLanguage.Russian : GameLanguage.English;
        }
        return null;
    }

    /// <summary>Sets [Game] IsNative in system\Option.ini (creates file/section/key if missing). Returns the new file text.</summary>
    public static string Apply(string gamePath, GameLanguage lang)
    {
        var path = PathSafety.ResolveSafePath(gamePath, OptionIniRelative);
        string text; Encoding enc;
        if (File.Exists(path)) (text, enc) = ReadText(path);
        else { text = ""; enc = Latin1NoBom; Directory.CreateDirectory(Path.GetDirectoryName(path)!); }

        var result = SetKey(text, "Game", "IsNative", IsNativeValue(lang));
        if (result == text && File.Exists(path)) return text; // already correct, don't touch mtime

        var tmp = path + ".launcher.tmp";
        File.WriteAllText(tmp, result, enc);
        if (File.Exists(path))
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        File.Move(tmp, path, overwrite: true);
        return result;
    }

    /// <summary>Pure INI edit that preserves line endings and all other content.</summary>
    public static string SetKey(string text, string section, string key, string value)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : (text.Contains('\n') ? "\n" : "\r\n");
        var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        bool trailingNewline = lines.Count > 0 && lines[^1] == "";
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);

        int secStart = -1, secEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                if (secStart >= 0) { secEnd = i; break; }
                if (t[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) secStart = i;
            }
        }

        if (secStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim() != "") lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        else
        {
            bool found = false;
            for (int i = secStart + 1; i < secEnd; i++)
            {
                var eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{lines[i][..eq].Trim()}={value}";
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                int insertAt = secEnd;
                while (insertAt > secStart + 1 && lines[insertAt - 1].Trim() == "") insertAt--;
                lines.Insert(insertAt, $"{key}={value}");
            }
        }
        return string.Join(nl, lines) + nl;
    }

    private static readonly Encoding Latin1NoBom = Encoding.Latin1;

    /// <summary>Option.ini is ANSI in stock clients; handle a UTF-16 BOM too. Latin-1 round-trips any byte.</summary>
    private static (string, Encoding) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        return (Latin1NoBom.GetString(bytes), Latin1NoBom);
    }
}

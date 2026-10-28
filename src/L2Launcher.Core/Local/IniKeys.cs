using System.Text;
using System.Text.RegularExpressions;

namespace L2Launcher.Core.Local;

/// <summary>"Key = Value" files (server ini overrides, .env): replace owned keys in place, keep comments and other keys, append missing ones.</summary>
public static class IniKeys
{
    public static string Set(string text, IReadOnlyDictionary<string, string> values, string sep = " = ")
    {
        var nl = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^\s*([A-Za-z0-9_.]+)\s*=");
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var hit = values.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (hit == null) continue;
            if (done.Contains(hit)) { lines.RemoveAt(i--); continue; } // duplicate: keep one
            lines[i] = hit + sep + values[hit];
            done.Add(hit);
        }
        foreach (var kv in values) if (!done.Contains(kv.Key)) lines.Add(kv.Key + sep + kv.Value);
        return string.Join(nl, lines) + nl;
    }

    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = Regex.Match(raw, @"^\s*([A-Za-z0-9_.]+)\s*=\s*(.*?)\s*$");
            if (m.Success) d[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return d;
    }

    public static void SetFile(string path, IReadOnlyDictionary<string, string> values, string sep = " = ")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var next = Set(text, values, sep);
        if (next != text) AtomicWrite(path, next);
    }

    internal static void AtomicWrite(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

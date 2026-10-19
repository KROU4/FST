using System.Text;
using System.Text.RegularExpressions;
using Fst.Core;

namespace Fst.App;

public static class LumaBackend
{
    private const string Begin = "# >>> Fast Steam Tools managed keys >>>";
    private const string End = "# <<< Fast Steam Tools managed keys <<<";
    public static string Render(Settings settings)
    {
        var text = File.Exists(settings.LumaKeys) ? File.ReadAllText(settings.LumaKeys) : "";
        var begin = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if ((begin < 0) != (end < 0) || end >= 0 && end < begin || begin >= 0 && text.IndexOf(Begin, begin + Begin.Length, StringComparison.Ordinal) >= 0 || end >= 0 && text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException(L.T("Повреждён блок FST в keys.txt. Файл сохранён без изменений."));
        if (begin >= 0)
            text = text[..begin] + text[(end + End.Length)..];
        var manual = new HashSet<string>(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(';')[0].Trim()), StringComparer.Ordinal);
        var lines = new SortedDictionary<uint, string>();
        foreach (var entry in LuaCatalog.List(settings.LuaDirectories()))
        {
            var lua = File.ReadAllText(entry.Paths[0]);
            var pins = Regex.Matches(lua, "(?im)^\\s*setmanifestid\\s*\\(\\s*([0-9]+)\\s*,\\s*\"([0-9]+)\"").Cast<Match>().GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Last().Groups[2].Value);
            if (!manual.Contains(entry.AppId))
                lines[uint.Parse(entry.AppId)] = entry.AppId + ";";
            foreach (var match in Regex.Matches(lua, "(?im)^\\s*addappid\\s*\\(\\s*([0-9]+)\\s*,\\s*[0-9]+\\s*,\\s*\"([a-fA-F0-9]{64})\"").Cast<Match>())
            {
                var depot = match.Groups[1].Value;
                if (manual.Contains(depot))
                    continue;
                var key = match.Groups[2].Value.ToLowerInvariant();
                lines[uint.Parse(depot)] = depot == entry.AppId ? depot + ";" + key : depot + ";" + entry.AppId + ";" + pins.GetValueOrDefault(depot, "0") + ";0;" + key;
            }
        }
        var output = new StringBuilder(text.TrimEnd());
        if (output.Length > 0)
            output.Append('\n');
        if (lines.Count > 0)
        {
            output.AppendLine(Begin);
            foreach (var line in lines.Values)
                output.AppendLine(line);
            output.AppendLine(End);
        }
        return output.ToString();
    }
}

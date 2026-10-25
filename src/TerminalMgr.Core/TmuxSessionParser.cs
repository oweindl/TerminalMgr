namespace TerminalMgr.Core;

public sealed record TmuxSessionRecord(string Id, string Name, int Windows, int Attached);

public static class TmuxSessionParser
{
    public static bool IsSessionId(string value) =>
        value.Length > 1 && value[0] == '$' && value[1..].All(char.IsAsciiDigit);

    public static IReadOnlyList<TmuxSessionRecord> Parse(string output)
    {
        var sessions = new List<TmuxSessionRecord>();
        foreach (string line in output.Split('\n'))
        {
            string[] fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 4 || !IsSessionId(fields[0]) ||
                !int.TryParse(fields[2], out int windows) || windows < 0 ||
                !int.TryParse(fields[3], out int attached) || attached < 0) continue;
            sessions.Add(new(fields[0], fields[1], windows, attached));
        }
        return sessions.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

using System.Text;

namespace TerminalMgr.Core;

public sealed record SseEvent(string Name, string Data, string? Id);

/// <summary>Incremental parser for text/event-stream (WHATWG): events end with a blank line, lines starting with ":" are comments, several data lines are joined with a line feed.
/// Text can arrive in any pieces; a half line waits for the next chunk.</summary>
public sealed class SseParser
{
    private readonly StringBuilder _line = new();
    private string _name = "message", _id = "";
    private readonly List<string> _data = [];
    private bool _hasId;
    private bool _afterCr;
    public const int MaxLine = 1 << 20;

    public IReadOnlyList<SseEvent> Feed(string chunk)
    {
        var events = new List<SseEvent>();
        foreach (char c in chunk)
        {
            if (_afterCr) { _afterCr = false; if (c == '\n') continue; }
            if (c == '\r' || c == '\n')
            {
                _afterCr = c == '\r';
                Line(_line.ToString(), events);
                _line.Clear();
            }
            else
            {
                if (_line.Length >= MaxLine) throw new InvalidDataException("SSE-Zeile zu lang.");
                _line.Append(c);
            }
        }
        return events;
    }

    private void Line(string line, List<SseEvent> events)
    {
        if (line.Length == 0)
        {
            if (_data.Count > 0) events.Add(new(_name, string.Join("\n", _data), _hasId ? _id : null));
            _name = "message"; _data.Clear(); _hasId = false; _id = "";
            return;
        }
        if (line[0] == ':') return;
        int colon = line.IndexOf(':');
        string field = colon < 0 ? line : line[..colon];
        string value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "event": _name = value; break;
            case "data": _data.Add(value); break;
            case "id": _id = value; _hasId = true; break;
        }
    }
}

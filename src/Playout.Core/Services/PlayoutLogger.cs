using System.Collections.Generic;
using System.IO;
using Playout.Core.Models;

namespace Playout.Core.Services;

public record LogEntry(DateTime Timestamp, string FileName, string EventType, TimeSpan Duration);

public sealed class PlayoutLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();
    private readonly List<LogEntry> _history = new();

    public PlayoutLogger(string logPath = "playout_log.csv")
    {
        _logPath = logPath;
        InitializeLog();
    }

    private void InitializeLog()
    {
        if (!File.Exists(_logPath))
        {
            File.WriteAllText(_logPath, "Timestamp,FileName,Status,Duration\n");
        }
    }

    public void LogEntry(PlaylistItem item, string status)
    {
        var entry = new LogEntry(DateTime.UtcNow, item.FileName, status, item.EffectiveDuration);
        lock (_lock)
        {
            _history.Add(entry);
            var line = $"{entry.Timestamp:O},{entry.FileName},{entry.EventType},{entry.Duration}\n";
            File.AppendAllText(_logPath, line);
        }
    }

    public List<LogEntry> GetHistory()
    {
        lock (_lock)
        {
            return new List<LogEntry>(_history);
        }
    }

    public void ClearLogs()
    {
        lock (_lock)
        {
            _history.Clear();
            File.WriteAllText(_logPath, "Timestamp,FileName,Status,Duration\n");
        }
    }

    public string ExportToCsv()
    {
        return File.ReadAllText(_logPath);
    }
}

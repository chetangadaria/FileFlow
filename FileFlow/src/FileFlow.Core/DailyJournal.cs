using System.Text;

namespace FileFlow.Core;

public sealed class DailyJournal
{
    private readonly object _sync = new();
    private readonly string _logDirectory;

    public DailyJournal(string? logDirectory = null)
        => _logDirectory = logDirectory ?? FileFlowPaths.Logs;

    public void Write(string eventType, string task, string action,
        string source, string destination, string details)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_logDirectory);
            var now = DateTimeOffset.Now;
            var filename = Path.Combine(_logDirectory, $"{now:yyyy-MM-dd}.csv");
            var header = !File.Exists(filename) || new FileInfo(filename).Length == 0;
            using var writer = new StreamWriter(filename, true, new UTF8Encoding(false));
            if (header) writer.WriteLine("LocalTimestamp,Event,Task,Action,Source,Destination,Details");
            writer.WriteLine(string.Join(",", new[]
            {
                Cell(now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")),
                Cell(eventType), Cell(task), Cell(action),
                Cell(source), Cell(destination), Cell(details)
            }));
        }
    }

    private static string Cell(string text)
    {
        var cleaned = text.Replace("\r", " ").Replace("\n", " ");
        // Prevent spreadsheet formula injection when an administrator opens the CSV.
        if (cleaned.Length > 0 && "=+-@".Contains(cleaned[0])) cleaned = "'" + cleaned;
        return "\"" + cleaned.Replace("\"", "\"\"") + "\"";
    }
}

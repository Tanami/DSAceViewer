using System.Text;

namespace DSAceLogViewer.Core;

public static class LogExporter
{
    public static async Task ExportAsync(string path, IReadOnlyList<LogEntry> entries, bool csv, CancellationToken token = default)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(csv));
        if (csv) await writer.WriteLineAsync("Line,Timestamp,Category,Message".AsMemory(), token);
        foreach (var entry in entries)
        {
            var line = csv ? $"{entry.Number},{Escape(entry.Timestamp)},{Escape(entry.CategoryName)},{Escape(entry.Message)}" : entry.Raw;
            await writer.WriteLineAsync(line.AsMemory(), token);
        }
    }

    private static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}

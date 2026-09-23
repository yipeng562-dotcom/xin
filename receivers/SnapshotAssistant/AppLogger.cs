using System.Text;

namespace SnapshotAssistant;

public sealed class AppLogger
{
    private readonly object _gate = new();
    public string LogPath { get; }
    public event Action<string>? EntryWritten;

    public AppLogger(string? path = null)
    {
        LogPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapshotAssistant",
            "SnapshotAssistant.log");
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock(_gate)
            File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
        EntryWritten?.Invoke(line);
    }
}

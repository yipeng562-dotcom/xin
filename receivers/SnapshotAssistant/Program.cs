namespace SnapshotAssistant;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? exportDirectory = null;
        var port = 8765;
        for(var i = 0; i + 1 < args.Length; i++)
        {
            if(args[i].Equals("--export-dir", StringComparison.OrdinalIgnoreCase))
                exportDirectory = args[i + 1];
            if(args[i].Equals("--port", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(args[i + 1], out var parsed) && parsed is > 0 and <= 65535)
            {
                port = parsed;
            }
        }
        Application.Run(new MainForm(port, exportDirectory));
    }
}

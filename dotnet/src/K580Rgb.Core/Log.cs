namespace K580Rgb.Core;

/// <summary>Appends to %APPDATA%\k580rgb\k580rgb.log (rotated at 1 MB, 2 backups) and the console if there is one.</summary>
public static class Log
{
    const long MaxBytes = 1_000_000;
    const int Backups = 2;
    static readonly Lock _lock = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARNING", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e == null ? msg : $"{msg}\n{e}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {msg}";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ConfigDir);
                var path = AppPaths.LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    for (int i = Backups; i > 0; i--)
                    {
                        var src = i == 1 ? path : $"{path}.{i - 1}";
                        if (File.Exists(src))
                            File.Move(src, $"{path}.{i}", overwrite: true);
                    }
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException) { }               // logging must never take the app down
            catch (UnauthorizedAccessException) { }
            Console.Error.WriteLine(line);
        }
    }
}

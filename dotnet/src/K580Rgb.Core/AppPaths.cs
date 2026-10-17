namespace K580Rgb.Core;

/// <summary>Same folder as the Python app, so both read one config.</summary>
public static class AppPaths
{
    public static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "k580rgb");
    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");
    public static string LogPath => Path.Combine(ConfigDir, "k580rgb.log");
}

// K580Rgb.exe [--port N] [--background] [--no-resume]
//
// Runs headless in the system tray; the UI opens in its own window (WebView2), never in a browser.
// --background: don't open the window on start (used by "Start with Windows").
using System.Net.Http;
using System.Net.Sockets;
using K580Rgb.Core;
using K580Rgb.Core.Web;

namespace K580Rgb.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        int port = 5800;
        bool background = false, resume = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--background" or "--no-browser": background = true; break;
                case "--no-resume": resume = false; break;
            }
        }
        ApplicationConfiguration.Initialize();
        var url = $"http://127.0.0.1:{port}/";
        BackupPythonConfig();

        var controller = new Controller();
        var automation = new Automation(controller);
        using var host = new AppHost(controller, url);
        var api = new Api(controller, automation, port, host);
        HttpServer server;
        try
        {
            server = new HttpServer(port, api.Handle);
        }
        catch (SocketException)
        {
            return OtherInstance(url, background);
        }

        try
        {
            server.Start();
            controller.Start();
            automation.Start();
            if (resume)
                controller.RestoreLastLive();
            Log.Info($"K580 RGB {Api.Version} running at {url}");
            bool trayIcon = (bool?)controller.Config["tray_icon"] ?? true;
            if (!background && (((bool?)controller.Config["open_browser"] ?? true) || !trayIcon))
                host.ShowWindow();          // always when there's no icon to open it from
            Application.Run(host);          // until Quit (tray menu or the UI)
        }
        finally
        {
            server.Dispose();
            controller.Stop();
            Log.Info("K580 RGB stopped");
        }
        return 0;
    }

    /// <summary>The port is taken: bring up the running copy, or explain what holds the port.</summary>
    static int OtherInstance(string url, bool background)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            if (!http.GetStringAsync(url + "api/static").Result.Contains("\"version\""))
                throw new HttpRequestException("not K580 RGB");
            if (background)
                return 0;
            var req = new HttpRequestMessage(HttpMethod.Post, url + "api/show") { Content = new StringContent("{}") };
            req.Headers.Add("X-K580", "1");
            if (http.Send(req).IsSuccessStatusCode)
            {
                Log.Info("K580 RGB is already running; opened its window");
                return 0;
            }
            // only the Python app answers the API without /api/show
            MessageBox.Show("The Python version of K580 RGB is running.\n\nQuit it from its tray icon first, then start this one again.",
                            "K580 RGB", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is HttpRequestException or AggregateException or TaskCanceledException)
        {
            MessageBox.Show($"Port {new Uri(url).Port} is used by another program, so K580 RGB can't start.\n\nStart it with --port N to use another port.",
                            "K580 RGB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return 1;
    }

    /// <summary>The first run keeps a copy of the config the Python app wrote, in case you switch back.</summary>
    static void BackupPythonConfig()
    {
        var backup = Path.Combine(AppPaths.ConfigDir, "config.python-backup.json");
        try
        {
            if (File.Exists(AppPaths.ConfigPath) && !File.Exists(backup))
                File.Copy(AppPaths.ConfigPath, backup);
        }
        catch (IOException e)
        {
            Log.Warn($"could not back up the config: {e.Message}");
        }
    }
}

// The UI in its own window: WebView2 showing the local page. Closing the window disposes the
// browser engine (its memory is freed); the app keeps running in the tray.
using System.Diagnostics;
using K580Rgb.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace K580Rgb.App;

sealed class MainWindow : Form
{
    static readonly Color Background = Color.FromArgb(0x0e, 0x0f, 0x13);     // --bg in index.html
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    readonly string _url;

    public MainWindow(string url)
    {
        _url = url;
        Text = "K580 RGB";
        Icon = AppHost.AppIcon;
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Size = new Size(Math.Min(1400, area.Width - 80), Math.Min(980, area.Height - 60));
        MinimumSize = new Size(720, 480);
        Controls.Add(_web);
        Load += async (_, _) => await Init();
    }

    async Task Init()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "k580rgb", "webview2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: data);
            await _web.EnsureCoreWebView2Async(env);
            var s = _web.CoreWebView2.Settings;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = true;
            s.IsZoomControlEnabled = true;
            _web.CoreWebView2.NewWindowRequested += (_, e) =>     // links that open a new tab go to the real browser
            {
                e.Handled = true;
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            };
            _web.CoreWebView2.DocumentTitleChanged += (_, _) => Text = _web.CoreWebView2.DocumentTitle is { Length: > 0 } t ? t : "K580 RGB";
            _web.Source = new Uri(_url);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Log.Warn("WebView2 runtime not installed; opening the UI in the browser");
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            Close();
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;                // dark title bar to match the page (DWMWA_USE_IMMERSIVE_DARK_MODE)
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _web.Dispose();
        base.Dispose(disposing);
    }
}

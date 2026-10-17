namespace K580Rgb.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // placeholder until the controller and tray are ported: an icon with Quit
        using var icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "K580 RGB", Visible = true };
        icon.ContextMenuStrip = new ContextMenuStrip();
        icon.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Application.Exit());
        Application.Run();
        icon.Visible = false;
    }
}

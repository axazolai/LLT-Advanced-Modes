using System.Windows.Forms;
using LegionModes.Core;
using LegionModes.Core.Ipc;
using LegionModes.Core.Quick;

namespace LegionModes.Quick;

// Thin desktop-menu client: one argument, one message on the quick pipe, a balloon on failure.
// Exit codes: 0 done, 1 not delivered or refused, 2 unknown argument.
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Tr.FromLlt();
        var arg = args.Length == 1 ? args[0] : "";
        if (arg == "--register")
        {
            var idle = new MenuState(false, false, null, false, false, false, false, false);
            MenuRegistry.Write(MenuRegistry.Values(DesktopMenu.Build(idle), Paths.QuickExe, Paths.LltExe));
            return 0;
        }
        if (arg == "--unregister") { MenuRegistry.Remove(); return 0; }
        if (QuickActions.Parse(arg) is not { } kind) return 2;

        var reply = PipeClient.SendAsync(Paths.QuickPipe, new QuickAction(kind), TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        if (reply is null) return Balloon(Tr.T("Advanced modes is not running"));
        if (reply is Ack { Ok: false } nack) return Balloon(nack.Error ?? Tr.T("The action was not done"));
        return 0;
    }

    static int Balloon(string text)
    {
        using var icon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Visible = true };
        icon.ShowBalloonTip(5000, Tr.T("Advanced modes"), text, ToolTipIcon.Info);
        Thread.Sleep(5000);
        icon.Visible = false;
        return 1;
    }
}

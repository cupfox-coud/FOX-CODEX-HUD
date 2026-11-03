using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using CodexTokenHud;

// All fixture windows are offscreen and never take focus from the user's apps.
class ZOrderTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    static void Ensure(Hud hud)
    {
        typeof(Hud).GetField("nextZOrderCheck", Private).SetValue(hud, DateTime.MinValue);
        typeof(Hud).GetMethod("EnsureShown", Private).Invoke(hud, null);
    }
    static bool Above(IntPtr higher, IntPtr lower)
    {
        for (IntPtr p = Native.GetWindow(lower, 3); p != IntPtr.Zero; p = Native.GetWindow(p, 3))
            if (p == higher) return true;
        return false;
    }
    static void Move(IntPtr window, int x, int y)
    { Check(Native.SetWindowPos(window, IntPtr.Zero, x, y, 200, 200, 0x14), "fixture moves without activation"); }
    class NoActivateWindow : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }
    }

    [STAThread] static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        string root = Path.GetFullPath(args[0]);
        string temporary = Path.Combine(root, "build", "tests", "zorder-" + Guid.NewGuid().ToString("N"));
        var config = new RuntimeConfiguration { Folder = root, DataDirectory = temporary,
            CodexHome = Path.Combine(temporary, "empty-codex"), Pythonw = args[1], CornerMode = true, ResetAlerts = false };
        Hud hud = null;
        try
        {
            IntPtr foreground = Native.GetForegroundWindow();
            hud = new Hud(config);
            ((System.Windows.Forms.Timer)typeof(Hud).GetField("ticker", Private).GetValue(hud)).Stop();
            hud.Show();
            Ensure(hud);
            Move(hud.Handle, -30000, -30000);
            using (var other = new NoActivateWindow { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(-30000, -30000, 200, 200) })
            {
                other.Show();
                Move(other.Handle, -30000, -30000);
                // Reproduce a native demotion despite WinForms still caching the old topmost state.
                Check(Native.SetWindowPos(hud.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x13), "fixture demotes the HUD");
                Check(Native.SetWindowPos(other.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13), "ordinary fixture is placed above");
                Check(hud.TopMost && !Native.IsTopmost(hud.Handle) && Above(other.Handle, hud.Handle),
                    "WinForms can cache topmost while an ordinary overlapping window is actually above");
                Check(Native.CoveredByWindowAbove(hud.Handle), "actual overlapping window is detected");
                Rectangle original = hud.Bounds;
                Ensure(hud);
                Check(Native.IsTopmost(hud.Handle) && !Above(other.Handle, hud.Handle) && hud.Bounds == original,
                    "recovery moves the HUD above the covering ordinary window without moving or resizing it");
                Check(Native.GetForegroundWindow() == foreground, "recovery does not steal keyboard focus");

                Check(Native.SetWindowPos(other.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13), "competing topmost fixture is raised");
                Check(Native.IsTopmost(hud.Handle) && Above(other.Handle, hud.Handle) && Native.CoveredByWindowAbove(hud.Handle),
                    "an overlapping topmost peer is detected despite the HUD's topmost style");
                Ensure(hud);
                Check(!Above(other.Handle, hud.Handle) && Native.GetForegroundWindow() == foreground,
                    "the HUD recovers from an overlapping topmost peer without activating");

                Move(other.Handle, -29600, -29600);
                Check(Native.SetWindowPos(other.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13), "nonoverlapping peer is raised");
                long repairs = (long)typeof(Hud).GetField("zOrderRepairs", Private).GetValue(hud);
                Check(Above(other.Handle, hud.Handle) && !Native.CoveredByWindowAbove(hud.Handle),
                    "nonoverlapping topmost windows do not count as covering the HUD");
                Ensure(hud);
                Check(Above(other.Handle, hud.Handle) && (long)typeof(Hud).GetField("zOrderRepairs", Private).GetValue(hud) == repairs,
                    "nonoverlapping windows are not needlessly reordered");

                Move(other.Handle, -30000, -30000);
                other.Hide();
                Check(!Native.CoveredByWindowAbove(hud.Handle), "hidden overlapping windows are ignored");
            }
            var gate = typeof(Hud).GetMethod("ShouldCheckZOrder", BindingFlags.Static | BindingFlags.NonPublic);
            var now = DateTime.UtcNow;
            Check(!(bool)gate.Invoke(null, new object[] { true, true, now, now.AddSeconds(-1) }),
                "open tray menus suspend Z-order repairs");
            Check(!(bool)gate.Invoke(null, new object[] { true, false, now, now.AddSeconds(1) }) &&
                (bool)gate.Invoke(null, new object[] { true, false, now, now.AddSeconds(-1) }),
                "repairs are rate-limited and resume after the menu closes");
            Check(Native.DeferForTransient("#32768", 0, false, false, 0, 0) &&
                Native.DeferForTransient("tooltips_class32", 0, false, false, 0, 0) &&
                Native.DeferForTransient("dialog", 0, true, true, 1, 1),
                "native menus, tooltips and an active owned dialog take precedence over HUD recovery");
            Check(Native.DeferForTransient("Chrome_WidgetWin_1", 0x80, true, false, 5, 5) &&
                !Native.DeferForTransient("Chrome_WidgetWin_1", 0x80, true, false, 5, 6) &&
                !Native.DeferForTransient("Chrome_WidgetWin_1", 0, false, false, 0, 5),
                "active Chromium popups are preserved while ordinary browser windows remain eligible for recovery");
            Check(Native.GetForegroundWindow() == foreground, "all offscreen checks leave the user's foreground window unchanged");
            Console.WriteLine("Z-order checks passed: " + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (hud != null) { hud.Close(); hud.Dispose(); } }
    }
}

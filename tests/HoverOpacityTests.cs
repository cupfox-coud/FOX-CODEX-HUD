using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexTokenHud;

// Synthetic cursor positions exercise the real native alpha; the user's pointer never moves.
class HoverOpacityTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    static int checks;
    static void Check(bool value, string name)
    { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static object Call(Hud hud, string name, params object[] values)
    { return typeof(Hud).GetMethod(name, Private).Invoke(hud, values); }
    static object Field(Hud hud, string name)
    { return typeof(Hud).GetField(name, Private).GetValue(hud); }
    static void Set(Hud hud, string name, object value)
    { typeof(Hud).GetField(name, Private).SetValue(hud, value); }
    static int Alpha(Hud hud)
    {
        uint key, flags; byte alpha;
        if (!GetLayeredWindowAttributes(hud.Handle, out key, out alpha, out flags) || (flags & 2) == 0)
            throw new Exception("Native window opacity is unavailable");
        return alpha;
    }
    static void Hover(Hud hud, int x, int y, bool available, double now)
    { Call(hud, "UpdateCornerHover", new Native.Point { X = x, Y = y }, available, now); }
    static int Hit(Hud hud, int x, int y)
    { return SendMessage(hud.Handle, 0x84, IntPtr.Zero, new IntPtr(((long)(y & 0xffff) << 16) | (uint)(x & 0xffff))).ToInt32(); }

    [STAThread] static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        string root = Path.GetFullPath(args[0]);
        string temporary = Path.Combine(root, "build", "tests", "hover-" + Guid.NewGuid().ToString("N"));
        var config = new RuntimeConfiguration { Folder = root, DataDirectory = temporary,
            CodexHome = Path.Combine(temporary, "empty-codex"), Pythonw = args[1], CornerMode = true,
            CornerOpacity = 0.78, CornerMotion = "on", CornerHoverFade = true, ResetAlerts = false };
#if HUD_TEST
        RenderMemoryGuard.TestSnapshot = new MemorySnapshot { CommitLimit = 100UL << 30,
            CommitAvailable = 20UL << 30, PhysicalAvailable = 8UL << 30 };
#endif
        Hud hud = null;
        try
        {
            IntPtr foreground = Native.GetForegroundWindow();
            hud = new Hud(config);
            ((Timer)Field(hud, "ticker")).Stop();
            ((Timer)Field(hud, "motionTicker")).Stop();
            ((Timer)Field(hud, "hoverTicker")).Stop();
            hud.Show();
            Check(Native.SetWindowPos(hud.Handle, IntPtr.Zero, -30000, -30000, 0, 0, 0x15),
                "offscreen fixture moves without resizing or activating");
            Set(hud, "scale", 1.0);
            Set(hud, "motionAllowed", true);
            Native.Rect rect;
            Check(Native.GetWindowRect(hud.Handle, out rect), "fixture window bounds are available");
            int x = rect.Left + 200, y = rect.Top + 300;
            Call(hud, "RestoreCornerHover");
            int normal = Alpha(hud);
            Check(Math.Abs(normal - 199) <= 1, "normal opacity respects the user's saved 78 percent setting");
            Hover(hud, x, y, true, 1000);
            Check((bool)Field(hud, "hoverDimmed") && Alpha(hud) == normal,
                "a synthetic screen position starts the hover transition without jumping");
            Hover(hud, x, y, true, 1090);
            Check(Alpha(hud) < normal && Alpha(hud) > 64, "hover fades through an intermediate native alpha");
            Hover(hud, x, y, true, 1180);
            Check(Math.Abs(Alpha(hud) - 64) <= 1 && Math.Abs(hud.Opacity - 0.78) < 0.01,
                "hover reaches 25 percent without changing the saved normal opacity");
            Check((Native.GetWindowLongPtr(hud.Handle, -20).ToInt64() & 0x20) != 0 && Hit(hud, x, y) == -1,
                "the dimmed card remains click-through in native style and hit testing");
            Hover(hud, rect.Right + 20, y, true, 1200);
            Hover(hud, rect.Right + 20, y, true, 1380);
            Check(!((bool)Field(hud, "hoverDimmed")) && Alpha(hud) == normal,
                "leaving the card restores the original opacity");

            var contains = typeof(Hud).GetMethod("CursorInsideRoundedCard", BindingFlags.NonPublic | BindingFlags.Static);
            var negativeScreen = new Native.Rect { Left = -1920, Top = -1000, Right = -1520, Bottom = -312 };
            Check((bool)contains.Invoke(null, new object[] { negativeScreen, new Native.Point { X = -1720, Y = -656 }, 39.0 }) &&
                !(bool)contains.Invoke(null, new object[] { negativeScreen, new Native.Point { X = -1920, Y = -1000 }, 39.0 }) &&
                !(bool)contains.Invoke(null, new object[] { negativeScreen, new Native.Point { X = -1921, Y = -656 }, 39.0 }),
                "negative screen coordinates, rounded corners and DPI-scaled radius are handled correctly");
            var gate = typeof(Hud).GetMethod("ShouldDimOnHover", BindingFlags.NonPublic | BindingFlags.Static);
            Check(!(bool)gate.Invoke(null, new object[] { true, true, true, true, true }) &&
                !(bool)gate.Invoke(null, new object[] { true, true, false, false, true }) &&
                !(bool)gate.Invoke(null, new object[] { true, false, true, false, true }) &&
                !(bool)gate.Invoke(null, new object[] { true, true, true, false, false }),
                "open menus, temporary interaction, disabled preference and hidden cards suppress dimming");

            Set(hud, "motionAllowed", false);
            Hover(hud, x, y, true, 2000);
            Check(Math.Abs(Alpha(hud) - 64) <= 1, "reduced-motion mode still dims immediately");
            Hover(hud, x, y, false, 2001);
            Check(Alpha(hud) == normal, "an unavailable cursor reading restores normal opacity");
            Hover(hud, x, y, true, 2002);
            Call(hud, "SetCornerClickThrough", false);
            Check(Alpha(hud) == normal && (Native.GetWindowLongPtr(hud.Handle, -20).ToInt64() & 0x20) == 0,
                "temporarily unlocking the card restores readability and ordinary input");
            Call(hud, "SetCornerClickThrough", true);
            config.CornerOpacity = 0.90;
            hud.Opacity = config.CornerOpacity;
            Call(hud, "SetCornerClickThrough", true);
            int changedNormal = Alpha(hud);
            Hover(hud, x, y, true, 2100);
            Hover(hud, rect.Right + 20, y, true, 2101);
            Check(Math.Abs(changedNormal - 230) <= 1 && Alpha(hud) == changedNormal,
                "changing the normal opacity is respected after the next hover");
            config.CornerHoverFade = false;
            config.SaveCornerSettings();
            Hover(hud, x, y, true, 2200);
            Check(Alpha(hud) == changedNormal && Json.Text(Json.Read(Path.Combine(temporary, "settings.json")), "corner_hover_fade") == "False",
                "disabling hover fade persists and keeps the chosen normal opacity");
            string oldHome = Environment.GetEnvironmentVariable("CODEX_TOKEN_HUD_HOME");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_TOKEN_HUD_HOME", temporary);
                Check(!RuntimeConfiguration.Load(root).CornerHoverFade, "disabled hover preference is restored on the next launch");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_TOKEN_HUD_HOME", oldHome); }
            Call(hud, "BuildMenu");
            var menu = (ContextMenuStrip)Field(hud, "menu");
            var option = menu.Items.Cast<ToolStripItem>().OfType<ToolStripMenuItem>().Single(i => i.Text == "悬停时淡化");
            Check(!option.Checked, "tray menu reflects the saved hover setting");
            option.PerformClick();
            Check(config.CornerHoverFade && Json.Text(Json.Read(Path.Combine(temporary, "settings.json")), "corner_hover_fade") == "True",
                "tray menu can enable hover fade and save the preference");
            Hover(hud, x, y, true, 2300);
            menu.GetType().GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs(false) });
            Check(Alpha(hud) == changedNormal && !(bool)Field(hud, "hoverDimmed"),
                "opening the real tray menu restores normal opacity immediately");
#if HUD_TEST
            var simulated = new Native.Point { X = x, Y = y };
            int cursorReads = 0;
            hud.TestHoverCursor = delegate { cursorReads++; return simulated; };
            var timer = (Timer)Field(hud, "hoverTicker");
            timer.Interval = 20;
            config.CornerHoverFade = false;
            timer.Start();
            var disabledUntil = DateTime.UtcNow.AddMilliseconds(150);
            while (DateTime.UtcNow < disabledUntil) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            Check(cursorReads == 0 && Alpha(hud) == changedNormal,
                "disabling hover fade stops sampling the cursor");
            config.CornerHoverFade = true;
            var until = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < until && Math.Abs(Alpha(hud) - 64) > 1)
            { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            Check(Math.Abs(Alpha(hud) - 64) <= 1 && !((Timer)Field(hud, "motionTicker")).Enabled,
                "the independent cursor timer dims even while decorative animation is stopped");
            simulated = new Native.Point { X = rect.Right + 20, Y = y };
            until = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < until && Alpha(hud) != changedNormal)
            { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            Check(Alpha(hud) == changedNormal, "the cursor timer restores opacity when the pointer leaves");
            timer.Stop();
            hud.TestHoverCursor = null;
#endif
            var memory = (RenderMemoryGuard)Field(hud, "renderMemory");
            memory.AllocationFailed();
            Call(hud, "ApplyMemoryProtection");
            Hover(hud, x, y, true, 3000);
            Check(((Timer)Field(hud, "hoverTicker")).Interval == 125 && Math.Abs(Alpha(hud) - 64) <= 1,
                "low-memory mode keeps hover fade responsive without animation frames");
            Hover(hud, rect.Right + 20, y, true, 3001);
            Check(Alpha(hud) == changedNormal, "low-memory mode restores normal opacity on exit");
            Check(Native.GetForegroundWindow() == foreground, "hover updates do not take keyboard focus");
            Console.WriteLine("Hover opacity checks passed: " + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
#if HUD_TEST
            RenderMemoryGuard.TestSnapshot = null;
#endif
            if (hud != null) { hud.Close(); hud.Dispose(); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexTokenHud;

class CornerHudTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.Point point);
    static int checks;
    static object Call(Hud hud, string name, params object[] args)
    { return typeof(Hud).GetMethod(name, Private).Invoke(hud, args); }
    static object Field(object hud, string name)
    { return hud.GetType().GetField(name, Private).GetValue(hud); }
    static void Set(Hud hud, string name, object value)
    { typeof(Hud).GetField(name, Private).SetValue(hud, value); }
    static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static int Hit(Hud hud, int x, int y)
    { return SendMessage(hud.Handle, 0x84, IntPtr.Zero, new IntPtr(((long)(y & 0xffff) << 16) | (uint)(x & 0xffff))).ToInt32(); }

    [STAThread] static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        var root = Path.GetFullPath(args[0]);
        var temporary = Path.Combine(root, "build", "tests", "corner-" + Guid.NewGuid().ToString("N"));
        var target = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen;
        var config = new RuntimeConfiguration { Folder = root, DataDirectory = temporary,
            CodexHome = Path.Combine(temporary, "empty-codex"), Pythonw = args[1],
            CornerMode = true, CornerMonitor = target.DeviceName, CornerOpacity = 0.78, ResetAlerts = false };
        Hud hud = null;
        try
        {
            var foreground = Native.GetForegroundWindow();
            hud = new Hud(config);
            ((System.Windows.Forms.Timer)Field(hud, "ticker")).Stop();
            hud.StartMonitoring();
            Check(Json.Text(Json.Read(Path.Combine(config.Runtime, "selection.json")), "binding_mode") == "activity",
                "corner starts following latest local activity without relying on window title recognition");
            Call(hud, "PositionHud");
            double scale = Native.Scale(hud.Handle);
            int margin = (int)Math.Round(18 * scale);
            Check(hud.Right == target.WorkingArea.Right - margin && hud.Top == target.WorkingArea.Top + margin,
                "card is anchored at the secondary monitor top-right in physical coordinates");
            Check(hud.Width == (int)Math.Round(368 * scale) && hud.Height == (int)Math.Round(374 * scale),
                "compact card respects the destination monitor DPI");
            Check(Native.IsWindowVisible(hud.Handle) && Native.IsTopmost(hud.Handle) && Native.GetForegroundWindow() == foreground,
                "card stays visible without any Codex window and never takes focus");
            long style = Native.GetWindowLongPtr(hud.Handle, -20).ToInt64();
            Check((style & 0x08080020) == 0x08080020, "native window is layered, click-through and no-activate");
            uint key, flags; byte alpha;
            bool layered = GetLayeredWindowAttributes(hud.Handle, out key, out alpha, out flags);
            Console.WriteLine("Native alpha=" + alpha + " flags=" + flags + " queried=" + layered);
            Check(layered && Math.Abs(alpha - 199) <= 1 && (flags & 2) != 0,
                "Windows applies 78 percent opacity");
            Check(Hit(hud, hud.Left + 2, hud.Top + 2) == -1 && Hit(hud, hud.Left + hud.Width / 2, hud.Top + hud.Height / 2) == -1 &&
                Hit(hud, hud.Right - 2, hud.Bottom - 2) == -1, "edges and card content pass hit tests through instead of dragging or resizing");
            var point = new Native.Point { X = hud.Left + hud.Width / 2, Y = hud.Top + hud.Height / 2 };
            Check(WindowFromPoint(point) != hud.Handle, "Windows resolves input at the card center to the underlying window");
            var anchored = hud.Bounds;
            hud.Bounds = new Rectangle(hud.Left - 200, hud.Top + 150, hud.Width + 100, hud.Height + 50);
            Call(hud, "PositionHud");
            Check(hud.Bounds == anchored, "unexpected move or resize is corrected back to the corner");
            Call(hud, "SetCornerClickThrough", false);
            Check((Native.GetWindowLongPtr(hud.Handle, -20).ToInt64() & 0x20) == 0 && Hit(hud, point.X, point.Y) == 1,
                "temporary tray unlock enables only ordinary client interaction");
            Call(hud, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, hud.Width - 25, hud.Height / 2, 0));
            Check(!hud.IsDisposed && Native.IsWindowVisible(hud.Handle), "clicking the former close-button area cannot quit the card");
            Set(hud, "cornerUnlockUntil", DateTime.UtcNow.AddSeconds(-1));
            Set(hud, "nextInspection", DateTime.MaxValue);
            Set(hud, "nextBackendCheck", DateTime.MaxValue);
            Call(hud, "Tick", null, EventArgs.Empty);
            Check((Native.GetWindowLongPtr(hud.Handle, -20).ToInt64() & 0x20) != 0,
                "expired temporary unlock automatically restores click-through");
            var menu = (ContextMenuStrip)Field(hud, "menu");
            menu.Show(new Point(hud.Left, hud.Bottom));
            Application.DoEvents();
            Check(menu.Visible && menu.Items.Count > 0, "tray menu opens on the first request before items have been built");
            var previousItems = menu.Items.Cast<ToolStripItem>().ToArray();
            int disposedItems = 0;
            foreach (var item in previousItems) item.Disposed += delegate { disposedItems++; };
            menu.Close();
            Application.DoEvents();
            Check(!menu.Visible, "tray menu dismisses normally");
            menu.Show(new Point(hud.Left, hud.Bottom));
            Application.DoEvents();
            Check(menu.Visible && menu.Items.Count > 0, "tray menu opens again after closing");
            Check(disposedItems == previousItems.Length && previousItems.All(i => !menu.Items.Contains(i)),
                "rebuilding the tray menu releases previous menu items");
            menu.Close();
            string closedReason = "";
            menu.Closed += delegate(object sender, ToolStripDropDownClosedEventArgs e) { closedReason = e.CloseReason.ToString(); };
            typeof(NotifyIcon).GetMethod("ShowContextMenu", Private).Invoke(Field(hud, "tray"), null);
            var menuDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < menuDeadline)
            {
                Application.DoEvents();
                Call(hud, "Tick", null, EventArgs.Empty);
                System.Threading.Thread.Sleep(150);
            }
            Check(menu.Visible, "native tray menu stays open through live HUD ticks; close reason=" + closedReason);
            var liveOpacity = menu.Items.Cast<ToolStripItem>().OfType<ToolStripMenuItem>().Single(i => i.Text == "不透明度");
            liveOpacity.ShowDropDown();
            for (int tick = 0; tick < 10; tick++)
            {
                Call(hud, "Tick", null, EventArgs.Empty);
                Application.DoEvents();
                System.Threading.Thread.Sleep(150);
            }
            Check(menu.Visible && liveOpacity.DropDown.Visible, "opacity submenu stays open while HUD refreshes");
            liveOpacity.DropDownItems.Cast<ToolStripItem>().Single(i => i.Text == "100%").PerformClick();
            Check(hud.Opacity == 1 && (Native.GetWindowLongPtr(hud.Handle, -20).ToInt64() & 0x20) != 0,
                "command from live tray submenu works without disabling click-through");
            menu.Close();
            typeof(NotifyIcon).GetMethod("ShowContextMenu", Private).Invoke(Field(hud, "tray"), null);
            Call(hud, "Tick", null, EventArgs.Empty);
            Application.DoEvents();
            Check(menu.Visible, "native tray menu can reopen after a command");
            typeof(ToolStripDropDown).GetMethod("ProcessDialogKey", Private).Invoke(menu, new object[] { Keys.Escape });
            Application.DoEvents();
            Check(!menu.Visible, "Escape dismisses the tray menu normally");
            Check(menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "登录后常驻显示") &&
                !menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "恢复底部默认位置和大小"), "tray exposes resident controls instead of obsolete strip positioning");
            var opacityMenu = menu.Items.Cast<ToolStripItem>().OfType<ToolStripMenuItem>().Single(i => i.Text == "不透明度");
            opacityMenu.DropDownItems.Cast<ToolStripItem>().Single(i => i.Text == "100%").PerformClick();
            Check(GetLayeredWindowAttributes(hud.Handle, out key, out alpha, out flags) && alpha == 255 &&
                (Native.GetWindowLongPtr(hud.Handle, -20).ToInt64() & 0x20) != 0, "fully opaque tray option preserves native click-through");
            opacityMenu.DropDownItems.Cast<ToolStripItem>().Single(i => i.Text.StartsWith("78%")).PerformClick();
            config.CornerMonitor = "disconnected-monitor";
            Call(hud, "PositionHud");
            Check(hud.Bounds == anchored, "missing saved monitor falls back to the available secondary monitor");
            hud.Hide();
            Call(hud, "PositionHud");
            Check(Native.IsWindowVisible(hud.Handle) && Native.IsTopmost(hud.Handle), "visibility recovers independently of application focus");
            double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            Set(hud, "data", new Dictionary<string, object> {
                {"title", "Preview fixture"}, {"thread_id", "fixture"}, {"binding", "ok"}, {"binding_mode", "activity"}, {"updated_at_ms", now},
                {"source", new Dictionary<string, object> {{"state", "ok"}}},
                {"reset_radar", new Dictionary<string, object> {{"state", "ok"}, {"fetched_at_ms", now}, {"source_updated_at_ms", now}, {"upcoming", null}}},
                {"account_quota", new Dictionary<string, object> {{"state", "ok"}, {"updated_at_ms", now},
                    {"windows", new object[] { new Dictionary<string, object> {{"window_minutes", 10080}, {"remaining_percent", 59}, {"resets_at", now / 1000 + 86400 * 6}} }}}},
                {"network_latency", new Dictionary<string, object> {{"state", "ok"}, {"updated_at_ms", now - 3000},
                    {"nodes", new object[] { Node("ai", 223, now), Node("general", 66, now) }}}},
                {"metrics", new Dictionary<string, object> {{"model", "gpt-6.1-sol"}, {"stage", "tools"},
                    {"response_duration", new Dictionary<string, object> {{"mean_ms", 142000}, {"count", 12}, {"limit", 20}}},
                    {"turn_active", true}, {"last_event_at_ms", now - 12000},
                    {"last", new Dictionary<string, object> {{"model", "gpt-6.1-sol"}, {"measured_at_ms", now}, {"rate", 87.4}, {"cache_percent", 98.8}}}}} });
            Call(hud, "UpdateView", new object[] { null });
            Call(hud, "UpdateMetrics");
            StressDrawing(hud);
            using (var bitmap = new Bitmap(hud.Width, hud.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(hud.BackColor);
                Call(hud, "OnPaint", new PaintEventArgs(graphics, new Rectangle(Point.Empty, hud.Size)));
                bitmap.Save(Path.Combine(root, "build", "corner-preview.png"));
            }
            Check((string)Field(hud, "rateText") == "87.4" && (string)Field(hud, "cacheText") == "缓存 98.8%",
                "compact presentation retains real metric formatting and latest-activity attribution");
            Check(((string)Field(hud, "weeklyText")).Contains("周额度剩余 59%") && !(bool)Field(hud, "quotaCached"),
                "weekly quota is independent from thread metrics and shows remaining percentage");
            var fixture = (Dictionary<string, object>)Field(hud, "data");
            var quota = Json.Object(fixture, "account_quota");
            quota["state"] = "unavailable";
            Call(hud, "UpdateMetrics");
            Check((bool)Field(hud, "quotaCached") && ((string)Field(hud, "quotaSyncText")).Contains("上次数据"),
                "network failure explicitly identifies cached account quota");
            quota["updated_at_ms"] = now - 901000;
            Call(hud, "UpdateMetrics");
            Check(((List<Dictionary<string, object>>)Field(hud, "quotaWindows")).Count == 0,
                "old account quota is hidden instead of presented as current");
            quota["state"] = "ok";
            quota["updated_at_ms"] = now;
            quota["windows"] = new object[] {new Dictionary<string, object> {{"window_minutes", 300}, {"remaining_percent", 72}, {"resets_at", now / 1000 + 3600}},
                new Dictionary<string, object> {{"window_minutes", 10080}, {"remaining_percent", 59}, {"resets_at", now / 1000 + 86400 * 6}}};
            Call(hud, "UpdateMetrics");
            using (var bitmap = new Bitmap(hud.Width, hud.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(hud.BackColor);
                Call(hud, "OnPaint", new PaintEventArgs(graphics, new Rectangle(Point.Empty, hud.Size)));
                bitmap.Save(Path.Combine(root, "build", "corner-two-windows.png"));
            }
            Check(((List<Dictionary<string, object>>)Field(hud, "quotaWindows")).Count == 2,
                "all returned account windows remain parsed while the card prioritizes weekly quota");
            quota["windows"] = new object[] {new Dictionary<string, object> {{"window_minutes", 10080}, {"remaining_percent", 93}, {"resets_at", now / 1000 + 86400 * 6}}};
            var metrics = Json.Object(fixture, "metrics");
            metrics["stage"] = "generating";
            foreach (int seconds in new[] {12, 45, 90, 150})
            {
                metrics["last_event_at_ms"] = now - seconds * 1000;
                var ai = Json.List(Json.Object(fixture, "network_latency"), "nodes").First();
                ai["delay_ms"] = seconds == 150 ? (object)null : seconds == 90 ? 1050 : 223;
                ai["state"] = seconds == 150 ? "timeout" : "ok";
                Call(hud, "UpdateMetrics");
                SavePreview(hud, Path.Combine(root, "build", "corner-wait-" + seconds + ".png"));
                Check(((ActivityDisplay)Field(hud, "activity")).Severity == (seconds == 12 ? 0 : seconds == 45 ? 1 : seconds == 90 ? 2 : 3),
                    "activity severity and timer reflect " + seconds + " seconds without log updates");
            }
            metrics["turn_active"] = false;
            metrics["last_event_at_ms"] = now - 7200000;
            Call(hud, "UpdateMetrics");
            SavePreview(hud, Path.Combine(root, "build", "corner-idle.png"));
            Check(((ActivityDisplay)Field(hud, "activity")).Severity == 0, "completed task never escalates with age");
            fixture["updated_at_ms"] = now - 10000;
            Call(hud, "UpdateMetrics");
            SavePreview(hud, Path.Combine(root, "build", "corner-paused.png"));
            Check(((ActivityDisplay)Field(hud, "activity")).Title == "采集暂停", "collector pause is distinct from a slow model");
            fixture["updated_at_ms"] = now;
            var radar = Json.Object(fixture, "reset_radar");
            radar["upcoming"] = new Dictionary<string, object> {{"id", "demo-plan"}, {"kind", "reset_scheduled"},
                {"start_ms", now + 1420000}, {"end_ms", now + 1420000}, {"precision", "exact"}, {"basis", "explicit"}};
            Call(hud, "UpdateMetrics");
            SavePreview(hud, Path.Combine(root, "build", "corner-reset-scheduled.png"));
            Check(((ResetRadarDisplay)Field(hud, "resetRadar")).Alert, "near scheduled reset is prominent in the small radar card");
            radar["upcoming"] = null;
            quota["windows"] = new object[] {new Dictionary<string, object> {{"window_minutes", 10080}, {"remaining_percent", 59}, {"resets_at", now / 1000 - 1}}};
            Call(hud, "UpdateMetrics");
            Check(((List<Dictionary<string, object>>)Field(hud, "quotaWindows")).Count == 0 &&
                ((string)Field(hud, "quotaNotice")).Contains("等待同步"), "passed reset hides the old balance without guessing a new percentage");
            CheckDiagnostics(temporary);
            CheckMotion(hud, root);
            Console.WriteLine("Corner HUD checks passed: " + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (hud != null) { hud.Close(); hud.Dispose(); } }
    }

    static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    static void StressDrawing(Hud hud)
    {
        using (var bitmap = new Bitmap(hud.Width, hud.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var ring = typeof(Hud).GetMethod("PaintWaitRing", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (float sweep in new[] { float.NaN, float.PositiveInfinity, -1, 0, 1e-8f, 1e-5f, .001f, .01f, .1f, .249f, .25f, 1, 90, 359.999f, 360, 400 })
                ring.Invoke(null, new object[] { graphics, new RectangleF(222, 301, 18, 18), sweep, Color.White });
            Check(true, "wait ring handles tiny, full and invalid sweep angles without GDI+ DrawArc");
            for (int i = 0; i < 30; i++) Call(hud, "PaintCorner", graphics);
            Collect();
            var before = HudDiagnostics.Resources();
            var paintWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++)
            {
                Call(hud, "PaintCorner", graphics);
                if (i % 100 == 0) Application.DoEvents();
            }
            paintWatch.Stop();
            Console.WriteLine("Full card average render ms=" + (paintWatch.Elapsed.TotalMilliseconds / 2000).ToString("0.000"));
            Collect();
            var after = HudDiagnostics.Resources();
            Console.WriteLine("Drawing resources before=" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(before) +
                " after=" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(after));
            Check(Convert.ToInt64(after["gdi_objects"]) <= Convert.ToInt64(before["gdi_objects"]) + 8 &&
                Convert.ToInt64(after["user_objects"]) <= Convert.ToInt64(before["user_objects"]) + 8,
                "2000 full card redraws keep GDI and USER resources stable");
            Check(Convert.ToInt64(after["private_bytes"]) - Convert.ToInt64(before["private_bytes"]) < 32 * 1024 * 1024,
                "2000 full card redraws retain less than 32 MiB after collection");
            using (var transform = graphics.Transform)
                Check(transform.IsIdentity, "card drawing restores the caller graphics transform");
            Check(((ToolTip)Field(hud, "tooltip")).GetToolTip(hud) == "",
                "click-through corner mode does not update disabled native tooltip text");
        }
    }

    static void CheckDiagnostics(string temporary)
    {
        HudDiagnostics.Install(temporary);
        try { throw new OutOfMemoryException("private-fixture-do-not-log"); }
        catch (OutOfMemoryException) { }
        string log = Path.Combine(temporary, "hud-errors.jsonl");
        string recorded = File.ReadAllText(log);
        Check(recorded.Contains("System.OutOfMemoryException") && recorded.Contains("CheckDiagnostics") &&
            recorded.Contains("gdi_objects") && !recorded.Contains("private-fixture-do-not-log"),
            "allocation exception is recorded with stack and resources, without private message text");
        try { throw new OutOfMemoryException(); } catch (OutOfMemoryException) { }
        Check(File.ReadAllText(log) == recorded, "repeated allocation errors are throttled");
        File.WriteAllText(log, new string('x', HudDiagnostics.MaximumLogBytes));
        HudDiagnostics.Record(new InvalidOperationException(), "test", false);
        Check(File.Exists(log + ".1") && new FileInfo(log).Length < HudDiagnostics.MaximumLogBytes,
            "diagnostic log rotates within its size limit");
        string blocked = Path.Combine(temporary, "not-a-directory");
        File.WriteAllText(blocked, "fixture");
        // Exercise an unwritable destination without installing another event observer.
        typeof(HudDiagnostics).GetField("directory", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, blocked);
        HudDiagnostics.Record(new InvalidOperationException(), "test", false);
        Check(File.ReadAllText(blocked) == "fixture", "diagnostic write failure does not escape to the application");
    }

    static void CheckMotion(Hud hud, string root)
    {
        var motion = Field(hud, "aiMotion");
        var update = motion.GetType().GetMethod("Update", Private);
        var color = motion.GetType().GetMethod("ReadColor", Private);
        update.Invoke(motion, new object[] {0, Color.Aquamarine, 1.0, 1000.0, "AI节点"});
        update.Invoke(motion, new object[] {4, Color.Red, 2.0, 2000.0, "AI节点"});
        Check((string)Field(motion, "Notice") == "AI节点 · 探测超时", "timeout transitions announce a route-specific failure");
        update.Invoke(motion, new object[] {4, Color.Red, 2.0, 4000.0, "AI节点"});
        Check((double)Field(motion, "ChangedAt") == 2000, "unchanged failure does not restart the notification");
        Check(((Color)color.Invoke(motion, new object[] {2000.0, false})).ToArgb() == Color.Red.ToArgb(),
            "reduced motion immediately uses the accurate severity color");
        update.Invoke(motion, new object[] {0, Color.Aquamarine, 3.0, 5000.0, "AI节点"});
        Check(((string)Field(motion, "Notice")).Contains("已恢复"), "healthy measurement after failure announces recovery");
        update.Invoke(motion, new object[] {-1, Color.Gray, 3.0, 6000.0, "AI节点"});
        Check(!((string)Field(motion, "Notice")).Contains("已恢复"), "stale data is never announced as recovered");

        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        var fixture = (Dictionary<string, object>)Field(hud, "data");
        fixture["updated_at_ms"] = now;
        var nodes = new[] {Node("ai", 1800, now), Node("general", 100, now)};
        nodes[0]["state"] = "timeout"; nodes[0]["delay_ms"] = null;
        fixture["network_latency"] = new Dictionary<string, object> {{"state", "partial"}, {"updated_at_ms", now}, {"nodes", nodes}};
        var metrics = Json.Object(fixture, "metrics");
        metrics["turn_active"] = true; metrics["stage"] = "generating"; metrics["last_event_at_ms"] = now - 150000;
        Call(hud, "UpdateMetrics");
        Set(hud, "motionAllowed", false);
        Set(hud, "nextMotionPreference", double.MaxValue);
        SavePreview(hud, Path.Combine(root, "build", "corner-alert.png"));
        Check((int)Field(Field(hud, "aiMotion"), "Level") == 4, "real timeout telemetry drives the visual alert");
        Set(hud, "motionAllowed", true);
        StressDrawing(hud);
        Set(hud, "motionAllowed", false);
        nodes[0]["state"] = "ok"; nodes[0]["delay_ms"] = 120;
        SavePreview(hud, Path.Combine(root, "build", "corner-recovered.png"));
        Check(((string)Field(Field(hud, "aiMotion"), "Notice")).Contains("已恢复"),
            "real recovery is reflected in the compact notification line");
    }

    static Dictionary<string, object> Node(string id, int delay, double now)
    {
        return new Dictionary<string, object> {{"id", id}, {"state", "ok"}, {"delay_ms", delay}, {"measured_at_ms", now},
            {"history", Enumerable.Range(0, 12).Select(i => new Dictionary<string, object> {
                {"at_ms", now - (11 - i) * 30000}, {"state", "ok"}, {"delay_ms", delay + (i % 4) * 34}}).ToArray()}};
    }
    static void SavePreview(Hud hud, string path)
    {
        using (var bitmap = new Bitmap(hud.Width, hud.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(hud.BackColor);
            Call(hud, "OnPaint", new PaintEventArgs(graphics, new Rectangle(Point.Empty, hud.Size)));
            bitmap.Save(path);
        }
    }
}

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
            Check(hud.Width == (int)Math.Round(400 * scale) && hud.Height == (int)Math.Round(536 * scale),
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
            // Rendering checks can run while the user works in another foreground app.
            // The default suite still requires all interactive tray checks.
            if (args.Length < 3 || args[2] != "--render-only")
            {
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
            }
            else Console.WriteLine("SKIP interactive tray checks (--render-only)");
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
                    {"response_duration", ResponseFixture(now)},
                    {"turn_active", true}, {"last_event_at_ms", now - 12000},
                    {"last", new Dictionary<string, object> {{"model", "gpt-6.1-sol"}, {"measured_at_ms", now}, {"rate", 87.4}, {"cache_percent", 98.8}}}}} });
            Call(hud, "UpdateView", new object[] { null });
            Call(hud, "UpdateMetrics");
            StressDrawing(hud);
            CheckResponsePreviews(hud, root);
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
                now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                fixture["updated_at_ms"] = now;
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
            foreach (string prediction in new[] {"deadline", "estimated"})
            {
                radar["upcoming"] = new Dictionary<string, object> {{"id", "demo-prediction"}, {"kind", "reset_scheduled"},
                    {"start_ms", now + 7200000}, {"end_ms", now + 7200000}, {"precision", prediction}};
                Call(hud, "UpdateMetrics");
                SavePreview(hud, Path.Combine(root, "build", "corner-reset-" + prediction + ".png"));
                Check(((ResetRadarDisplay)Field(hud, "resetRadar")).CalendarTime,
                    prediction + " renders the publisher's absolute date in the actual card");
            }
            double nextYear = (new DateTime(DateTime.Now.Year + 1, 1, 1, 2, 0, 0, DateTimeKind.Local).ToUniversalTime() -
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            radar["upcoming"] = new Dictionary<string, object> {{"id", "demo-year"}, {"precision", "deadline"},
                {"start_ms", nextYear}, {"end_ms", nextYear}};
            Call(hud, "UpdateMetrics");
            SavePreview(hud, Path.Combine(root, "build", "corner-reset-deadline-next-year.png"));
            radar["upcoming"] = null;
            radar["latest_events"] = new object[] {new Dictionary<string, object> {
                {"id", "demo-completed"}, {"kind", "reset_completed"}, {"reset_type", "global"},
                {"at_ms", now - 480000}, {"time_basis", "announced"}}};
            Call(hud, "UpdateMetrics");
            SavePreview(hud, Path.Combine(root, "build", "corner-reset-completed.png"));
            Check(((ResetRadarDisplay)Field(hud, "resetRadar")).Countdown == "已重置",
                "recent public completion is presented in the actual corner card");
            radar["latest_events"] = new object[0];
            quota["windows"] = new object[] {new Dictionary<string, object> {{"window_minutes", 10080}, {"remaining_percent", 59}, {"resets_at", now / 1000 - 1}}};
            Call(hud, "UpdateMetrics");
            Check(((List<Dictionary<string, object>>)Field(hud, "quotaWindows")).Count == 0 &&
                ((string)Field(hud, "quotaNotice")).Contains("等待同步"), "passed reset hides the old balance without guessing a new percentage");
            CheckDiagnostics(temporary);
            CheckMotion(hud, root);
            CheckRadarMotion(hud, root);
            CheckMotionPreferences(hud, config);
            SaveDesignAnimation(hud, root);
            Console.WriteLine("Corner HUD checks passed: " + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (hud != null) { hud.Close(); hud.Dispose(); } }
    }

    static void CheckMotionPreferences(Hud hud, RuntimeConfiguration config)
    {
        var resolve = typeof(Hud).GetMethod("ResolveMotion", BindingFlags.NonPublic | BindingFlags.Static);
        Check((bool)resolve.Invoke(null, new object[] {"on", false}),
            "explicit full motion remains on even when Windows client-area animation is disabled");
        Check(!(bool)resolve.Invoke(null, new object[] {"off", true}), "explicit static mode overrides Windows animation");
        Check(!(bool)resolve.Invoke(null, new object[] {"system", false}) &&
              (bool)resolve.Invoke(null, new object[] {"system", true}), "system mode respects both Windows preference states");
        Call(hud, "BuildMenu");
        var menu = (ContextMenuStrip)Field(hud, "menu");
        var choices = menu.Items.Cast<ToolStripItem>().OfType<ToolStripMenuItem>().Single(i => i.Text == "面板动效");
        choices.DropDownItems.Cast<ToolStripItem>().Single(i => i.Text == "完整动效").PerformClick();
        Check((bool)Field(hud, "motionAllowed") && config.CornerMotion == "on" &&
              Json.Text(Json.Read(Path.Combine(config.DataDirectory, "settings.json")), "corner_motion") == "on",
              "full-motion tray command takes effect immediately and persists for restart");
        choices.DropDownItems.Cast<ToolStripItem>().Single(i => i.Text == "静态显示").PerformClick();
        Check(!(bool)Field(hud, "motionAllowed") && config.CornerMotion == "off", "static tray command stops decorative motion");
        choices.DropDownItems.Cast<ToolStripItem>().Single(i => i.Text == "跟随系统").PerformClick();
        Check((bool)Field(hud, "motionAllowed") == (bool)Field(hud, "systemMotionAllowed") && config.CornerMotion == "system",
              "system tray command restores the current Windows preference");
    }

    // Render the shipping painter, with fixture data and a deterministic visual clock.
    // These frames are documentation artifacts; no runtime notification is emitted.
    static void SaveDesignAnimation(Hud hud, string root)
    {
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        var fixture = (Dictionary<string, object>)Field(hud, "data");
        fixture["updated_at_ms"] = now;
        Json.Object(fixture, "account_quota")["windows"] = new object[] {new Dictionary<string, object> {
            {"window_minutes", 10080}, {"remaining_percent", 59}, {"resets_at", now / 1000 + 86400 * 6}}};
        fixture["network_latency"] = new Dictionary<string, object> {{"state", "ok"}, {"updated_at_ms", now},
            {"nodes", new object[] {Node("ai", 223, now), Node("general", 66, now)}}};
        fixture["reset_radar"] = new Dictionary<string, object> {{"state", "ok"}, {"fetched_at_ms", now}, {"source_updated_at_ms", now},
            {"upcoming", new Dictionary<string, object> {{"id", "design-preview"}, {"precision", "deadline"},
                {"start_ms", now + 7200000}, {"end_ms", now + 7200000}}}};
        var metrics = Json.Object(fixture, "metrics");
        metrics["turn_active"] = true; metrics["stage"] = "generating"; metrics["last_event_at_ms"] = now - 12000;
        var last = Json.Object(metrics, "last");
        last["measured_at_ms"] = now;
        double clock = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Set(hud, "previewMotionClock", clock);
        Set(hud, "motionAllowed", true);
        Set(hud, "nextMotionPreference", double.MaxValue);
        Call(hud, "UpdateMetrics");
        var frames = Path.Combine(root, "build", "design21-frames");
        Directory.CreateDirectory(frames);
        try
        {
            for (int i = 0; i < 75; i++)
            {
                Set(hud, "previewMotionClock", clock + i * 80);
                SavePreview(hud, Path.Combine(frames, i.ToString("000") + ".png"));
            }
            // Normal operation must visibly move even with no reset announcement or active response.
            Json.Object(fixture, "reset_radar")["upcoming"] = null;
            metrics["turn_active"] = false;
            Call(hud, "UpdateMetrics");
            Set(hud, "previewMotionClock", clock + 8000);
            SavePreview(hud, Path.Combine(root, "build", "motion21-idle-a.png"));
            Set(hud, "previewMotionClock", clock + 9500);
            SavePreview(hud, Path.Combine(root, "build", "motion21-idle-b.png"));
            using (var a = new Bitmap(Path.Combine(root, "build", "motion21-idle-a.png")))
            using (var b = new Bitmap(Path.Combine(root, "build", "motion21-idle-b.png")))
            {
                int changed = 0;
                for (int y = 0; y < a.Height; y += 2)
                    for (int x = 0; x < a.Width; x += 2)
                    {
                        var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                        if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 6) changed++;
                    }
                Check(changed > 1000, "idle material and scanning create clearly different native frames without inventing a radar signal");
                Check(!(bool)Field(Field(hud, "radarMotion"), "Active"), "idle scanning does not create a reset signal");
            }
            Set(hud, "motionAllowed", false);
            Json.Object(fixture, "network_latency")["updated_at_ms"] =
                (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds + 1000;
            Set(hud, "previewMotionClock", clock + 10000);
            SavePreview(hud, Path.Combine(root, "build", "motion21-static-a.png"));
            Set(hud, "previewMotionClock", clock + 11500);
            SavePreview(hud, Path.Combine(root, "build", "motion21-static-b.png"));
            Check(File.ReadAllBytes(Path.Combine(root, "build", "motion21-static-a.png")).SequenceEqual(
                File.ReadAllBytes(Path.Combine(root, "build", "motion21-static-b.png"))),
                "static mode produces identical frames as the visual clock advances");
        }
        finally { Set(hud, "previewMotionClock", null); }
        Check((int)Field(Field(hud, "aiMotion"), "Level") == 0 &&
              (int)Field(Field(hud, "generalMotion"), "Level") == 0,
              "both independent route cards render healthy measurements after AI group migration");
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
                // The production collector sends heartbeats while the painter runs.
                // Keep that condition in the stress fixture even on slower machines.
                if (i % 100 == 0)
                    ((Dictionary<string, object>)Field(hud, "data"))["updated_at_ms"] =
                        (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
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

    static void CheckRadarMotion(Hud hud, string root)
    {
        var motion = Field(hud, "radarMotion");
        var update = motion.GetType().GetMethod("Update", Private);
        var tone = motion.GetType().GetMethod("Tone", Private);
        var burst = motion.GetType().GetMethod("Bursting", Private);
        var view = new ResetRadarDisplay {State = "deadline", PredictionKind = "deadline", UpcomingId = "signal-one",
            UpcomingStartMs = 10000, UpcomingEndMs = 20000};
        update.Invoke(motion, new object[] {view, 1000.0});
        Check((bool)Field(motion, "Active") && (bool)burst.Invoke(motion, new object[] {1000.0}),
            "any valid deadline starts visual attention without waiting for the near-time threshold");
        update.Invoke(motion, new object[] {view, 2000.0});
        Check((double)Field(motion, "ChangedAt") == 1000, "unchanged radar polls do not restart the arrival effect");
        Check(!(bool)burst.Invoke(motion, new object[] {14000.0}) && (bool)Field(motion, "Active"),
            "arrival effect settles after twelve seconds while persistent signal highlighting remains");
        view.UpcomingEndMs = 25000;
        update.Invoke(motion, new object[] {view, 3000.0});
        Check((string)Field(motion, "Notice") == "预告时间已更新", "changed deadline triggers a distinct visual update");
        view.Alert = true;
        update.Invoke(motion, new object[] {view, 4000.0});
        Check((bool)Field(motion, "Urgent") && ((Color)tone.Invoke(motion, new object[] {4000.0})).ToArgb() == Color.FromArgb(255, 164, 106).ToArgb(),
            "near forecast strengthens the signal with an orange attention color");
        view.State = "unavailable";
        update.Invoke(motion, new object[] {view, 5000.0});
        Check(!(bool)Field(motion, "Active"), "offline radar cannot keep highlighting stale predictions as active signals");
        view.State = "deadline"; view.Alert = false;
        update.Invoke(motion, new object[] {view, 6000.0});
        Check((double)Field(motion, "ChangedAt") == 4000, "recovery of the same forecast does not announce a new event");
        view.State = "completed"; view.CompletionEventId = "completed-new"; view.Countdown = "已重置";
        update.Invoke(motion, new object[] {view, 7000.0});
        Check((bool)Field(motion, "Confirmed") && ((Color)tone.Invoke(motion, new object[] {7000.0})).ToArgb() == Color.FromArgb(115, 230, 195).ToArgb(),
            "public completion receives its distinct mint signal");

        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        var fixture = (Dictionary<string, object>)Field(hud, "data");
        fixture["updated_at_ms"] = now;
        var radar = new Dictionary<string, object> {{"state", "ok"}, {"fetched_at_ms", now}, {"source_updated_at_ms", now},
            {"upcoming", new Dictionary<string, object> {{"id", "visual-preview"}, {"precision", "deadline"},
                {"start_ms", now + 7200000}, {"end_ms", now + 7200000}}}};
        fixture["reset_radar"] = radar;
        Call(hud, "UpdateMetrics");
        Set(hud, "motionAllowed", true);
        Set(hud, "nextMotionPreference", double.MaxValue);
        SavePreview(hud, Path.Combine(root, "build", "corner-radar-new-signal.png"));
        motion.GetType().GetField("ChangedAt", Private).SetValue(motion, -100000.0);
        SavePreview(hud, Path.Combine(root, "build", "corner-radar-persistent.png"));
        Set(hud, "motionAllowed", false);
        SavePreview(hud, Path.Combine(root, "build", "corner-radar-reduced-motion.png"));
        Check((bool)Field(motion, "Active"), "reduced-motion display retains the signal through static highlighting");
        radar["upcoming"] = null;
        radar["latest_events"] = new object[] {new Dictionary<string, object> {{"id", "preview-confirmed"},
            {"kind", "reset_completed"}, {"reset_type", "global"}, {"at_ms", now - 1000}, {"time_basis", "completed"}}};
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-radar-confirmed.png"));
        Check((bool)Field(motion, "Confirmed"), "rendered confirmed state remains separate from a deadline prediction");
        Set(hud, "motionAllowed", true);
        StressDrawing(hud);
    }

    static void CheckMotion(Hud hud, string root)
    {
        var response = Field(hud, "responseMotion");
        var responseUpdate = response.GetType().GetMethod("Update", Private);
        var responseBlend = response.GetType().GetMethod("Blend", Private);
        var view = new ActivityDisplay {SessionId = "test-one", LastResponseAt = 1};
        view.ResponseMs.AddRange(new double[] {10000, 20000});
        responseUpdate.Invoke(response, new object[] {view, 1000.0, true});
        Check((double)Field(response, "ChangedAt") < 0, "first history load does not announce a new response");
        view.LastResponseAt = 2; view.ResponseMs.Add(15000);
        responseUpdate.Invoke(response, new object[] {view, 2000.0, true});
        Check((double)Field(response, "ChangedAt") == 2000 &&
            (double)responseBlend.Invoke(response, new object[] {2000.0, true}) == 0 &&
            (double)responseBlend.Invoke(response, new object[] {2900.0, true}) == 1,
            "new response morph starts once and settles in 900 milliseconds");
        responseUpdate.Invoke(response, new object[] {view, 2500.0, true});
        Check((double)Field(response, "ChangedAt") == 2000, "unchanged telemetry does not restart the curve reveal");
        Check((double)responseBlend.Invoke(response, new object[] {2100.0, false}) == 1, "reduced motion displays the exact curve immediately");
        view.SessionId = "test-two";
        responseUpdate.Invoke(response, new object[] {view, 3000.0, true});
        Check((double)Field(response, "ChangedAt") < 0 &&
            ((double[])Field(response, "From")).SequenceEqual(view.ResponseMs), "switching conversations resets curve without blending unrelated data");
        view.ResponseMs.Clear();
        responseUpdate.Invoke(response, new object[] {view, 4000.0, true});
        Check(((double[])Field(response, "Target")).Length == 0, "missing history clears all chart points");
        var motion = Field(hud, "aiMotion");
        var update = motion.GetType().GetMethod("Update", Private);
        var color = motion.GetType().GetMethod("ReadColor", Private);
        update.Invoke(motion, new object[] {0, Color.Aquamarine, 1.0, 1000.0, "AI服务"});
        update.Invoke(motion, new object[] {4, Color.Red, 2.0, 2000.0, "AI服务"});
        Check((string)Field(motion, "Notice") == "AI服务 · 探测超时", "timeout transitions announce a route-specific failure");
        update.Invoke(motion, new object[] {4, Color.Red, 2.0, 4000.0, "AI服务"});
        Check((double)Field(motion, "ChangedAt") == 2000, "unchanged failure does not restart the notification");
        Check(((Color)color.Invoke(motion, new object[] {2000.0, false})).ToArgb() == Color.Red.ToArgb(),
            "reduced motion immediately uses the accurate severity color");
        update.Invoke(motion, new object[] {0, Color.Aquamarine, 3.0, 5000.0, "AI服务"});
        Check(((string)Field(motion, "Notice")).Contains("已恢复"), "healthy measurement after failure announces recovery");
        update.Invoke(motion, new object[] {-1, Color.Gray, 3.0, 6000.0, "AI服务"});
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

    static void CheckResponsePreviews(Hud hud, string root)
    {
        var original = (ActivityDisplay)Field(hud, "activity");
        var sample = new ActivityDisplay {SessionId = "preview", Active = true, State = "generating", Title = "正在生成",
            ElapsedText = "00:12", EventAgeMs = 12000, AverageText = "15.0s", AverageMs = 15000, CompletedCount = 20, LastResponseAt = 1};
        sample.ResponseMs.AddRange(original.ResponseMs);
        Set(hud, "activity", sample);
        SavePreview(hud, Path.Combine(root, "build", "corner-response-trend.png"));
        var motion = Field(hud, "responseMotion");
        var update = motion.GetType().GetMethod("Update", Private);
        double time = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        sample.ResponseMs.RemoveAt(0); sample.ResponseMs.Add(45000); sample.LastResponseAt = 2;
        sample.AverageMs = sample.ResponseMs.Average(); sample.AverageText = (sample.AverageMs.Value / 1000).ToString("0.0") + "s";
        update.Invoke(motion, new object[] {sample, time, true});
        foreach (int offset in new[] {0, 450, 900, 1500})
        {
            motion.GetType().GetField("ChangedAt", Private).SetValue(motion,
                System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency - offset);
            SavePreview(hud, Path.Combine(root, "build", "corner-response-transition-" + offset + ".png"));
        }
        sample.ResponseMs.Clear(); sample.ResponseMs.Add(12500); sample.AverageMs = 12500;
        sample.AverageText = "12.5s"; sample.CompletedCount = 1; sample.LastResponseAt = 3;
        SavePreview(hud, Path.Combine(root, "build", "corner-response-single.png"));
        sample.ResponseMs.Clear(); sample.AverageMs = null; sample.AverageText = "--:--"; sample.CompletedCount = 0;
        SavePreview(hud, Path.Combine(root, "build", "corner-response-empty.png"));
        Check(((double[])Field(motion, "Target")).Length == 0, "empty response chart clears previous session visuals");
        sample.ResponseMs.Add(7200000); sample.AverageMs = 7200000; sample.AverageText = "02:00:00";
        sample.CompletedCount = 1; sample.LastResponseAt = 4;
        SavePreview(hud, Path.Combine(root, "build", "corner-response-long.png"));
        Set(hud, "activity", original);
    }

    static Dictionary<string, object> ResponseFixture(double now)
    {
        var seconds = new double[] {8, 12, 9, 19, 14, 11, 27, 16, 10, 7, 15, 34, 21, 12, 9, 18, 25, 13, 8, 11};
        return new Dictionary<string, object> {{"mean_ms", seconds.Average() * 1000}, {"count", 20}, {"limit", 20},
            {"samples", seconds.Select((value, i) => new Dictionary<string, object> {
                {"duration_ms", value * 1000}, {"completed_at_ms", now - (20 - i) * 40000}}).ToArray()}};
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

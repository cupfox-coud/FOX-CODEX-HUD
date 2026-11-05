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
#if HUD_TEST
        RenderMemoryGuard.TestSnapshot = new MemorySnapshot { CommitLimit = 100UL << 30,
            CommitAvailable = 20UL << 30, PhysicalAvailable = 8UL << 30 };
#endif
        Hud hud = null;
        try
        {
            var foreground = Native.GetForegroundWindow();
            hud = new Hud(config);
            ((System.Windows.Forms.Timer)Field(hud, "ticker")).Stop();
            ((System.Windows.Forms.Timer)Field(hud, "hoverTicker")).Stop();
            hud.StartMonitoring();
            Check(Json.Text(Json.Read(Path.Combine(config.Runtime, "selection.json")), "binding_mode") == "activity",
                "corner starts following latest local activity without relying on window title recognition");
            Call(hud, "PositionHud");
            double scale = Native.Scale(hud.Handle);
            int margin = (int)Math.Round(18 * scale);
            Check(hud.Right == target.WorkingArea.Right - margin && hud.Top == target.WorkingArea.Top + margin,
                "card is anchored at the secondary monitor top-right in physical coordinates");
            Check(hud.Width == (int)Math.Round(400 * scale) && hud.Height == (int)Math.Round(738 * scale),
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
                    {"live_speed", SpeedFixture(now, 87.4, .7)},
                    {"response_duration", ResponseFixture(now)},
                    {"turn_active", true}, {"last_event_at_ms", now - 12000},
                    {"last", new Dictionary<string, object> {{"model", "gpt-6.1-sol"}, {"measured_at_ms", now}, {"rate", 87.4}, {"cache_percent", 98.8}}}}} });
            Call(hud, "UpdateView", new object[] { null });
            Call(hud, "UpdateMetrics");
            CheckTodayQuota(hud);
            CheckQuotaWeekScale();
            CheckQuotaFutureRail(hud);
            CheckQuotaSpectrum(hud, root);
            CheckQuotaRunway(hud, root);
            CheckTodayQuotaRendering(hud);
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
            radar["upcoming"] = new Dictionary<string, object> {{"id", "demo-plan"}, {"kind", "reset_scheduled"}, {"reset_type", "global"},
                {"start_ms", now + 1420000}, {"end_ms", now + 1420000}, {"precision", "exact"}, {"basis", "explicit"}};
            Call(hud, "UpdateMetrics");
            SavePreview(hud, Path.Combine(root, "build", "corner-reset-scheduled.png"));
            Check(((ResetRadarDisplay)Field(hud, "resetRadar")).Alert, "near scheduled reset is prominent in the small radar card");
            foreach (string prediction in new[] {"deadline", "estimated"})
            {
                radar["upcoming"] = new Dictionary<string, object> {{"id", "demo-prediction"}, {"kind", "reset_scheduled"}, {"reset_type", "global"},
                    {"start_ms", now + 7200000}, {"end_ms", now + 7200000}, {"precision", prediction}};
                Call(hud, "UpdateMetrics");
                SavePreview(hud, Path.Combine(root, "build", "corner-reset-" + prediction + ".png"));
                var forecast = (ResetRadarDisplay)Field(hud, "resetRadar");
                Check(forecast.CountingDown && forecast.Countdown.Contains(":") && forecast.CalendarLabel.Length > 0,
                    prediction + " renders a countdown and the publisher's calendar reference together");
            }
            radar["upcoming"] = new Dictionary<string, object> {{"id", "demo-date-window"}, {"reset_type", "global"}, {"precision", "date"},
                {"start_ms", now - 23 * 3600000}, {"end_ms", now + 3600000}};
            Call(hud, "UpdateMetrics");
            SavePreview(hud, Path.Combine(root, "build", "corner-reset-date-window.png"));
            var dateWindow = (ResetRadarDisplay)Field(hud, "resetRadar");
            Check(dateWindow.CountingDown && dateWindow.CalendarLabel.StartsWith("窗口截至 "),
                "the real API's coarse date-window shape renders a countdown rather than yesterday's date");
            double nextYear = (new DateTime(DateTime.Now.Year + 1, 1, 1, 2, 0, 0, DateTimeKind.Local).ToUniversalTime() -
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            radar["upcoming"] = new Dictionary<string, object> {{"id", "demo-year"}, {"reset_type", "global"}, {"precision", "deadline"},
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
            SaveRealtimeAnimation(hud, root);
#if HUD_TEST
            CheckMemoryProtection(hud, root);
#endif
            Console.WriteLine("Corner HUD checks passed: " + checks);
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

    static Dictionary<string, object> TodayFixture(double now, double remaining)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var day = epoch.AddMilliseconds(now).ToLocalTime().Date;
        double start = (day.ToUniversalTime() - epoch).TotalMilliseconds;
        double end = (day.AddDays(1).ToUniversalTime() - epoch).TotalMilliseconds;
        double allocated = remaining < -90 ? 0 : Math.Max(10, remaining);
        return new Dictionary<string, object> {{"state", remaining < 0 ? "over_budget" : remaining > 0 ? "ok" : "spent"},
            {"remaining_percent", remaining}, {"allocated_percent", allocated}, {"spent_percent", allocated - remaining},
            {"anchor_at_ms", start}, {"day_start_ms", start}, {"day_end_ms", end}, {"valid_until_ms", end}};
    }

    static void CheckTodayQuota(Hud hud)
    {
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        var daily = TodayFixture(now, 4.28);
        var view = TodayQuotaDisplay.Read(daily, 60, false, 1000, now);
        Check(view.Label == "今日建议余量" && view.Text == "4.2%" && view.State == "ok",
            "today's remaining recommendation is a direct quota amount rounded down to a tenth");
        Check(TodayQuotaDisplay.Read(TodayFixture(now, 0.04), 60, false, 0, now).Text == "<0.1%",
            "a small positive daily remainder is not reported as zero");
        Check(TodayQuotaDisplay.Read(TodayFixture(now, 0), 60, false, 0, now).Text == "0%",
            "exactly meeting the daily budget remains zero");
        view = TodayQuotaDisplay.Read(TodayFixture(now, -8.13), 60, false, 0, now);
        Check(view.Text == "−8.1%" && view.OverBudget && view.State == "over_budget" && view.Label == "今日余量·超额",
            "a true deficit retains its minus sign and explicit over-budget label");
        Check(TodayQuotaDisplay.Read(TodayFixture(now, -0.04), 60, false, 0, now).Text == "−<0.1%",
            "a small negative budget is never rounded away as zero");
        Check(TodayQuotaDisplay.Read(TodayFixture(now, -100), 0, false, 0, now).Text == "−100%",
            "negative advice remains valid even when the actual weekly balance is zero");
        view = TodayQuotaDisplay.Read(TodayFixture(now, -8.13), 60, true, 60000, now);
        Check(view.Cached && view.OverBudget && view.Label == "今日超额·暂存" && view.Text == "−8.1%",
            "a cached deficit is qualified without losing its amount");
        Check(TodayQuotaDisplay.Read(daily, 60, true, 60000, now).Label == "今日建议·暂存" &&
            TodayQuotaDisplay.Read(daily, 60, true, 60000, now).Text == "4.2%",
            "recent cached daily advice is explicitly qualified");
        Check(TodayQuotaDisplay.Read(daily, 60, true, 181000, now).Text == "等待同步" &&
            TodayQuotaDisplay.Read(daily, null, false, 0, now).Text == "等待额度",
            "stale and unavailable balances do not supply actionable daily amounts");
        double midnight = Json.Number(daily, "valid_until_ms").Value;
        Check(TodayQuotaDisplay.Read(daily, 60, true, 1000, midnight).Text == "等待同步",
            "yesterday's cached recommendation expires exactly at midnight");
        daily["valid_until_ms"] = now + 1000;
        Check(TodayQuotaDisplay.Read(daily, 60, true, 1000, now + 1000).Text == "等待同步",
            "daily advice expires at an official reset earlier than midnight");
        daily = TodayFixture(now, 4.28);
        foreach (object invalid in new object[] {true, "4.2", double.NaN, double.PositiveInfinity, -101.0, 101.0})
        {
            daily["remaining_percent"] = invalid;
            Check(TodayQuotaDisplay.Read(daily, 60, false, 0, now).Text == "等待同步",
                "malformed daily amount is rejected: " + invalid);
        }
        daily = TodayFixture(now, 4.28);
        Check(TodayQuotaDisplay.Read(daily, 3, false, 0, now).Text == "等待同步",
            "daily advice cannot exceed the actual weekly remainder");
        daily["spent_percent"] = 9;
        Check(TodayQuotaDisplay.Read(daily, 60, false, 0, now).Text == "等待同步",
            "daily allocation, consumption and remainder must be consistent");
        daily = TodayFixture(now, -8.13); daily["state"] = "spent";
        Check(TodayQuotaDisplay.Read(daily, 60, false, 0, now).Text == "等待同步",
            "an inconsistent deficit state cannot silently masquerade as a spent budget");
        using (var surface = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(surface))
        using (var format = StringFormat.GenericTypographic)
        {
            var font = (Font)Field(hud, "cornerMicroFont");
            Check(new[] {"今日建议余量", "今日建议·暂存", "今日余量·超额", "今日超额·暂存"}.All(t => g.MeasureString(t, font, 1000, format).Width <= 104),
                "daily recommendation labels fit the existing compact column");
            font = (Font)Field(hud, "cornerValueFont");
            Check(new[] {"−100%", "−99.9%", "−<0.1%"}.All(t => g.MeasureString(t, font, 1000, format).Width <= 104),
                "negative amounts and the sub-decimal qualifier fit without reducing the type size");
        }
    }

    static Bitmap TodaySurface(Hud hud, double clock)
    {
        var bitmap = new Bitmap(400, 738);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Call(hud, "PaintQuotaSurface", g, clock);
        }
        return bitmap;
    }

    static int TodaySurfaceChanges(Hud hud)
    {
        using (var a = TodaySurface(hud, 1000))
        using (var b = TodaySurface(hud, 2700))
        {
            int changed = 0;
            for (int y = 260; y < 456; y++) for (int x = 20; x < 380; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) changed++;
            return changed;
        }
    }

    static void CheckTodayQuotaRendering(Hud hud)
    {
        var original = Field(hud, "quotaToday");
        var originalScale = Field(hud, "quotaScale");
        bool motion = (bool)Field(hud, "motionAllowed");
        var guard = (RenderMemoryGuard)Field(hud, "renderMemory");
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        var debt = TodayQuotaDisplay.Read(TodayFixture(now, -8.13), 60, false, 0, now);
        try
        {
            Set(hud, "quotaToday", debt); Set(hud, "motionAllowed", true);
            Check(TodaySurfaceChanges(hud) > 300, "over-budget light moves visibly while the number remains fixed");
            using (var frame = TodaySurface(hud, 1000))
                Check(frame.GetPixel(32, 432).GetBrightness() < .12f, "the warning keeps a dark obsidian surface");
            Set(hud, "motionAllowed", false);
            Check(TodaySurfaceChanges(hud) == 0, "reduced motion keeps the warning visible without animated pixels");
            Set(hud, "motionAllowed", true);
            debt.Cached = true;
            Check(TodaySurfaceChanges(hud) == 0, "cached over-budget advice does not emit a live animated warning");
            debt.Cached = false;
            guard.Sample(new MemorySnapshot {CommitLimit = 100UL << 30, CommitAvailable = 1UL << 30}, 1);
            Check(TodaySurfaceChanges(hud) == 0, "memory protection disables deficit effects even with animation enabled");
            var healthy = new MemorySnapshot {CommitLimit = 100UL << 30, CommitAvailable = 20UL << 30, PhysicalAvailable = 8UL << 30};
            guard.Sample(healthy, 2); guard.Sample(healthy, 32002);
            Set(hud, "quotaScale", new QuotaWeekScale {Available = true, Projected = true, Current = 2.0 / 7, End = 5.0 / 7,
                EndLabel = "约11/02 17时用尽", ResetText = "11/04 17:27 重置", State = "estimated"});
            StressDrawing(hud);
        }
        finally { Set(hud, "quotaToday", original); Set(hud, "quotaScale", originalScale); Set(hud, "motionAllowed", motion); }
    }

    static void CheckQuotaWeekScale()
    {
        const double day = 86400000;
        double now = (new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc) - new DateTime(1970, 1, 1)).TotalMilliseconds;
        var window = new Dictionary<string, object> {{"window_minutes", 10080}, {"resets_at", (now + 5 * day) / 1000}};
        var view = new QuotaRunwayDisplay {State = "estimated", ForecastState = "estimated", HasTimeline = true, Hours = 70, GapHours = 50};
        var scale = QuotaWeekScale.Read(window, view, now, now, now, false);
        Check(scale.Available && scale.Projected && Math.Abs(scale.Current - 2.0 / 7) < 1e-8 && Math.Abs(scale.End - 118.0 / 168) < 1e-8,
            "seven-day ruler places now and predicted depletion on the actual weekly cycle");
        Check(Math.Abs(scale.FutureCoverage - 70.0 / 120) < 1e-8,
            "future-only coverage excludes the 48 elapsed hours and spans now to reset");
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Check(scale.Ticks.Count >= 4 && scale.Ticks.Count <= 6 && scale.Ticks.All(t => t.Fraction > 0 && t.Fraction < 1) &&
            scale.Ticks.Any(t => t.Label == "11/01") && scale.Ticks.All(t => {
                var date = epoch.AddMilliseconds(now + t.Fraction * 5 * day).ToLocalTime();
                return date.Hour == 0 && date.Minute == 0 && t.Label == date.ToString("MM/dd");
            }), "calendar ticks cross month boundaries and represent actual local midnights at their true positions");
        var shortWindow = new Dictionary<string, object> {{"window_minutes", 10080}, {"resets_at", (now + 6 * 3600000) / 1000}};
        var shortScale = QuotaWeekScale.Read(shortWindow, view, now, now, now, false);
        Check(shortScale.Ticks.Count >= 2 && shortScale.Ticks.Count <= 3 && shortScale.Ticks.All(t => {
                var date = epoch.AddMilliseconds(now + t.Fraction * 6 * 3600000).ToLocalTime();
                return date.Minute == 0 && t.Label == date.ToString("HH:mm");
            }), "short reset windows use real clock ticks instead of a misleading day scale");
        double december = (new DateTime(2026, 12, 30, 12, 0, 0, DateTimeKind.Utc) - epoch).TotalMilliseconds;
        var newYearWindow = new Dictionary<string, object> {{"window_minutes", 10080}, {"resets_at", (december + 5 * day) / 1000}};
        var newYearScale = QuotaWeekScale.Read(newYearWindow, view, december, december, december, false);
        Check(newYearScale.Ticks.Any(t => t.Label == "01/01") && newYearScale.Ticks.Zip(newYearScale.Ticks.Skip(1), (a, b) => a.Fraction < b.Fraction).All(increasing => increasing),
            "year rollover keeps chronological tick placement and readable dates");
        var later = QuotaWeekScale.Read(window, view, now, now + 600000, now + 600000, false);
        Check(later.Current > scale.Current && later.End == scale.End && later.EndLabel == scale.EndLabel,
            "a held forecast keeps its calendar endpoint fixed while the present advances");
        Check(scale.EndLabel.StartsWith("约") && scale.EndLabel.EndsWith("用尽") && scale.ResetText.EndsWith("重置"),
            "estimated depletion and the official reset retain distinct explicit labels");
        Check(!QuotaWeekScale.Read(window, view, null, now, now, false).Projected &&
            !QuotaWeekScale.Read(window, view, now - 15 * 60000, now, now, false).Projected &&
            !QuotaWeekScale.Read(window, view, now + 60000, now, now, false).Projected,
            "missing, stale and future forecast anchors cannot invent a calendar endpoint");
        scale = QuotaWeekScale.Read(window, view, now, now - 120000, now, true);
        Check(scale.Cached && scale.Current < 2.0 / 7 && scale.Projected,
            "cached ruler qualifies and places the last snapshot rather than pretending it is live");
        view.State = "stale";
        Check(!QuotaWeekScale.Read(window, view, now, now, now, true).Projected,
            "expired account data cannot project future coverage");
        view.State = "estimated"; view.Hours = 0;
        scale = QuotaWeekScale.Read(window, view, now, now, now, false);
        Check(scale.Projected && scale.End == scale.Current && scale.EndLabel == "不足15分内用尽",
            "a sub-quarter-hour prediction stays an interval without a fabricated exact time");
        view.Hours = 1.75; view.GapHours = 0; window["resets_at"] = (now + 1.64 * 3600000) / 1000;
        scale = QuotaWeekScale.Read(window, view, now, now, now, false);
        Check(scale.Projected && scale.End == 1 && scale.EndLabel == "临近重置用尽" && !scale.ThroughReset,
            "rounded near-reset exhaustion never moves beyond the official cycle boundary");
        view.ForecastState = view.State = "through_reset";
        scale = QuotaWeekScale.Read(window, view, now, now, now, false);
        Check(scale.ThroughReset && scale.End == 1 && scale.Projected,
            "sufficient coverage ends at the reset without claiming an exact exhaustion date");
        Check(scale.FutureCoverage == 1, "sufficient future coverage fills the complete rail");
        view.ForecastState = view.State = "exhausted";
        scale = QuotaWeekScale.Read(window, view, now, now, now, false);
        Check(scale.Exhausted && scale.End == scale.Current && !scale.Projected,
            "actual exhaustion marks the present instead of a future forecast");
        Check(scale.FutureCoverage == 0, "actual exhaustion leaves the entire future interval as a gap");
        window["resets_at"] = now / 1000;
        Check(!QuotaWeekScale.Read(window, view, now, now, now, false).Available,
            "a passed weekly reset cannot draw an obsolete cycle");
        window["resets_at"] = (now + 8 * day) / 1000;
        Check(!QuotaWeekScale.Read(window, view, now, now, now, false).Available,
            "a reset outside a seven-day window is rejected");

    }

    static Bitmap FutureRailFrame(Hud hud, double clock)
    {
        Set(hud, "previewMotionClock", clock);
        var frame = new Bitmap(400, 738);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(17, 20, 26));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Call(hud, "PaintQuotaRuler", g);
        }
        return frame;
    }

    static int FutureRailChanges(Hud hud, int from, int to)
    {
        using (var a = FutureRailFrame(hud, 400))
        using (var b = FutureRailFrame(hud, 2600))
        {
            int count = 0;
            for (int y = 363; y < 377; y++) for (int x = from; x < to; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) count++;
            return count;
        }
    }

    static void CheckQuotaFutureRail(Hud hud)
    {
        var oldScale = Field(hud, "quotaScale"); var oldView = Field(hud, "quotaRunway");
        var oldClock = Field(hud, "previewMotionClock"); bool oldMotion = (bool)Field(hud, "motionAllowed");
        var guard = (RenderMemoryGuard)Field(hud, "renderMemory");
        var healthy = new MemorySnapshot {CommitLimit = 100UL << 30, CommitAvailable = 20UL << 30, PhysicalAvailable = 8UL << 30};
        var scale = new QuotaWeekScale {Available = true, Projected = true, Current = 2.0 / 7, End = 118.0 / 168,
            EndLabel = "约11/02 17时用尽", ResetText = "11/04 17:27 重置", State = "estimated"};
        var view = new QuotaRunwayDisplay {State = "estimated", ForecastState = "estimated", HasTimeline = true, Hours = 70, GapHours = 50};
        try
        {
            Set(hud, "quotaScale", scale); Set(hud, "quotaRunway", view); Set(hud, "motionAllowed", true);
            int split = 36 + (int)(328 * scale.FutureCoverage);
            Check(FutureRailChanges(hud, 36, split - 2) == 0 && FutureRailChanges(hud, split + 2, 364) > 100,
                "only the shortage segment moves; usable time stays visually stable");
            Check((string)Call(hud, "QuotaGapCaption") == "预计空窗 2天2小时",
                "the shortage is directly named and quantified independently of color");
            Set(hud, "motionAllowed", false);
            Check(FutureRailChanges(hud, 36, 364) == 0, "static preference preserves distinct segments without animated pixels");
            Set(hud, "motionAllowed", true); scale.Cached = true;
            Check(FutureRailChanges(hud, 36, 364) == 0 && ((string)Call(hud, "QuotaGapCaption")).StartsWith("上次空窗"),
                "cached shortage is explicitly qualified and never emits a live warning sweep");
            scale.Cached = false;
            guard.Sample(new MemorySnapshot {CommitLimit = 100UL << 30, CommitAvailable = 1UL << 30}, 1);
            Check(FutureRailChanges(hud, 36, 364) == 0, "memory pressure stops shortage animation even when motion is enabled");
            guard.Sample(healthy, 2); guard.Sample(healthy, 32002);
            scale.ThroughReset = true; scale.End = 1;
            Check(FutureRailChanges(hud, 36, 364) == 0 && (string)Call(hud, "QuotaGapCaption") == "预计无空窗",
                "sufficient coverage has an affirmative label without warning motion");
        }
        finally
        {
            guard.Sample(healthy, 40000); guard.Sample(healthy, 80000);
            Set(hud, "quotaScale", oldScale); Set(hud, "quotaRunway", oldView);
            Set(hud, "previewMotionClock", oldClock); Set(hud, "motionAllowed", oldMotion);
        }
    }

    static Bitmap QuotaEffectFrame(Hud hud, double clock)
    {
        Set(hud, "previewMotionClock", clock);
        var frame = new Bitmap(400, 738);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(17, 20, 26));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Call(hud, "PaintQuotaSurface", g, clock); Call(hud, "PaintCornerQuota", g);
            Call(hud, "PaintQuotaRuler", g); Call(hud, "PaintQuotaRadarBridge", g, clock);
        }
        return frame;
    }

    static int ImageChanges(Bitmap a, Bitmap b, Rectangle region)
    {
        int changed = 0;
        for (int y = region.Top; y < region.Bottom; y++) for (int x = region.Left; x < region.Right; x++)
            if (a.GetPixel(x, y) != b.GetPixel(x, y)) changed++;
        return changed;
    }

    static int QuotaEffectChanges(Hud hud, double first, double second, Rectangle region)
    {
        using (var a = QuotaEffectFrame(hud, first)) using (var b = QuotaEffectFrame(hud, second))
            return ImageChanges(a, b, region);
    }

    static void CheckQuotaSpectrum(Hud hud, string root)
    {
        var original = Field(hud, "data"); var oldClock = Field(hud, "previewMotionClock");
        var oldPreference = Field(hud, "nextMotionPreference"); bool oldMotion = (bool)Field(hud, "motionAllowed");
        var guard = (RenderMemoryGuard)Field(hud, "renderMemory");
        var healthy = new MemorySnapshot {CommitLimit = 100UL << 30, CommitAvailable = 20UL << 30, PhysicalAvailable = 8UL << 30};
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        var data = new Dictionary<string, object>((Dictionary<string, object>)original);
        var weekly = new Dictionary<string, object> {{"window_minutes", 10080}, {"remaining_percent", 58.0}, {"resets_at", (now + 5 * 86400000) / 1000}};
        var quota = new Dictionary<string, object> {{"state", "ok"}, {"updated_at_ms", now}, {"windows", new object[] {weekly}},
            {"runway", new Dictionary<string, object> {{"state", "estimated"}, {"method", "weekly_cycle_v3"}, {"basis", "cycle"},
                {"span_hours", 48}, {"hours", 70}, {"reset_hours", 120}, {"runway_fraction", 70.0 / 120}, {"gap_hours", 50},
                {"rate_pct_per_day", 20.3}, {"allowance_pct_per_day", 11.8}, {"calculated_at_ms", now}, {"today", TodayFixture(now, -8.13)}}}};
        var radar = new Dictionary<string, object> {{"state", "ok"}, {"fetched_at_ms", now}, {"source_updated_at_ms", now}, {"upcoming", null}};
        var upcoming = new Dictionary<string, object> {{"id", "quota-spectrum-preview"}, {"reset_type", "global"}, {"precision", "deadline"},
            {"start_ms", now + 7200000}, {"end_ms", now + 7200000}};
        data["updated_at_ms"] = now; data["account_quota"] = quota; data["reset_radar"] = radar;
        try
        {
            Set(hud, "data", data); Set(hud, "motionAllowed", true); Set(hud, "nextMotionPreference", double.MaxValue);
            Set(hud, "previewMotionClock", 1000.0); Call(hud, "UpdateMetrics");
            Check((string)Call(hud, "QuotaResetLink") == "none", "everyday rainbow never fabricates a reset signal");
            using (var empty = new Bitmap(200, 350))
            using (var quarter = new Bitmap(200, 350))
            using (var full = new Bitmap(200, 350))
            {
                using (var g = Graphics.FromImage(empty)) Call(hud, "PaintQuotaBattery", g, 0.0);
                using (var g = Graphics.FromImage(quarter)) Call(hud, "PaintQuotaBattery", g, 25.0);
                using (var g = Graphics.FromImage(full)) Call(hud, "PaintQuotaBattery", g, 100.0);
                Check(quarter.GetPixel(68, 337) == full.GetPixel(68, 337) &&
                    quarter.GetPixel(77, 337) == empty.GetPixel(77, 337) &&
                    full.GetPixel(170, 337) != quarter.GetPixel(170, 337),
                    "battery charge occupies exactly the first quarter at 25 percent independently of forecast coverage");
            }
            using (var a = new Bitmap(400, 350)) using (var b = new Bitmap(400, 350))
            {
                var font = (Font)Field(hud, "cornerHeroFont");
                ((ReadoutTransition)Field(hud, "quotaDigits")).Set("58", "weekly", 700, false);
                using (var g = Graphics.FromImage(a)) { g.Clear(Color.Transparent); Set(hud, "previewMotionClock", 1000.0); Call(hud, "PaintQuotaNumber", g, "58", font, 58.0, true); }
                using (var g = Graphics.FromImage(b)) { g.Clear(Color.Transparent); Set(hud, "previewMotionClock", 5000.0); Call(hud, "PaintQuotaNumber", g, "58", font, 58.0, true); }
                int maskChanges = 0;
                for (int y = 295; y < 350; y++) for (int x = 30; x < 198; x++)
                    if (a.GetPixel(x, y).A != b.GetPixel(x, y).A) maskChanges++;
                Check(ImageChanges(a, b, new Rectangle(30, 295, 168, 55)) > 100 && maskChanges == 0,
                    "rainbow flows through fixed glyphs without moving or interpolating the percentage");
            }
            Set(hud, "previewMotionClock", 1000.0); SavePreview(hud, Path.Combine(root, "build", "corner-spectrum-normal.png"));
            // Drive the actual painter from distinct measured headline rates.
            var speedMetrics = new Dictionary<string, object>(Json.Object(data, "metrics"));
            data["metrics"] = speedMetrics; speedMetrics["stage"] = "generating"; speedMetrics["turn_active"] = true;
            var speedColors = new List<int>(); var flowRates = new List<double>();
            var samples = new[] {12.0, 48.0, 160.0};
            for (int sample = 0; sample < samples.Length; sample++)
            {
                double current = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                data["updated_at_ms"] = current;
                var speedFrame = SpeedFixture(current, samples[sample], 0);
                speedFrame["steady_rate"] = speedFrame["recent_rate"] = speedFrame["last_response_rate"] = samples[sample];
                speedFrame["peak_rate"] = Math.Max(138, samples[sample]);
                speedMetrics["live_speed"] = speedFrame; Call(hud, "UpdateMetrics");
                using (var surface = new Bitmap(400, 738)) using (var g = Graphics.FromImage(surface))
                    for (int step = 0; step <= 50; step++)
                    {
                        Set(hud, "previewMotionClock", 40000.0 + sample * 4000 + step * 40);
                        Call(hud, "PaintCorner", g);
                    }
                Check(((SpeedDisplay)Field(hud, "speedView")).Rate == samples[sample], "coupled painter keeps the actual headline rate " + samples[sample]);
                speedColors.Add(((Color)Field(hud, "displayedSpeedColor")).ToArgb());
                flowRates.Add(((SpectrumFlow)Field(hud, "quotaFlow")).CyclesPerSecond);
                SavePreview(hud, Path.Combine(root, "build", "corner-speed-" + samples[sample].ToString("0") + ".png"));
            }
            Check(speedColors.Distinct().Count() == 3 && flowRates[0] < flowRates[1] && flowRates[1] < flowRates[2],
                "rendered low, medium and high speed produce distinct instrument colors and increasing rainbow velocity");
            var coupledFrames = Path.Combine(root, "build", "speed-coupled-frames"); Directory.CreateDirectory(coupledFrames);
            for (int i = 0; i < 90; i++)
            {
                double current = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                data["updated_at_ms"] = current;
                double rate = i < 30 ? 12 : i < 60 ? 48 : 160;
                var speedFrame = SpeedFixture(current, rate, 0);
                speedFrame["steady_rate"] = speedFrame["recent_rate"] = speedFrame["last_response_rate"] = rate;
                speedFrame["peak_rate"] = Math.Max(138, rate);
                speedMetrics["live_speed"] = speedFrame; Call(hud, "UpdateMetrics");
                Set(hud, "previewMotionClock", 60000.0 + i * 120);
                SavePreview(hud, Path.Combine(coupledFrames, i.ToString("000") + ".png"));
            }
            Set(hud, "previewMotionClock", 1000.0);
            radar["upcoming"] = upcoming; Call(hud, "UpdateMetrics");
            Check((string)Call(hud, "QuotaResetLink") == "forecast" && ((string)Call(hud, "QuotaHeading")).Contains("优先使用"),
                "a fresh deadline links the radar to the positive weekly balance with an explicit reminder");
            double changed = (double)Field(Field(hud, "radarMotion"), "ChangedAt");
            Set(hud, "previewMotionClock", 1800.0); Call(hud, "UpdateMetrics");
            Check((double)Field(Field(hud, "radarMotion"), "ChangedAt") == changed,
                "unchanged API polls cannot retrigger the linked arrival sweep");
            Check(QuotaEffectChanges(hud, 1800, 3200, new Rectangle(37, 456, 14, 13)) > 12,
                "a visible upward signal bridges the radar and quota surfaces");
            Set(hud, "previewMotionClock", 2200.0); SavePreview(hud, Path.Combine(root, "build", "corner-spectrum-signal.png"));
            // Reproduce the real API shape: a banked date window must retain
            // its countdown without sending any signal into the quota card.
            Set(hud, "motionAllowed", false);
            upcoming["reset_type"] = "banked"; upcoming["precision"] = "date";
            upcoming["end_ms"] = now + 2 * 86400000;
            Set(hud, "previewMotionClock", 2200.0); Call(hud, "UpdateMetrics");
            var cardView = (ResetRadarDisplay)Field(hud, "resetRadar");
            Check(cardView.HasSignal && cardView.CountingDown && cardView.EventHeading == "重置卡发放" && cardView.EventBadge == "不自动重置额度",
                "a banked date window remains visible with a card-only label and countdown");
            Check((string)Call(hud, "QuotaResetLink") == "none" && (string)Call(hud, "QuotaHeading") == "周额度剩余",
                "card grants never encourage early depletion of weekly quota");
            SavePreview(hud, Path.Combine(root, "build", "corner-radar-card-grant.png"));
            var motion = Field(hud, "radarMotion");
            double cardArrival = (double)Field(motion, "ChangedAt");
            motion.GetType().GetMethod("Announce", Private).Invoke(motion, new object[] {
                new ResetNotice {Kind = "completed", ResetType = "global", EventId = "unrelated-global", Title = "global"}, 2300.0});
            Check((double)Field(motion, "ChangedAt") == cardArrival && !(bool)Field(motion, "BurstCompleted"),
                "an unrelated global completion notification cannot contaminate the displayed card forecast");
            using (var a = QuotaEffectFrame(hud, 2200))
            {
                radar["upcoming"] = null; Call(hud, "UpdateMetrics");
                using (var b = QuotaEffectFrame(hud, 2200))
                    Check(ImageChanges(a, b, new Rectangle(20, 260, 360, 209)) == 0,
                        "card-only signal leaves quota pixels exactly equal to the ordinary no-signal state");
            }
            radar["upcoming"] = upcoming;
            foreach (string type in new[] {"global", "global_and_banked", "banked", "unknown", "", "future_type"})
            {
                upcoming["reset_type"] = type; Call(hud, "UpdateMetrics");
                Check((string)Call(hud, "QuotaResetLink") == (type == "global" || type == "global_and_banked" ? "forecast" : "none"),
                    "only explicitly global reset types can link quota feedback: " + type);
                var labelView = (ResetRadarDisplay)Field(hud, "resetRadar");
                using (var surface = new Bitmap(400, 738)) using (var g = Graphics.FromImage(surface)) using (var format = StringFormat.GenericTypographic)
                    Check(g.MeasureString(labelView.EventHeading, (Font)Field(hud, "cornerTitleFont"), 1000, format).Width <= 110 &&
                        g.MeasureString(labelView.EventBadge, (Font)Field(hud, "cornerMicroFont"), 1000, format).Width <= 113,
                        "event category and guidance fit the existing compact radar layout: " + type);
                if (type == "global_and_banked" || type == "unknown") SavePreview(hud, Path.Combine(root, "build", "corner-radar-" + type + ".png"));
            }
            radar["upcoming"] = null;
            radar["latest_events"] = new object[] {new Dictionary<string, object> {{"id", "card-done"}, {"kind", "reset_completed"},
                {"reset_type", "banked"}, {"at_ms", now - 1000}, {"time_basis", "completed"}}};
            Call(hud, "UpdateMetrics");
            Check(((ResetRadarDisplay)Field(hud, "resetRadar")).Countdown == "重置卡已发放" && (string)Call(hud, "QuotaResetLink") == "none",
                "completed card grants never claim weekly quota recovery or light a quota confirmation pulse");
            SavePreview(hud, Path.Combine(root, "build", "corner-radar-card-completed.png"));
            radar["latest_events"] = new object[0]; radar["upcoming"] = upcoming;
            upcoming["reset_type"] = "global"; upcoming["precision"] = "deadline"; upcoming["end_ms"] = now + 7200000;
            Set(hud, "previewMotionClock", 1000.0);
            Set(hud, "motionAllowed", true); Call(hud, "UpdateMetrics");
            double coverageBefore = ((QuotaWeekScale)Field(hud, "quotaScale")).FutureCoverage;
            Check(QuotaEffectChanges(hud, 2400, 2600, new Rectangle(40, 362, 140, 17)) > 40 &&
                ((QuotaWeekScale)Field(hud, "quotaScale")).FutureCoverage == coverageBefore,
                "reset spectrum reaches the usable timeline without changing its duration or depletion position");
            var folder = Path.Combine(root, "build", "quota-spectrum-frames"); Directory.CreateDirectory(folder);
            for (int i = 0; i < 60; i++) { Set(hud, "previewMotionClock", 1000.0 + i * 120); SavePreview(hud, Path.Combine(folder, i.ToString("000") + ".png")); }
            Set(hud, "previewMotionClock", 15000.0);
            Check((string)Call(hud, "QuotaResetLink") == "forecast" && !(bool)Field(hud, "quotaCached"),
                "a valid forecast remains linked after the arrival sweep settles");
            // Resource stress includes the new gradient text and linked vector effects.
            StressDrawing(hud);
            now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            data["updated_at_ms"] = quota["updated_at_ms"] = radar["fetched_at_ms"] = radar["source_updated_at_ms"] = now;
            Call(hud, "UpdateMetrics"); Set(hud, "motionAllowed", false);
            Check(QuotaEffectChanges(hud, 1800, 3200, new Rectangle(20, 260, 360, 209)) == 0,
                "static preference preserves rainbow and reset labels with no animated pixels");
            Set(hud, "previewMotionClock", 2200.0); SavePreview(hud, Path.Combine(root, "build", "corner-spectrum-static.png"));
            Set(hud, "motionAllowed", true);
            guard.Sample(new MemorySnapshot {CommitLimit = 100UL << 30, CommitAvailable = 1UL << 30}, 1);
            Check(QuotaEffectChanges(hud, 1800, 3200, new Rectangle(20, 260, 360, 209)) == 0,
                "memory protection freezes rainbow and all linked effects even when motion is enabled");
            guard.Sample(healthy, 2); guard.Sample(healthy, 32002);
            quota["state"] = "unavailable"; Call(hud, "UpdateMetrics");
            Check((string)Call(hud, "QuotaResetLink") == "none" &&
                QuotaEffectChanges(hud, 1800, 3200, new Rectangle(30, 295, 168, 49)) == 0,
                "cached account balances stay neutral and cannot invite usage from a public prediction");
            Set(hud, "previewMotionClock", 2200.0); SavePreview(hud, Path.Combine(root, "build", "corner-spectrum-cached.png"));
            quota["state"] = "ok"; radar["state"] = "unavailable"; Call(hud, "UpdateMetrics");
            Check((string)Call(hud, "QuotaResetLink") == "none" && (string)Call(hud, "QuotaHeading") == "周额度剩余",
                "offline radar immediately removes the usage reminder while ordinary fresh quota keeps its rainbow");
            radar["state"] = "ok"; upcoming["id"] = "quota-spectrum-date"; upcoming["precision"] = "date";
            upcoming["end_ms"] = now + 86400000; Call(hud, "UpdateMetrics");
            Check((string)Call(hud, "QuotaResetLink") == "forecast", "coarse date predictions link without waiting for an exact reset time");
            weekly["remaining_percent"] = 0.0; Call(hud, "UpdateMetrics");
            Check((string)Call(hud, "QuotaResetLink") == "none" &&
                QuotaEffectChanges(hud, 1800, 3200, new Rectangle(30, 295, 168, 49)) == 0,
                "an empty balance cannot receive a use-it-now reminder or celebratory rainbow");
            weekly["remaining_percent"] = 58.0; radar["upcoming"] = null;
            radar["latest_events"] = new object[] {new Dictionary<string, object> {{"id", "quota-spectrum-complete"}, {"kind", "reset_completed"},
                {"reset_type", "global"}, {"at_ms", now - 60000}, {"time_basis", "completed"}}};
            Set(hud, "previewMotionClock", 20000.0); Call(hud, "UpdateMetrics");
            Check((string)Call(hud, "QuotaResetLink") == "completed" && ((string)Call(hud, "QuotaHeading")).Contains("核对到账"),
                "public completion replaces the usage reminder with account verification, never an invented refill");
            SavePreview(hud, Path.Combine(root, "build", "corner-spectrum-completed.png"));
            Set(hud, "previewMotionClock", 33000.0);
            Check((string)Call(hud, "QuotaResetLink") == "none", "historical completion cannot sustain a quota consumption warning");
        }
        finally
        {
            guard.Sample(healthy, 40000); guard.Sample(healthy, 80000);
            // The stress loop takes longer than the live heartbeat timeout. Resume
            // the original fixture as a current sample, as its collector would.
            var restored = (Dictionary<string, object>)original;
            double resumed = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            restored["updated_at_ms"] = resumed;
            Json.Object(restored, "account_quota")["updated_at_ms"] = resumed;
            Set(hud, "data", original); Set(hud, "previewMotionClock", oldClock); Call(hud, "UpdateMetrics");
            Set(hud, "nextMotionPreference", oldPreference); Set(hud, "motionAllowed", oldMotion);
        }
    }

    static void CheckQuotaRunway(Hud hud, string root)
    {
        var early = new Dictionary<string, object> {{"state", "observing"}, {"method", "weekly_cycle_v4"},
            {"basis", "cycle"}, {"span_hours", 0.0}, {"reset_hours", 168.0}, {"rate_pct_per_day", 0.0},
            {"allowance_pct_per_day", 14.3}, {"provisional", true}};
        var fresh = QuotaRunwayDisplay.Read(early, true, false);
        early["consumed_since_reset_percent"] = 5.0; early["rate_pct_per_day"] = 20.7;
        var actualUse = QuotaRunwayDisplay.Read(early, true, false);
        Check(actualUse.Consumed == 5 && actualUse.RatePerDay == 20.7,
            "actual consumed quota remains five percent independently of the internal per-day projection");
        early["rate_pct_per_day"] = 0.0;
        Check(fresh.RatePerDay == 0 && fresh.Text == "新周期" && fresh.AllowancePerDay == 14.3,
            "fresh full quota immediately shows the new cycle and zero measured consumption");
        early["state"] = "through_reset"; early["span_hours"] = 5.0; early["rate_pct_per_day"] = 9.6;
        early["runway_fraction"] = 1.0; early["gap_hours"] = 0.0;
        fresh = QuotaRunwayDisplay.Read(early, true, false);
        Check(fresh.HasTimeline && fresh.RatePerDay == 9.6 && fresh.Provisional && fresh.Text == "至周重置",
            "early real use provides a qualified estimate without a six-hour frozen blank");
        var forecast = new Dictionary<string, object> {{"state", "estimated"}, {"method", "weekly_cycle_v3"},
            {"basis", "cycle"}, {"span_hours", 24.0}, {"hours", 35.0}, {"reset_hours", 144.0},
            {"runway_fraction", .2398}, {"gap_hours", 109.0}, {"rate_pct_per_day", 41.0},
            {"baseline_pct_per_day", 14.3}, {"allowance_pct_per_day", 9.8}};
        forecast["calculated_at_ms"] = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        forecast["today"] = TodayFixture((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds, 4.2);
        var view = QuotaRunwayDisplay.Read(forecast, true, false);
        Check(view.State == "estimated" && view.Basis == "周期均耗" && view.Text == "1天11小时" &&
            view.HasTimeline && view.ResetHours == 144 && view.GapHours == 109 && view.Fraction == .2398 &&
            view.RatePerDay == 41 && view.BaselinePerDay == 14.3 && view.AllowancePerDay == 9.8,
            "weekly runway exposes the cycle pace, seven-day baseline and remaining daily budget");
        foreach (object value in new object[] { true, "0.5", double.NaN, double.PositiveInfinity, -0.1, 1.1 })
        {
            forecast["runway_fraction"] = value;
            Check(!QuotaRunwayDisplay.Read(forecast, true, false).HasTimeline,
                "invalid timeline fractions cannot draw a misleading risk marker: " + value);
        }
        forecast["runway_fraction"] = .2398;
        forecast["rate_pct_per_day"] = -1.0;
        Check(!QuotaRunwayDisplay.Read(forecast, true, false).HasTimeline,
            "an invalid consumption rate cannot display a risk timeline");
        forecast["rate_pct_per_day"] = 41.0;
        forecast["allowance_pct_per_day"] = double.NaN;
        Check(!QuotaRunwayDisplay.Read(forecast, true, false).AllowancePerDay.HasValue,
            "invalid daily allowance cannot be displayed as a valid budget");
        forecast["allowance_pct_per_day"] = 9.8;
        foreach (object value in new object[] { true, "51", double.NaN, double.PositiveInfinity, -1.0, 169.0 })
        {
            forecast["hours"] = value;
            Check(QuotaRunwayDisplay.Read(forecast, true, false).State == "observing",
                "malformed forecast values do not become a runway: " + value);
        }
        forecast["hours"] = 0.0;
        Check(QuotaRunwayDisplay.Read(forecast, true, false).Text == "不足15分",
            "a sub-15-minute forecast is not rounded up to a fabricated quarter hour");
        forecast["hours"] = 1.5;
        Check(QuotaRunwayDisplay.Read(forecast, true, false).Text == "1小时30分",
            "short forecasts use readable hours and minutes");
        using (var surface = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(surface))
        using (var format = StringFormat.GenericTypographic)
        {
            var font = (Font)Field(hud, "cornerRunwayFont");
            Check(new[] { "6天23小时", "1小时30分", "不足15分", "等待同步", "至周重置" }
                .All(text => g.MeasureString(text, font, 1000, format).Width <= 166),
                "forecast labels fit beside the weekly balance without clipping");
            var small = (Font)Field(hud, "cornerMicroFont");
            Check(new[] { "预计提前约6天18小时用尽", "预计提前不足15分用尽", "预计接近重置时用尽",
                "额度已用尽，等待周重置", "预计提前用尽", "预计可用至周重置", "样本积累中，暂不判断", "同步中，暂不判断" }
                .All(text => g.MeasureString(text, small, 1000, format).Width <= 187),
                "plain-language usage assessments fit beneath the runway headline");
            var micro = (Font)Field(hud, "cornerMicroFont");
            Check(new[] { "最晚 2027/12/31 23:59 前", "预计 2027/12/31 23:59", "等待新的公共预告" }
                .All(text => g.MeasureString(text, micro, 1000, format).Width <= 181),
                "public radar retains its full deadline calendar including the year");
        }
        forecast["state"] = "through_reset";
        forecast["runway_fraction"] = 1.0; forecast["gap_hours"] = 0.0;
        Check(QuotaRunwayDisplay.Read(forecast, true, false).Text == "至周重置",
            "an estimated runway longer than the current window is qualified by the reset");
        forecast["state"] = "exhausted";
        Check(QuotaRunwayDisplay.Read(forecast, true, false).Text == "已耗尽",
            "an actual zero balance is shown distinctly");
        forecast["state"] = "estimated"; forecast["hours"] = 35.0;
        forecast["runway_fraction"] = .2398; forecast["gap_hours"] = 109.0;
        var recentlyCached = QuotaRunwayDisplay.Read(forecast, true, true, 60000);
        Check(recentlyCached.State == "cached" && recentlyCached.Text == "1天11小时" && recentlyCached.Basis == "上次估算" && recentlyCached.HasTimeline,
            "a brief quota interruption retains and labels the last recent estimate");
        Check(QuotaRunwayDisplay.Read(forecast, true, true).Text == "等待同步" &&
            QuotaRunwayDisplay.Read(forecast, true, true, 181000).Text == "等待同步" &&
            QuotaRunwayDisplay.Read(forecast, false, false).Text == "等待额度",
            "expired and missing quota cannot display a live forecast");

        var fixture = (Dictionary<string, object>)Field(hud, "data");
        var quota = Json.Object(fixture, "account_quota");
        var weekly = Json.List(quota, "windows").First();
        double previewNow = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        double originalReset = Json.Number(weekly, "resets_at").Value;
        weekly["resets_at"] = previewNow + 6 * 86400;
        quota["runway"] = new Dictionary<string, object>(forecast);
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-runway-estimate.png"));
        Check(((QuotaRunwayDisplay)Field(hud, "quotaRunway")).Text == "1天11小时",
            "actual card binds the weekly forecast from the account snapshot");
        forecast["today"] = TodayFixture(previewNow * 1000, -8.13);
        quota["runway"] = new Dictionary<string, object>(forecast);
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-today-over-budget.png"));
        Check(((TodayQuotaDisplay)Field(hud, "quotaToday")).OverBudget,
            "the actual card binds the signed deficit from the account snapshot");
        quota["state"] = "unavailable";
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-today-over-budget-cached.png"));
        quota["state"] = "ok";
        forecast["today"] = TodayFixture(previewNow * 1000, 4.2);
        weekly["remaining_percent"] = 89; weekly["resets_at"] = previewNow + 2 * 86400;
        quota["runway"] = new Dictionary<string, object> {{"state", "through_reset"}, {"method", "weekly_cycle_v3"},
            {"basis", "cycle"}, {"span_hours", 120.0}, {"reset_hours", 48.0}, {"runway_fraction", 1.0},
            {"gap_hours", 0.0}, {"rate_pct_per_day", 2.2}, {"baseline_pct_per_day", 14.3}, {"allowance_pct_per_day", 44.5}};
        Json.Object(quota, "runway")["calculated_at_ms"] = previewNow * 1000;
        Json.Object(quota, "runway")["today"] = TodayFixture(previewNow * 1000, 12.5);
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-runway-reset.png"));
        weekly["remaining_percent"] = 1; weekly["resets_at"] = previewNow + 86400;
        quota["runway"] = new Dictionary<string, object> {{"state", "estimated"}, {"method", "weekly_cycle_v3"},
            {"basis", "cycle"}, {"span_hours", 144.0}, {"hours", 1.5}, {"reset_hours", 24.0},
            {"runway_fraction", .0606}, {"gap_hours", 22.5}, {"rate_pct_per_day", 16.5},
            {"baseline_pct_per_day", 14.3}, {"allowance_pct_per_day", 1.0}};
        Json.Object(quota, "runway")["calculated_at_ms"] = previewNow * 1000;
        Json.Object(quota, "runway")["today"] = TodayFixture(previewNow * 1000, 0);
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-runway-urgent.png"));
        weekly["remaining_percent"] = 100; weekly["resets_at"] = originalReset;
        quota["runway"] = new Dictionary<string, object> {{"state", "observing"}, {"reset_hours", 144.0},
            {"baseline_pct_per_day", 14.3}, {"allowance_pct_per_day", 16.7}};
        Json.Object(quota, "runway")["calculated_at_ms"] = previewNow * 1000;
        Json.Object(quota, "runway")["today"] = TodayFixture(previewNow * 1000, 8);
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-runway-observing.png"));
        weekly["remaining_percent"] = 59;
        quota["runway"] = new Dictionary<string, object>(forecast);
        quota["state"] = "unavailable";
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-runway-cached.png"));
        Check(((QuotaRunwayDisplay)Field(hud, "quotaRunway")).State == "cached",
            "the actual card keeps a clearly labeled recent estimate through a brief interruption");
        quota["updated_at_ms"] = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds - 181000;
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-runway-stale.png"));
        Check(((QuotaRunwayDisplay)Field(hud, "quotaRunway")).State == "stale",
            "the actual card suppresses the estimate when account quota is cached");
        quota["state"] = "ok";
        quota["updated_at_ms"] = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        weekly["remaining_percent"] = 0;
        quota["runway"] = new Dictionary<string, object> {{"state", "exhausted"}, {"reset_hours", 144.0},
            {"baseline_pct_per_day", 14.3}, {"allowance_pct_per_day", 0.0}};
        Json.Object(quota, "runway")["calculated_at_ms"] = previewNow * 1000;
        Json.Object(quota, "runway")["today"] = TodayFixture(previewNow * 1000, 0);
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-runway-exhausted.png"));
        Check(((QuotaRunwayDisplay)Field(hud, "quotaRunway")).State == "exhausted" &&
            ((QuotaRunwayDisplay)Field(hud, "quotaRunway")).HasTimeline,
            "actual exhaustion is distinct from an estimated depletion");
        weekly["remaining_percent"] = 100;
        quota["runway"] = new Dictionary<string, object> {{"state", "observing"}, {"reset_hours", 144.0},
            {"baseline_pct_per_day", 14.3}, {"allowance_pct_per_day", 16.7}};
        Json.Object(quota, "runway")["calculated_at_ms"] = previewNow * 1000;
        Json.Object(quota, "runway")["today"] = TodayFixture(previewNow * 1000, 8);
        Call(hud, "UpdateMetrics");
        SavePreview(hud, Path.Combine(root, "build", "corner-runway-full-quota.png"));
        weekly["remaining_percent"] = 59;
        weekly["resets_at"] = originalReset;
        quota.Remove("runway");
        Call(hud, "UpdateMetrics");
    }

#if HUD_TEST
    static void CheckMemoryProtection(Hud hud, string root)
    {
        const ulong GiB = 1UL << 30;
        var healthy = new MemorySnapshot { CommitLimit = 100 * GiB, CommitAvailable = 20 * GiB, PhysicalAvailable = 8 * GiB };
        var low = new MemorySnapshot { CommitLimit = 100 * GiB, CommitAvailable = 2 * GiB, PhysicalAvailable = 8 * GiB };
        RenderMemoryGuard.TestSnapshot = null;
        MemorySnapshot native;
        Check(RenderMemoryGuard.TryRead(out native) && native.CommitLimit > 0 &&
            native.CommitAvailable <= native.CommitLimit && native.PhysicalAvailable > 0,
            "Windows performance counters provide valid system-wide memory headroom");
        RenderMemoryGuard.TestSnapshot = healthy;
        var guard = new RenderMemoryGuard();
        Check(!guard.Sample(healthy, 0) && !guard.Reduced, "healthy system keeps full visual effects");
        Check(guard.Sample(low, 1) && guard.Reduced, "low system commit enters resource-saving mode even with free physical RAM");
        guard.Sample(healthy, 2000);
        guard.Sample(low, 20000);
        guard.Sample(healthy, 22000);
        guard.Sample(healthy, 51000);
        Check(guard.Reduced, "a brief recovery or renewed pressure cannot rapidly toggle visual modes");
        Check(guard.Sample(healthy, 52000) && !guard.Reduced, "full visuals return after 30 seconds of sustained headroom");
        Check(guard.Sample(new MemorySnapshot { CommitLimit = 100 * GiB, CommitAvailable = 20 * GiB,
            PhysicalAvailable = GiB / 4 }, 54000) && guard.Reduced, "critically low physical memory also reduces rendering");

        Set(hud, "previewMotionClock", 100000.0);
        Call(hud, "SetCornerMotion", "on");
        int attempts = 0;
        hud.TestCornerRender = delegate { attempts++; hud.TestCornerRender = null; throw new OutOfMemoryException(); };
        SavePreview(hud, Path.Combine(root, "build", "corner-memory-reduced.png"));
        var liveGuard = (RenderMemoryGuard)Field(hud, "renderMemory");
        Check(attempts == 1 && liveGuard.Reduced && !hud.IsDisposed,
            "a simulated GDI allocation failure is caught and the card immediately redraws");
        Check(Field(hud, "cornerBackdrop") == null && Field(hud, "cornerBlueLight") == null &&
            Field(hud, "cornerMintLight") == null && !(bool)Field(hud, "motionAllowed") &&
            ((System.Windows.Forms.Timer)Field(hud, "ticker")).Interval == 1000 &&
            !((System.Windows.Forms.Timer)Field(hud, "motionTicker")).Enabled,
            "resource-saving mode releases bitmap caches, stops animation and lowers refresh frequency");
        Call(hud, "SetCornerMotion", "on");
        Check(!(bool)Field(hud, "motionAllowed"), "animation preference cannot override active memory protection");
        int repeated = 0;
        hud.TestCornerRender = delegate { repeated++; throw new OutOfMemoryException(); };
        SavePreview(hud, Path.Combine(root, "build", "corner-memory-emergency.png"));
        SavePreview(hud, Path.Combine(root, "build", "corner-memory-emergency-again.png"));
        Check(repeated == 2 && !hud.IsDisposed, "persistent rendering failure uses a lightweight fallback without a tight retry loop");
        hud.TestCornerRender = null;
        RenderMemoryGuard.TestSnapshot = healthy;
        Set(hud, "nextMemoryCheck", 0.0);
        Call(hud, "CheckMemoryPressure");
        Set(hud, "previewMotionClock", 129999.0);
        Call(hud, "CheckMemoryPressure");
        Check(liveGuard.Reduced, "recovery waits for stable headroom");
        Set(hud, "previewMotionClock", 132001.0);
        Call(hud, "CheckMemoryPressure");
        Check(!liveGuard.Reduced && (bool)Field(hud, "motionAllowed") &&
            ((System.Windows.Forms.Timer)Field(hud, "ticker")).Interval == 100 &&
            ((System.Windows.Forms.Timer)Field(hud, "motionTicker")).Enabled,
            "recovery restores the user's motion preference and normal refresh rate");
        Set(hud, "previewMotionClock", null);
    }
#endif

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
        var quota = Json.Object(fixture, "account_quota");
        quota["state"] = "ok";
        quota["updated_at_ms"] = now;
        quota["runway"] = new Dictionary<string, object> {{"state", "estimated"}, {"method", "weekly_cycle_v3"},
            {"basis", "cycle"}, {"span_hours", 24.0}, {"hours", 35.0}, {"reset_hours", 144.0},
            {"runway_fraction", .2398}, {"gap_hours", 109.0}, {"rate_pct_per_day", 41.0},
            {"baseline_pct_per_day", 14.3}, {"allowance_pct_per_day", 9.8}};
        Json.Object(quota, "runway")["calculated_at_ms"] = now;
        Json.Object(quota, "runway")["today"] = TodayFixture(now, -8.13);
        quota["windows"] = new object[] {new Dictionary<string, object> {
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
        var frames = Path.Combine(root, "build", "design22-frames");
        Directory.CreateDirectory(frames);
        try
        {
            for (int i = 0; i < 75; i++)
            {
                double speed = i < 16 ? 22 : i < 42 ? 138 : i < 61 ? 65 : 0;
                metrics["live_speed"] = SpeedFixture(now, speed, i < 42 ? .8 : 0);
                metrics["stage"] = i < 61 ? "generating" : "tools";
                if (i >= 42) Json.Object(metrics, "live_speed")["last_response_at_ms"] = now + 1;
                Call(hud, "UpdateMetrics");
                Set(hud, "previewMotionClock", clock + i * 80);
                SavePreview(hud, Path.Combine(frames, i.ToString("000") + ".png"));
            }
            // Idle animation is localized: the radar still scans without animating the whole card.
            Json.Object(fixture, "reset_radar")["upcoming"] = null;
            metrics["turn_active"] = false;
            metrics["live_speed"] = SpeedFixture(now, 0, 0);
            Json.Object(metrics, "live_speed")["state"] = "idle";
            Call(hud, "UpdateMetrics");
            Set(hud, "previewMotionClock", clock + 8000);
            SavePreview(hud, Path.Combine(root, "build", "motion21-idle-a.png"));
            Set(hud, "previewMotionClock", clock + 9500);
            SavePreview(hud, Path.Combine(root, "build", "motion21-idle-b.png"));
            using (var a = new Bitmap(Path.Combine(root, "build", "motion21-idle-a.png")))
            using (var b = new Bitmap(Path.Combine(root, "build", "motion21-idle-b.png")))
            {
                int changed = 0;
                for (int y = 484; y < 516; y++)
                    for (int x = 28; x < 60; x++)
                    {
                        int px = (int)(x * a.Width / 400.0), py = (int)(y * a.Height / 738.0);
                        var p = a.GetPixel(px, py); var q = b.GetPixel(px, py);
                        if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 6) changed++;
                    }
                Check(changed > 40, "idle radar visibly scans inside its own icon without requiring broad decorative motion");
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
            // WinForms lazily creates native handles when the shown window first
            // processes WM_PAINT; compare steady-state resources, not startup.
            hud.Refresh();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
            Collect();
            var before = HudDiagnostics.Resources();
            var paintWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++)
            {
                // The production collector sends heartbeats while the painter runs.
                // Keep that condition in the stress fixture even on slower machines.
                if (i % 100 == 0)
                {
                    ((Dictionary<string, object>)Field(hud, "data"))["updated_at_ms"] =
                        (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                    var speed = Json.Object(Json.Object((Dictionary<string, object>)Field(hud, "data"), "metrics"), "live_speed");
                    if (speed != null) speed["updated_at_ms"] = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                }
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
        var view = new ResetRadarDisplay {State = "deadline", ResetType = "global", PredictionKind = "deadline", UpcomingId = "signal-one",
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
            {"upcoming", new Dictionary<string, object> {{"id", "visual-preview"}, {"reset_type", "global"}, {"precision", "deadline"},
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

    static Dictionary<string, object> SpeedFixture(double now, double rate, double estimate)
    {
        return new Dictionary<string, object> {{"state", estimate > 0 ? "estimating" : rate > 0 ? "measured" : "tools"},
            {"updated_at_ms", now}, {"rate", rate}, {"estimated_share", estimate}, {"recent_rate", 74.6}, {"steady_rate", 74.6},
            {"peak_rate", 138.0}, {"last_response_at_ms", now - 5000}, {"last_response_rate", 72.7}, {"confirmed_turn_output", 12400}, {"cache_percent", 98.8},
            {"history", Enumerable.Range(0, 60).Select(i => new Dictionary<string, object> {
                {"measured", i < 20 ? 0.0 : 30.0 + Math.Sin(i / 4.0) * 18},
                {"estimated", i < 48 ? 0.0 : estimate * rate} }).ToArray()}};
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
    static void SaveRealtimeAnimation(Hud hud, string root)
    {
        var fixture = (Dictionary<string, object>)Field(hud, "data");
        var metrics = Json.Object(fixture, "metrics");
        var quota = Json.Object(fixture, "account_quota");
        var weekly = Json.List(quota, "windows").First(w => Json.Number(w, "window_minutes") == 10080);
        var folder = Path.Combine(root, "build", "realtime48-frames"); Directory.CreateDirectory(folder);
        var oldPreference = Field(hud, "nextMotionPreference");
        var oldMotion = Field(hud, "motionAllowed");
        Set(hud, "nextMotionPreference", double.MaxValue);
        Set(hud, "motionAllowed", true);
        for (int i = 0; i < 150; i++)
        {
            double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            double rate = i < 25 ? 24.6 : i < 50 ? 48.2 : i < 75 ? 89.7 : i < 100 ? 132.5 : i < 125 ? 67.4 : 42.8;
            fixture["updated_at_ms"] = now; metrics["stage"] = "generating"; metrics["turn_active"] = true;
            metrics["live_speed"] = SpeedFixture(now, rate, .85);
            quota["state"] = "ok"; quota["updated_at_ms"] = now;
            weekly["remaining_percent"] = i < 50 ? 3.0 : i < 100 ? 100.0 : 99.0;
            weekly["resets_at"] = now / 1000 + 7 * 86400 - (i >= 100 ? 3600 : 0);
            quota["runway"] = new Dictionary<string, object> {{"method", "weekly_cycle_v4"}, {"state", "observing"},
                {"basis", "cycle"}, {"span_hours", i >= 100 ? 1.0 : 0.0}, {"rate_pct_per_day", i >= 100 ? 24.0 : 0.0},
                {"allowance_pct_per_day", 14.3}, {"reset_hours", 168.0}, {"provisional", true},
                {"calculated_at_ms", now}, {"today", TodayFixture(now, i < 50 ? -2.1 : i < 100 ? 8.4 : 7.4)}};
            var runway = Json.Object(quota, "runway");
            runway["consumed_since_reset_percent"] = 100.0 - (double)weekly["remaining_percent"];
            if (i < 50)
            {
                weekly["resets_at"] = now / 1000 + 3 * 3600;
                runway["state"] = "through_reset"; runway["span_hours"] = 165.0;
                runway["reset_hours"] = 3.0; runway["rate_pct_per_day"] = 97.0 * 24 / 165;
                runway["allowance_pct_per_day"] = 24.0; runway["runway_fraction"] = 1.0;
                runway["gap_hours"] = 0.0; runway["provisional"] = false;
            }
            else if (i >= 100)
            {
                runway["state"] = "estimated"; runway["reset_hours"] = 167.0; runway["hours"] = 99.0;
                runway["runway_fraction"] = 99.0 / 167; runway["gap_hours"] = 68.0;
                runway["allowance_pct_per_day"] = 99.0 * 24 / 167;
            }
            Set(hud, "previewMotionClock", 200000.0 + i * 40);
            Call(hud, "UpdateMetrics");
            SavePreview(hud, Path.Combine(folder, i.ToString("000") + ".png"));
            if (i == 32)
            {
                var surge = (SpeedSurge)Field(hud, "speedSurge");
                Check(surge.Started == 201000 && Math.Abs(surge.Gain - 23.6) < .001,
                    "rendered acceleration comes from the live 24.6 to 48.2 tok/s transition");
            }
        }
        Check(((ReadoutTransition)Field(hud, "speedDigits")).Text == "42.8", "animated live headline follows the final current speed, not the static reference average");
        Set(hud, "nextMotionPreference", oldPreference);
        Set(hud, "motionAllowed", oldMotion);
        Set(hud, "previewMotionClock", null);
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

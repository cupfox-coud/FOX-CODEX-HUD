using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CodexTokenHud
{
    internal static class Native
    {
        internal delegate bool EnumProc(IntPtr h, IntPtr state);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr state);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr h, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr h, ref Point point);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr h, uint command);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr h, IntPtr after,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLong64(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong32(IntPtr h, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, uint attribute, out int value, int size);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLong64(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint="SetWindowLongW")] private static extern int SetWindowLong32(IntPtr h, int index, int value);
        internal static IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value)
        { return IntPtr.Size == 8 ? SetWindowLong64(h, index, value) : new IntPtr(SetWindowLong32(h, index, value.ToInt32())); }
        internal static IntPtr GetWindowLongPtr(IntPtr h, int index)
        { return IntPtr.Size == 8 ? GetWindowLong64(h, index) : new IntPtr(GetWindowLong32(h, index)); }
        internal static bool IsTopmost(IntPtr h)
        { return (GetWindowLongPtr(h, -20).ToInt64() & 8) != 0; }
        internal static int? Cloaked(IntPtr h)
        {
            try { int value; return DwmGetWindowAttribute(h, 14, out value, 4) == 0 ? (int?)value : null; }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        internal static double Scale(IntPtr h)
        {
            try { uint dpi = GetDpiForWindow(h); return dpi > 0 ? dpi / 96.0 : 1.0; }
            catch (EntryPointNotFoundException) { return 1.0; }
        }
        internal static uint ProcessId(IntPtr h) { uint id; GetWindowThreadProcessId(h, out id); return id; }
        internal static List<IntPtr> Windows()
        {
            var pids = new HashSet<uint>(Process.GetProcessesByName("ChatGPT").Select(p => (uint)p.Id));
            var result = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr state) {
                if (pids.Contains(ProcessId(h)) && IsWindowVisible(h) && !IsIconic(h)) result.Add(h);
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }

    internal sealed class ViewSnapshot
    {
        internal IntPtr Handle;
        internal uint ProcessId;
        internal string Title;
        internal int PaneLeft;
        internal bool Primary;
        internal AutomationElement RootElement;
        internal AutomationElement TitleElement;
    }

    internal static class Inspector
    {
        internal static ViewSnapshot Read(IntPtr handle, HashSet<string> titles)
        {
            var result = new ViewSnapshot { Handle = handle, ProcessId = Native.ProcessId(handle) };
            try
            {
                var root = AutomationElement.FromHandle(handle);
                if (root == null) return result;
                var bounds = root.Current.BoundingRectangle;
                double scale = Native.Scale(handle);
                var request = new CacheRequest();
                request.Add(AutomationElement.NameProperty);
                request.Add(AutomationElement.ControlTypeProperty);
                request.Add(AutomationElement.BoundingRectangleProperty);
                request.Add(AutomationElement.IsOffscreenProperty);
                request.AutomationElementMode = AutomationElementMode.Full;
                var condition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                var header = new List<AutomationElement>();
                using (request.Activate())
                {
                    var elements = root.FindAll(TreeScope.Descendants, condition);
                    foreach (AutomationElement element in elements)
                    {
                        var c = element.Cached;
                        var box = c.BoundingRectangle;
                        if (c.IsOffscreen || box.IsEmpty || double.IsInfinity(box.Left) ||
                            box.Top < bounds.Top || box.Top > bounds.Top + 160 * scale) continue;
                        string name = c.Name;
                        if (string.IsNullOrEmpty(name) || name.Length > 160) continue;
                        if (c.ControlType == ControlType.Button &&
                            (name == "聊天操作" || name == "Chat actions" || name == "Thread actions" ||
                             name.StartsWith("切换模式，当前模式") || name.StartsWith("Switch mode"))) result.Primary = true;
                        if (c.ControlType == ControlType.Text && box.Top > bounds.Top + 42 * scale &&
                            box.Top < bounds.Top + 108 * scale && box.Left > bounds.Left + 100 * scale)
                            header.Add(element);
                    }
                }
                // Known local chat titles keep header discovery working across menu label/language changes.
                if (!result.Primary && header.Any(e => titles.Contains(e.Cached.Name))) result.Primary = true;
                if (!result.Primary) return result;
                var title = header.FirstOrDefault(e => titles.Contains(e.Cached.Name));
                if (title == null) title = header.OrderBy(e => e.Cached.BoundingRectangle.Top).FirstOrDefault();
                if (title != null)
                {
                    result.Title = title.Cached.Name;
                    result.PaneLeft = Math.Max(0, (int)(title.Cached.BoundingRectangle.Left - bounds.Left - 40 * scale));
                    result.RootElement = root;
                    result.TitleElement = title;
                }
                else result.PaneLeft = (int)(72 * scale);
            }
            catch (ElementNotAvailableException) { }
            catch (Exception) { }
            return result;
        }

        private static ViewSnapshot ReadCached(ViewSnapshot previous, HashSet<string> titles)
        {
            try
            {
                if (previous.TitleElement != null && previous.RootElement != null)
                {
                    var current = previous.TitleElement.Current;
                    var bounds = previous.RootElement.Current.BoundingRectangle;
                    var box = current.BoundingRectangle;
                    double scale = Native.Scale(previous.Handle);
                    if (!current.IsOffscreen && !box.IsEmpty && !string.IsNullOrEmpty(current.Name) &&
                        box.Top > bounds.Top + 42 * scale && box.Top < bounds.Top + 108 * scale &&
                        box.Left > bounds.Left + 100 * scale)
                        return new ViewSnapshot { Handle = previous.Handle, ProcessId = previous.ProcessId,
                            Primary = true, Title = current.Name, RootElement = previous.RootElement,
                            TitleElement = previous.TitleElement,
                            PaneLeft = Math.Max(0, (int)(box.Left - bounds.Left - 40 * scale)) };
                }
            }
            catch (Exception) { }
            return Read(previous.Handle, titles);
        }

        internal static ViewSnapshot Find(ViewSnapshot previous, HashSet<string> titles)
        {
            IntPtr preferred = previous == null ? IntPtr.Zero : previous.Handle;
            // A second Codex window can be foreground while the previous window stays open.
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != preferred && Native.IsWindowVisible(foreground))
            {
                uint foregroundPid = Native.ProcessId(foreground);
                if (Process.GetProcessesByName("ChatGPT").Any(p => p.Id == foregroundPid))
                {
                    var active = Read(foreground, titles);
                    if (active.Primary) return active;
                }
            }
            if (preferred != IntPtr.Zero && Native.IsWindow(preferred) && !Native.IsIconic(preferred))
            {
                var snapshot = ReadCached(previous, titles);
                if (snapshot.Primary) return snapshot;
            }
            foreach (var h in Native.Windows().OrderByDescending(h => h == Native.GetForegroundWindow()))
            {
                var snapshot = Read(h, titles);
                if (snapshot.Primary) return snapshot;
            }
            return null;
        }
    }

    internal sealed class MetricDisplay
    {
        internal string Model = "等待统计", Rate = "--", Cache = "缓存 待统计", Stage = "等待绑定", Freshness = "等待数据";
        internal double? SampleAgeMs, CollectorAgeMs;
        internal bool Fresh;
        internal static string Number(double? value) { return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "--"; }
        internal static string Age(double milliseconds)
        {
            double seconds = Math.Max(0, milliseconds / 1000);
            if (seconds < 1) return "刚刚";
            if (seconds < 60) return ((int)seconds).ToString() + "秒前";
            if (seconds < 3600) return ((int)(seconds / 60)).ToString() + "分钟前";
            if (seconds < 86400) return ((int)(seconds / 3600)).ToString() + "小时前";
            return ((int)(seconds / 86400)).ToString() + "天前";
        }

        internal static MetricDisplay Read(Dictionary<string, object> data, string title, double nowMs)
        {
            var result = new MetricDisplay();
            bool bound = Json.Text(data, "title") == title && Json.Text(data, "binding") == "ok";
            var metrics = bound ? Json.Object(data, "metrics") : null;
            result.Model = Json.Text(metrics, "model");
            if (string.IsNullOrEmpty(result.Model)) result.Model = string.IsNullOrEmpty(title) ? "打开本地对话" : "等待统计";
            if (!bound) {
                string repository = Json.Text(data, "repository_state");
                result.Stage = repository == "missing" ? "未找到本地对话" : repository == "unsupported" ? "数据库版本待适配" :
                    repository == "unavailable" ? "数据库暂不可读" : Json.Text(data, "binding") == "ambiguous" ? "同名对话 · 右键选择" : "等待绑定";
                return result;
            }
            double? heartbeat = Json.Number(data, "updated_at_ms");
            result.CollectorAgeMs = heartbeat.HasValue ? Math.Max(0, nowMs - heartbeat.Value) : (double?)null;
            if (!heartbeat.HasValue || result.CollectorAgeMs > 6000)
            { result.Stage = "采集器重连中"; result.Freshness = "数据未更新"; return result; }
            var range = Json.Object(data, "range");
            if (range != null && Json.Text(range, "thread_id") == Json.Text(data, "thread_id"))
            {
                var models = Json.Get(range, "models") as IEnumerable;
                if (models != null) {
                    var labels = models.Cast<object>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToArray();
                    if (labels.Length > 0) result.Model = String.Join(" / ", labels);
                }
                result.Rate = Number(Json.Number(range, "rate"));
                result.Cache = "缓存 " + (Json.Number(range, "cache_percent").HasValue ? Number(Json.Number(range, "cache_percent")) + "%" : "暂无数据");
                result.Stage = "区间平均 · " + Json.Text(range, "sample_count") + " 次响应";
                result.Freshness = "已确认范围";
                result.Fresh = Json.Number(range, "rate").HasValue;
                return result;
            }
            string source = Json.Text(Json.Object(data, "source"), "state");
            if (source == "unavailable" || source == "catching_up")
            { result.Stage = source == "unavailable" ? "日志不可读" : "同步最新日志"; return result; }
            string stage = Json.Text(metrics, "stage");
            result.Stage = stage == "tools" ? "工具运行中" : stage == "generating" ? "生成中" : "最近响应";
            var last = Json.Object(metrics, "last");
            if (last != null && !string.IsNullOrEmpty(Json.Text(last, "model")) && Json.Text(last, "model") != result.Model) last = null;
            double? measured = Json.Number(last, "measured_at_ms");
            if (!measured.HasValue) { result.Freshness = "等待新统计"; return result; }
            result.SampleAgeMs = Math.Max(0, nowMs - measured.Value);
            double? turnStart = Json.Number(metrics, "turn_started_at_ms");
            if (turnStart.HasValue && turnStart.Value > measured.Value && stage != "idle")
            { result.Freshness = "本轮待统计"; return result; }
            if (result.SampleAgeMs >= 15 * 60 * 1000)
            { result.Cache = "缓存 历史"; result.Freshness = "历史 " + Age(result.SampleAgeMs.Value); return result; }
            result.Rate = Number(Json.Number(last, "rate"));
            result.Cache = "缓存 " + (Json.Number(last, "cache_percent").HasValue ? Number(Json.Number(last, "cache_percent")) + "%" : "暂无数据");
            result.Freshness = "更新 " + Age(result.SampleAgeMs.Value);
            result.Fresh = true;
            return result;
        }
    }

    internal static class Json
    {
        internal static Dictionary<string, object> Read(string file)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            }
            catch { return new Dictionary<string, object>(); }
        }
        internal static object Get(Dictionary<string, object> value, string key)
        { object result; return value != null && value.TryGetValue(key, out result) ? result : null; }
        internal static string Text(Dictionary<string, object> value, string key)
        { return Convert.ToString(Get(value, key), CultureInfo.InvariantCulture); }
        internal static double? Number(Dictionary<string, object> value, string key)
        {
            var o = Get(value, key); if (o == null) return null;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return null; }
        }
        internal static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        { return Get(value, key) as Dictionary<string, object>; }
        internal static IEnumerable<Dictionary<string, object>> List(Dictionary<string, object> value, string key)
        {
            var array = Get(value, key) as IEnumerable;
            if (array == null) yield break;
            foreach (var o in array) { var d = o as Dictionary<string, object>; if (d != null) yield return d; }
        }
        internal static void Write(string file, Dictionary<string, object> value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var temporary = file + ".new";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
            }
            catch { }
        }
    }

    internal sealed partial class Hud : Form
    {
        private readonly RuntimeConfiguration configuration;
        private readonly string folder;
        private readonly string runtime;
        private readonly System.Windows.Forms.Timer ticker = new System.Windows.Forms.Timer();
        private readonly ToolTip tooltip = new ToolTip { InitialDelay = 250, AutoPopDelay = 25000, ReshowDelay = 100 };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly NotifyIcon tray = new NotifyIcon();
        private Process backend;
        private ViewSnapshot view;
        private Dictionary<string, object> data = new Dictionary<string, object>();
        private DateTime nextInspection = DateTime.MinValue;
        private DateTime lastStatus = DateTime.MinValue;
        private DateTime nextBackendCheck = DateTime.MinValue;
        private bool inspecting;
        private int inspectionGeneration;
        private bool waitingForAutoFocus;
        private int displayError;
        private DateTime lastShowRequest = DateTime.MinValue;
        private bool stopping;
        private Rectangle waitingBounds;
        private bool manualLayout;
        private bool sizingOrMoving;
        private double relativeX;
        private double relativeY;
        private double layoutWidth = 900;
        private double layoutHeight = 38;
        private string selectedTitle = "";
        private string pinnedId;
        private string manualBindingId = "";
        internal string FollowOwner = "";
        private bool activityFollowing;
        private bool startupChanging;
        private string previousSelection = "";
        private string rateText = "--";
        private string modelText = "等待对话";
        private string cacheText = "缓存 --";
        private string weeklyText = "本对话周额度 暂无数据";
        private string stageText = "";
        private string freshnessText = "等待数据";
        private bool fresh;
        private double? sampleAgeMs;
        private double? collectorAgeMs;
        private long refreshId;
        private DateTime refreshRequestedAt = DateTime.MinValue;
        private DateTime refreshNoticeUntil = DateTime.MinValue;
        private double? refreshPreviousSample;
        private string refreshNotice = "";
        private string helpText = "打开本地对话后自动显示。右键可以选择同名对话或退出。";
        private double scale = 1;
        private bool cornerClickThrough = true;
        private DateTime cornerUnlockUntil = DateTime.MinValue;
        private Size cornerShapeSize;
        private string cornerScreen = "";
        private List<Dictionary<string, object>> quotaWindows = new List<Dictionary<string, object>>();
        private bool quotaCached;
        private string quotaNotice = "正在同步账户额度";
        private string quotaSyncText = "每分钟自动同步";
        private readonly Font cornerHeaderFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerSmallFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font cornerSpeedFont = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerCacheFont = new Font("Segoe UI", 24, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font cornerQuotaFont = new Font("Segoe UI", 38, FontStyle.Bold, GraphicsUnit.Pixel);
        private Rectangle closeRect { get { return new Rectangle(Width - (int)(26 * scale) - 4, 3, (int)(26 * scale), Height - 6); } }
        private Rectangle infoRect { get { var close = closeRect; return new Rectangle(close.Left - close.Width, 3, close.Width, Height - 6); } }
        private readonly Font smallFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular);
        private readonly Font labelFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        private readonly Font speedFont = new Font("Segoe UI", 13.0f, FontStyle.Bold);

        internal Hud(RuntimeConfiguration configuration)
        {
            this.configuration = configuration;
            folder = configuration.Folder;
            configuration.PrepareData();
            runtime = configuration.Runtime;
            Text = "FOX CODEX HUD";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 31, 43);
            Size = new Size(1040, 40);
            ContextMenuStrip = menu;
            LoadLayout(Json.Read(configuration.Layout));
            if (configuration.CornerMode)
            {
                BackColor = Color.FromArgb(19, 25, 34);
                Size = new Size(CornerWidth, CornerHeight);
                Opacity = configuration.CornerOpacity;
                tooltip.Active = false;
            }
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                BuildMenu();
                // WinForms cancels an empty dropdown before raising Opening.
                // The menu is populated lazily, so re-evaluate that decision.
                e.Cancel = menu.Items.Count == 0;
            };
            tray.Icon = SystemIcons.Information;
            tray.Text = "FOX CODEX HUD";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { RequestShow(); RequestRefresh(); };
            ticker.Interval = 150;
            ticker.Tick += Tick;
            StartBackend();
            ticker.Start();
            StartCornerMotion();
        }

        private string LayoutPath { get { return string.IsNullOrEmpty(manualBindingId) ? configuration.Layout : Path.Combine(runtime, "pinned-layout.json"); } }
        private void LoadLayout(Dictionary<string, object> layout)
        {
            manualLayout = Json.Text(layout, "mode") == "manual";
            relativeX = Json.Number(layout, "x") ?? 0;
            relativeY = Json.Number(layout, "y") ?? 0;
            layoutWidth = Json.Number(layout, "width") ?? 900;
            layoutHeight = Json.Number(layout, "height") ?? 38;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080;
                if (configuration != null && configuration.CornerMode)
                {
                    cp.ExStyle |= 0x00080000; // Layered + transparent passes mouse input to other applications.
                    if (cornerClickThrough) cp.ExStyle |= 0x00000020;
                }
                else cp.Style |= 0x00040000; // Native sizing support for the original strip.
                return cp;
            }
        }
        internal void StartMonitoring()
        {
            var unused = Handle;
            SyncManualBinding();
            if (configuration.CornerMode && string.IsNullOrEmpty(manualBindingId)) UpdateView(null);
            RequestShow();
        }

        private void RequestShow()
        {
            lastShowRequest = DateTime.UtcNow;
            lastStatus = DateTime.MinValue;
            if (Displayed)
            {
                EnsureShown();
                return;
            }
            waitingForAutoFocus = string.IsNullOrEmpty(manualBindingId);
            nextInspection = DateTime.MinValue;
            UpdateMetrics();
            PositionHud();
        }

        private bool Displayed
        {
            get { return Native.IsWindowVisible(Handle) && !Native.IsIconic(Handle) && (Native.Cloaked(Handle) ?? 0) == 0; }
        }

        private void ConsumeShowRequest()
        {
            string request = Path.Combine(runtime, "show.request.json");
            if (!File.Exists(request)) return;
            try { File.Delete(request); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            RequestShow();
        }

        private void EnsureShown()
        {
            // Form.TopMost reorders/activates the window even when assigned true
            // again. Doing that every 150 ms dismisses the tray menu on focus loss.
            if (!TopMost) TopMost = true;
            if (!Visible) Show();
            // WinForms caches Visible/TopMost; validate Windows' actual state, including hidden startup settings.
            // Avoid changing an already correct z-order so our context menu remains above the strip.
            bool topmost = Native.IsTopmost(Handle);
            if (!topmost)
                // Reset the native z-order band as well as the style: a repeated TOPMOST request alone can be a no-op.
                Native.SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, 0x13);
            if (!Native.IsWindowVisible(Handle) || !topmost)
                displayError = Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x53) ? 0 : Marshal.GetLastWin32Error();
            else displayError = 0;
        }

        private void StartBackend()
        {
            try
            {
                var args = "-I -X utf8 " + RuntimeConfiguration.Quote(Path.Combine(folder, "metrics.py")) +
                    " --runtime " + RuntimeConfiguration.Quote(runtime) + " --codex-home " + RuntimeConfiguration.Quote(configuration.CodexHome) +
                    " --parent-pid " + Process.GetCurrentProcess().Id + " --dashboard" +
                    (configuration.CornerMode ? " --account-quota --network-latency --reset-radar" : "");
                backend = Process.Start(new ProcessStartInfo(configuration.Pythonw, args) {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception) { modelText = "采集器未启动"; }
        }

        private void Tick(object sender, EventArgs e)
        {
            if (stopping) return;
            if (configuration.CornerMode && !cornerClickThrough && DateTime.UtcNow >= cornerUnlockUntil)
                SetCornerClickThrough(true);
            if (ConsumeAppCloseRequest()) return;
            data = Json.Read(Path.Combine(runtime, "metrics.json"));
            bool bindingChanged = SyncManualBinding();
            ConsumeShowRequest();
            if (!string.IsNullOrEmpty(manualBindingId)) UpdateView(null);
            else if (configuration.CornerMode) UpdateView(null);
            if (DateTime.UtcNow >= nextBackendCheck)
            {
                nextBackendCheck = DateTime.UtcNow.AddSeconds(1);
                try
                {
                    double? heartbeat = Json.Number(data, "updated_at_ms");
                    if (backend != null && !backend.HasExited &&
                        DateTime.UtcNow - backend.StartTime.ToUniversalTime() > TimeSpan.FromSeconds(10) &&
                        (!heartbeat.HasValue || EpochMs() - heartbeat.Value > 10000)) backend.Kill();
                    if (backend == null || backend.HasExited) StartBackend();
                }
                catch { StartBackend(); }
            }
            if (!configuration.CornerMode && string.IsNullOrEmpty(manualBindingId) && !inspecting && DateTime.UtcNow >= nextInspection)
            {
                inspecting = true;
                nextInspection = DateTime.UtcNow.AddMilliseconds(350);
                var known = new HashSet<string>(Json.List(data, "threads").Select(t => Json.Text(t, "title")));
                var preferred = activityFollowing ? null : view;
                int generation = inspectionGeneration;
                Task.Run(delegate { return Inspector.Find(preferred, known); }).ContinueWith(task => {
                    if (stopping || IsDisposed) return;
                    try { BeginInvoke(new Action(delegate {
                        inspecting = false;
                        if (generation == inspectionGeneration && string.IsNullOrEmpty(manualBindingId) &&
                            task.Status == TaskStatus.RanToCompletion) UpdateView(task.Result);
                    })); } catch { }
                });
            }
            UpdateMetrics();
            PositionHud();
            if (bindingChanged && !string.IsNullOrEmpty(manualBindingId)) SaveLayout();
            string previewRequest = Path.Combine(runtime, "preview.request");
            if (File.Exists(previewRequest))
            {
                File.Delete(previewRequest);
                using (var bitmap = new Bitmap(Width, Height))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(BackColor);
                        OnPaint(new PaintEventArgs(graphics, new Rectangle(Point.Empty, Size)));
                    }
                    bitmap.Save(Path.Combine(runtime, "hud-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            if (DateTime.UtcNow >= lastStatus)
            {
                lastStatus = DateTime.UtcNow.AddSeconds(1);
                Json.Write(Path.Combine(runtime, "status.json"), new Dictionary<string, object> {
                    {"pid", Process.GetCurrentProcess().Id}, {"visible", Native.IsWindowVisible(Handle)},
                    {"resources", HudDiagnostics.Resources()},
                    {"managed_visible", Visible}, {"native_topmost", Native.IsTopmost(Handle)},
                    {"minimized", Native.IsIconic(Handle)},
                    {"hud_handle", Handle.ToInt64()}, {"cloaked", Native.Cloaked(Handle)}, {"display_error", displayError},
                    {"last_show_request", lastShowRequest.ToString("o")},
                    {"window", view == null ? 0L : view.Handle.ToInt64()}, {"title", selectedTitle},
                    {"thread_id", Json.Text(data, "thread_id")}, {"rate_text", rateText},
                    {"model_text", modelText}, {"cache_text", cacheText}, {"weekly_text", weeklyText},
                    {"binding_mode", string.IsNullOrEmpty(manualBindingId) ? (activityFollowing ? "activity" : "auto") : "manual"},
                    {"follow_owner", FollowOwner}, {"updated_at_ms", EpochMs()},
                    {"awaiting_view", AwaitingView},
                    {"layout_mode", manualLayout ? "manual" : "auto"},
                    {"freshness", freshnessText}, {"sample_age_ms", sampleAgeMs}, {"collector_age_ms", collectorAgeMs},
                    {"source", Json.Object(data, "source")},
                    {"stage", stageText}, {"left", Left}, {"top", Top}, {"width", Width}, {"height", Height},
                    {"corner_mode", configuration.CornerMode}, {"corner_monitor", cornerScreen},
                    {"click_through", configuration.CornerMode && cornerClickThrough}, {"opacity", Opacity},
                    {"animation_mode", configuration.CornerMotion}, {"animation_enabled", motionAllowed},
                    {"system_animation_enabled", systemMotionAllowed}, {"animation_frames", motionPaintCount},
                    {"animation_timer_hz", SystemInformation.TerminalServerSession ? 10 : 25},
                    {"account_quota", Json.Object(data, "account_quota")}, {"quota_cached", quotaCached},
                    {"network_latency", Json.Object(data, "network_latency")},
                    {"activity_title", activity.Title}, {"activity_detail", activity.Detail},
                    {"activity_elapsed", activity.ElapsedText}, {"activity_severity", activity.Severity}, {"activity_active", activity.Active},
                    {"activity_average_ms", activity.AverageMs}, {"activity_completed_count", activity.CompletedCount},
                    {"activity_wait_level", activity.WaitLevel},
                    {"reset_radar", Json.Object(data, "reset_radar")}, {"reset_radar_text", resetRadar == null ? "" : resetRadar.Countdown},
                    {"reset_radar_signal", radarMotion.Active}, {"reset_radar_notice", radarMotion.Notice},
                    {"reset_radar_burst", radarMotion.Bursting(MotionMs)}, {"reset_radar_last_notice", lastResetNoticeTitle},
                    {"reset_radar_last_notice_kind", lastResetNoticeKind}, {"reset_radar_last_notice_at_ms", lastResetNoticeAt},
                    {"reset_radar_attention", radarMotion.Active ? (radarMotion.Confirmed ? "completed" : radarMotion.Urgent ? "urgent" : "forecast") : "none"},
                    {"updated_at", DateTime.UtcNow.ToString("o")} });
            }
        }

        private bool SyncManualBinding()
        {
            var manual = Json.Read(Path.Combine(runtime, "manual-binding.json"));
            string manualId = Json.Text(manual, "thread_id");
            bool bindingChanged = manualId != manualBindingId;
            if (bindingChanged)
            {
                if (Visible) waitingBounds = Bounds;
                if (Native.GetWindow(Handle, 4) != IntPtr.Zero) Native.SetWindowLongPtr(Handle, -8, IntPtr.Zero);
                manualBindingId = manualId;
                waitingForAutoFocus = string.IsNullOrEmpty(manualBindingId);
                inspectionGeneration++;
                TopMost = !string.IsNullOrEmpty(manualBindingId);
                var layout = Json.Read(LayoutPath);
                if (layout.Count > 0 || string.IsNullOrEmpty(manualBindingId)) LoadLayout(layout);
                else if (Left != 0 || Top != 0) {
                    double dpi = Native.Scale(Handle);
                    manualLayout = true;
                    relativeX = Left / dpi; relativeY = Top / dpi;
                    layoutWidth = Width / dpi; layoutHeight = Height / dpi;
                }
                else manualLayout = false;
                view = null;
                previousSelection = "";
                pinnedId = null;
                nextInspection = DateTime.MinValue;
                UpdateView(null);
            }
            return bindingChanged;
        }

        private bool ConsumeAppCloseRequest()
        {
            var request = Json.Read(Path.Combine(runtime, "app-close.request.json"));
            if (string.IsNullOrEmpty(FollowOwner) || Json.Text(request, "owner") != FollowOwner) return false;
            File.Delete(Path.Combine(runtime, "app-close.request.json"));
            Close();
            return true;
        }

        private void Unpin()
        {
            Json.Write(Path.Combine(runtime, "manual-binding.json"), new Dictionary<string, object>());
            SyncManualBinding();
            UpdateMetrics();
            PositionHud();
        }

        private void UpdateView(ViewSnapshot snapshot)
        {
            var manual = Json.Read(Path.Combine(runtime, "manual-binding.json"));
            activityFollowing = false;
            if (!string.IsNullOrEmpty(manualBindingId))
                snapshot = new ViewSnapshot { Handle = Native.GetDesktopWindow(), Primary = true, Title = Json.Text(manual, "title") };
            else if ((configuration.CornerMode || !string.IsNullOrEmpty(FollowOwner) || IsStartupEnabled()) &&
                (snapshot == null || (Native.ProcessId(Native.GetForegroundWindow()) != snapshot.ProcessId &&
                 Native.ProcessId(Native.GetForegroundWindow()) != (uint)Process.GetCurrentProcess().Id && !menu.Visible)))
            {
                activityFollowing = true;
                waitingForAutoFocus = false;
                snapshot = new ViewSnapshot { Handle = Native.GetDesktopWindow(), Primary = true,
                    Title = Json.Text(data, "binding_mode") == "activity" ? Json.Text(data, "title") : "" };
            }
            view = snapshot;
            string title = snapshot == null ? "" : snapshot.Title ?? "";
            if (title != selectedTitle) pinnedId = null;
            selectedTitle = title;
            string id = string.IsNullOrEmpty(manualBindingId) ? pinnedId : manualBindingId;
            string key = title + "\n" + id + "\n" + activityFollowing;
            if (key == previousSelection) return;
            previousSelection = key;
            Json.Write(Path.Combine(runtime, "selection.json"), new Dictionary<string, object> {
                {"title", title}, {"thread_id", id}, {"refresh_id", refreshId},
                {"binding_mode", string.IsNullOrEmpty(manualBindingId) ? (activityFollowing ? "activity" : "auto") : "manual"},
                {"window", snapshot == null ? 0L : snapshot.Handle.ToInt64()}});
        }

        private void RequestRefresh()
        {
            refreshId = DateTime.UtcNow.Ticks;
            refreshRequestedAt = DateTime.UtcNow;
            refreshPreviousSample = Json.Number(Json.Object(Json.Object(data, "metrics"), "last"), "measured_at_ms");
            refreshNotice = "正在重新读取日志";
            refreshNoticeUntil = DateTime.UtcNow.AddSeconds(10);
            previousSelection = "";
            nextInspection = DateTime.MinValue;
            UpdateView(view);
        }

        private void OpenDashboard()
        {
            string url = Json.Text(Json.Read(Path.Combine(runtime, "dashboard.json")), "url");
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != "http" || address.Host != "127.0.0.1")
            {
                MessageBox.Show("统计面板尚未就绪，请稍候再打开。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(url + "&thread=" + Uri.EscapeDataString(Json.Text(data, "thread_id"))) { UseShellExecute = true });
        }

        private bool AwaitingView
        {
            get { return view == null || !Native.IsWindow(view.Handle) || Native.IsIconic(view.Handle) || !Native.IsWindowVisible(view.Handle) ||
                (waitingForAutoFocus && Native.ProcessId(Native.GetForegroundWindow()) != view.ProcessId); }
        }

        private void PositionHud()
        {
            // A direct restore of a minimized WinForms window can activate it, even with a no-activate show command.
            // Create a fresh hidden normal handle instead; EnsureShown uses the normal no-activate display path.
            if (Native.IsIconic(Handle))
            {
                Hide();
                WindowState = FormWindowState.Normal;
                RecreateHandle();
            }
            // Keep this window independent of Codex: destroying a foreign owner would destroy its owned HUD too.
            if (Native.GetWindow(Handle, 4) != IntPtr.Zero) Native.SetWindowLongPtr(Handle, -8, IntPtr.Zero);
            if (configuration.CornerMode)
            {
                PositionCorner();
                return;
            }
            if (waitingForAutoFocus && view != null && Native.ProcessId(Native.GetForegroundWindow()) == view.ProcessId)
                waitingForAutoFocus = false;
            if (AwaitingView)
            {
                if (!TopMost) TopMost = true;
                scale = Native.Scale(Handle);
                MinimumSize = new Size((int)(420 * scale), (int)(32 * scale));
                var area = Screen.FromHandle(Handle).WorkingArea;
                if (waitingBounds.IsEmpty)
                {
                    int width = Math.Max(MinimumSize.Width, Math.Min(area.Width - (int)(14 * scale), (int)(layoutWidth * scale)));
                    int height = Math.Max(MinimumSize.Height, (int)(layoutHeight * scale));
                    waitingBounds = new Rectangle(area.Left + (area.Width - width) / 2,
                        area.Bottom - height - (int)(7 * scale), width, height);
                }
                if (!sizingOrMoving) Bounds = waitingBounds;
                EnsureShown();
                Invalidate();
                return;
            }
            // Focus checks below hide automatic mode elsewhere; keep the independent HUD above the focused Codex window.
            if (!TopMost) TopMost = true;
            IntPtr foreground = Native.GetForegroundWindow();
            bool codexFocused = activityFollowing || !string.IsNullOrEmpty(manualBindingId) || Native.ProcessId(foreground) == view.ProcessId ||
                Native.ProcessId(foreground) == (uint)Process.GetCurrentProcess().Id || menu.Visible;
            Native.Rect client;
            Native.Point origin = new Native.Point();
            if (!Native.GetClientRect(view.Handle, out client) || !Native.ClientToScreen(view.Handle, ref origin)) return;
            scale = Native.Scale(string.IsNullOrEmpty(manualBindingId) ? view.Handle : Handle);
            MinimumSize = new Size((int)(420 * scale), (int)(32 * scale));
            int h = (int)Math.Round(38 * scale);
            int margin = (int)Math.Round(7 * scale);
            int pane = Math.Min(view.PaneLeft, Math.Max(0, client.Right - (int)(420 * scale)));
            int w = client.Right - pane - margin * 2;
            if (w < (int)(360 * scale)) { if (Visible) Hide(); return; }
            var wanted = new Rectangle(origin.X + pane + margin, origin.Y + client.Bottom - h - margin, w, h);
            if (manualLayout)
            {
                wanted = new Rectangle(origin.X + (int)Math.Round(relativeX * scale), origin.Y + (int)Math.Round(relativeY * scale),
                    Math.Max(MinimumSize.Width, (int)Math.Round(layoutWidth * scale)),
                    Math.Max(MinimumSize.Height, (int)Math.Round(layoutHeight * scale)));
                // Preserve a grab area if a display has been disconnected since the last run.
                var area = string.IsNullOrEmpty(manualBindingId) ? Screen.FromHandle(view.Handle).WorkingArea : Screen.FromRectangle(wanted).WorkingArea;
                wanted.X = Math.Max(area.Left - wanted.Width + (int)(120 * scale), Math.Min(wanted.X, area.Right - (int)(120 * scale)));
                wanted.Y = Math.Max(area.Top, Math.Min(wanted.Y, area.Bottom - (int)(32 * scale)));
            }
            if (!sizingOrMoving && Bounds != wanted) Bounds = wanted;
            if (!codexFocused) { if (Visible) Hide(); return; }
            EnsureShown();
            waitingBounds = Bounds;
            Invalidate();
        }

        private void PositionCorner()
        {
            var screens = Screen.AllScreens;
            var screen = screens.FirstOrDefault(s => s.DeviceName == configuration.CornerMonitor) ??
                screens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen;
            cornerScreen = screen.DeviceName;
            scale = Native.Scale(Handle);
            MinimumSize = Size.Empty;
            int margin = Math.Max(8, (int)Math.Round(18 * scale));
            var area = screen.WorkingArea;
            var size = new Size(Math.Min(area.Width - margin * 2, (int)Math.Round(CornerWidth * scale)),
                Math.Min(area.Height - margin * 2, (int)Math.Round(CornerHeight * scale)));
            var wanted = new Rectangle(area.Right - margin - size.Width, area.Top + margin, size.Width, size.Height);
            if (Bounds != wanted) Bounds = wanted;
            if (cornerShapeSize != ClientSize || Region == null)
            {
                cornerShapeSize = ClientSize;
                using (var path = RoundedCard(new RectangleF(0, 0, Width, Height), (float)(26 * scale)))
                {
                    var old = Region;
                    Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            EnsureShown();
            Invalidate();
        }

        private static GraphicsPath RoundedCard(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void SetCornerClickThrough(bool enabled)
        {
            cornerClickThrough = enabled;
            cornerUnlockUntil = enabled ? DateTime.MinValue : DateTime.UtcNow.AddSeconds(30);
            long style = Native.GetWindowLongPtr(Handle, -20).ToInt64() | 0x00080000L | 0x08000000L;
            style = enabled ? style | 0x20L : style & ~0x20L;
            Native.SetWindowLongPtr(Handle, -20, new IntPtr(style));
            Native.SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(Opacity * 255), 2);
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
            lastStatus = DateTime.MinValue;
            Invalidate();
        }

        private static string Format(double? n) { return n.HasValue ? n.Value.ToString("0.0", CultureInfo.InvariantCulture) : "--"; }
        private static double EpochMs() { return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
        private static string TimeLabel(double? milliseconds)
        {
            if (!milliseconds.HasValue || milliseconds.Value <= 0) return "未知";
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds.Value).ToLocalTime().ToString("MM-dd HH:mm:ss"); }
            catch { return "未知"; }
        }

        private void UpdateMetrics()
        {
            UpdateAccountQuota();
            activity = ActivityDisplay.Read(data, selectedTitle, EpochMs());
            UpdateResetRadar();
            bool bound = Json.Text(data, "title") == selectedTitle && Json.Text(data, "binding") == "ok";
            var metrics = bound ? Json.Object(data, "metrics") : null;
            var last = Json.Object(metrics, "last");
            var display = MetricDisplay.Read(data, selectedTitle, EpochMs());
            modelText = display.Model;
            if (last != null && !string.IsNullOrEmpty(Json.Text(last, "model")) && Json.Text(last, "model") != modelText) last = null;
            rateText = display.Rate;
            cacheText = display.Cache;
            stageText = display.Stage;
            freshnessText = display.Freshness;
            if (string.IsNullOrEmpty(manualBindingId) && AwaitingView)
            {
                modelText = "正在识别当前对话";
                rateText = "--";
                cacheText = "缓存 待绑定";
                stageText = "等待窗口识别";
                freshnessText = "自动跟随 · 可右键固定";
            }
            if (!string.IsNullOrEmpty(manualBindingId)) freshnessText = "固定 · " + freshnessText;
            else if (activityFollowing) freshnessText = "最新活动 · " + freshnessText;
            if (refreshRequestedAt != DateTime.MinValue)
            {
                var ack = Json.Object(data, "refresh");
                if (Json.Text(ack, "request_id") == refreshId.ToString(CultureInfo.InvariantCulture))
                {
                    string state = Json.Text(ack, "state");
                    double? latest = Json.Number(ack, "sample_at_ms");
                    refreshNotice = state == "ok" ? (latest == refreshPreviousSample ? "已刷新 · 无新计数" : "已刷新 · 已读最新日志") :
                        state == "unavailable" ? "刷新失败 · 日志不可读" : state == "ambiguous" ? "刷新失败 · 请绑定对话" : "刷新失败 · 未找到对话";
                    refreshRequestedAt = DateTime.MinValue;
                    refreshNoticeUntil = DateTime.UtcNow.AddSeconds(8);
                }
                else if (DateTime.UtcNow - refreshRequestedAt > TimeSpan.FromSeconds(6))
                {
                    refreshNotice = "刷新未响应 · 采集器重连中";
                    refreshRequestedAt = DateTime.MinValue;
                    refreshNoticeUntil = DateTime.UtcNow.AddSeconds(8);
                    try { if (backend != null && !backend.HasExited) backend.Kill(); } catch { }
                    nextBackendCheck = DateTime.MinValue;
                }
            }
            if (DateTime.UtcNow < refreshNoticeUntil) freshnessText = refreshNotice;
            sampleAgeMs = display.SampleAgeMs;
            collectorAgeMs = display.CollectorAgeMs;
            fresh = display.Fresh;
            weeklyText = configuration.CornerMode ? (quotaWindows.Count == 0 ? quotaNotice :
                string.Join(" · ", quotaWindows.Select(w => QuotaLabel(w) + " " + (Json.Number(w, "remaining_percent") ?? 0).ToString("0.#") + "%"))) :
                "本对话周额度 暂无数据";
            var help = new StringBuilder();
            help.AppendLine("当前对话：" + (string.IsNullOrEmpty(selectedTitle) ? "未识别" : selectedTitle));
            help.AppendLine("绑定方式：" + (string.IsNullOrEmpty(manualBindingId) ? (activityFollowing ? "最新活动日志；不代表前台 CLI/IDE 对话" : "自动跟随桌面对话") : "按 ID 固定对话；右键可解除固定"));
            help.AppendLine("模型：" + modelText);
            help.AppendLine("速率：最近一次模型响应的真实输出 token / 生成秒数。");
            help.AppendLine("每 0.1 秒检查新统计，界面每 0.15 秒刷新；实际计数在模型响应完成后到达。");
            help.AppendLine("当前状态：" + stageText + " · " + freshnessText);
            var range = Json.Object(data, "range");
            if (range != null && Json.Text(range, "thread_id") == Json.Text(data, "thread_id"))
            {
                help.AppendLine("当前显示已确认范围的加权平均；右键可返回最近响应。");
                help.AppendLine("范围输出 " + Format(Json.Number(range, "output_tokens")) + " token；有效计时 " +
                    Format(Json.Number(range, "duration_seconds")) + " 秒；计时覆盖 " + Json.Text(range, "timed_samples") +
                    "/" + Json.Text(range, "sample_count") + "，缓存覆盖 " + Json.Text(range, "cache_samples") + "/" + Json.Text(range, "sample_count") + "。");
                help.AppendLine("确认时间：" + TimeLabel(Json.Number(range, "confirmed_at_ms")));
                help.AppendLine("均速使用有效计时样本的 " + Format(Json.Number(range, "timed_output_tokens")) + " 输出 token；" +
                    "缓存使用 " + Format(Json.Number(range, "cached_input_tokens")) + " / " + Format(Json.Number(range, "input_tokens")) + " 输入 token。");
            }
            help.AppendLine("新一轮尚无计数、统计超过 15 分钟或采集器失联时显示 --，旧响应详情仍可在下方查看。");
            help.AppendLine("包含推理 token；不重复相加。隐藏的逐 token 流没有公开实时计数。");
            if (last != null)
            {
                help.AppendLine("输出 " + Format(Json.Number(last, "output_tokens")) + " token，计时 " +
                    Format(Json.Number(last, "duration_ms") / 1000) + " 秒（" +
                    (Json.Text(last, "basis") == "stream" ? "首个模型流式条目至响应结束" : "请求耗时，含首 token 等待") + "）。");
                help.AppendLine("缓存命中率：" + Format(Json.Number(last, "cached_input_tokens")) + " / " +
                    Format(Json.Number(last, "input_tokens")) + " 输入 token = " + Format(Json.Number(last, "cache_percent")) + "%");
                help.AppendLine("最近已完成响应的统计时间：" + TimeLabel(Json.Number(last, "measured_at_ms")));
            }
            help.AppendLine("采集心跳：" + TimeLabel(Json.Number(data, "updated_at_ms")));
            help.AppendLine("本对话周额度：当前服务未提供按对话归属的周额度扣减值，无法精确统计。");
            help.AppendLine("按住条内区域拖动；拖动边缘或右下角调整大小。位置和尺寸自动保存。");
            help.AppendLine("右键可立即刷新数据、恢复底部默认位置、选择同名对话、设置随应用启动或退出。");
            helpText = help.ToString();
            if (!configuration.CornerMode && tooltip.GetToolTip(this) != helpText) tooltip.SetToolTip(this, helpText);
        }

        private void UpdateAccountQuota()
        {
            var quota = Json.Object(data, "account_quota");
            double now = EpochMs();
            double age = now - (Json.Number(quota, "updated_at_ms") ?? 0);
            quotaCached = Json.Text(quota, "state") != "ok" || age > 180000 ||
                now - (Json.Number(data, "updated_at_ms") ?? 0) > 10000;
            quotaWindows = age >= -30000 && age < 900000 ? Json.List(quota, "windows")
                .Where(w => Json.Number(w, "remaining_percent").HasValue &&
                    (Json.Number(w, "resets_at") ?? 0) * 1000 > now &&
                    (Json.Number(w, "window_minutes") ?? 0) > 0)
                .OrderBy(w => Json.Number(w, "window_minutes")).Take(2).ToList() : new List<Dictionary<string, object>>();
            bool expired = Json.List(quota, "windows").Any(w => (Json.Number(w, "resets_at") ?? 0) * 1000 <= now);
            quotaNotice = expired ? "额度窗口已重置 · 等待同步" :
                Json.Text(quota, "state") == "loading" || quota == null ? "正在同步账户额度" : "账户额度暂不可用";
            quotaSyncText = quotaCached ? "上次数据 · 等待重新同步" :
                age < 60000 ? "刚刚同步 · 每分钟更新" : "1 分钟前同步";
        }

        private static string QuotaLabel(Dictionary<string, object> window)
        {
            double minutes = Json.Number(window, "window_minutes") ?? 0;
            return minutes == 10080 ? "周额度剩余" : minutes == 300 ? "5 小时额度剩余" :
                minutes == 1440 ? "日额度剩余" : (minutes / 60).ToString("0.#") + " 小时额度剩余";
        }

        private static string QuotaReset(Dictionary<string, object> window)
        {
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(Json.Number(window, "resets_at") ?? 0).ToLocalTime().ToString("MM-dd HH:mm") + " 重置"; }
            catch { return "重置时间未知"; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (configuration.CornerMode) { PaintCorner(e.Graphics); return; }
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(68, 91, 109))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            int x = (int)(13 * scale);
            int dot = (int)(7 * scale);
            using (var brush = new SolidBrush(fresh ? Color.FromArgb(87, 225, 196) : Color.FromArgb(148, 172, 191))) e.Graphics.FillEllipse(brush, x, (Height - dot) / 2, dot, dot);
            x += (int)(15 * scale);
            DrawPart(e.Graphics, modelText, smallFont, Color.FromArgb(194, 207, 221), ref x, Math.Min((int)(190 * scale), Width / 5));
            Divider(e.Graphics, ref x);
            DrawPart(e.Graphics, stageText.StartsWith("区间平均", StringComparison.Ordinal) ? "均速" : "速率", labelFont, Color.FromArgb(220, 228, 236), ref x, 0);
            DrawPart(e.Graphics, rateText, speedFont, Color.FromArgb(87, 225, 196), ref x, 0);
            DrawPart(e.Graphics, "tok/s", smallFont, Color.FromArgb(187, 205, 219), ref x, 0);
            int cacheRoom = TextRenderer.MeasureText(e.Graphics, cacheText, labelFont).Width + (int)(22 * scale);
            int ageRoom = TextRenderer.MeasureText(e.Graphics, freshnessText, smallFont).Width + (int)(20 * scale);
            int reserved = cacheRoom + ageRoom + (int)(105 * scale);
            if (Width - x > reserved + (int)(410 * scale)) DrawPart(e.Graphics, stageText, smallFont, Color.FromArgb(148, 172, 191), ref x, (int)(105 * scale));
            if (Width - x > reserved + (int)(265 * scale))
            {
                Divider(e.Graphics, ref x);
                DrawPart(e.Graphics, weeklyText, smallFont, Color.FromArgb(146, 165, 180), ref x, (int)(215 * scale));
            }
            Divider(e.Graphics, ref x);
            int cacheWidth = Math.Max(0, Width - x - ageRoom - (int)(80 * scale));
            if (cacheWidth > 0) DrawPart(e.Graphics, cacheText, labelFont, Color.FromArgb(169, 204, 255), ref x, cacheWidth);
            Divider(e.Graphics, ref x);
            DrawPart(e.Graphics, freshnessText, smallFont, fresh ? Color.FromArgb(148, 172, 191) : Color.FromArgb(240, 195, 125), ref x, Math.Max(1, Width - x - (int)(60 * scale)));
            TextRenderer.DrawText(e.Graphics, "×", labelFont, closeRect, Color.FromArgb(168, 187, 201), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, "?", labelFont, infoRect, Color.FromArgb(168, 187, 201), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var grip = new Pen(Color.FromArgb(117, 143, 163)))
                for (int n = 1; n <= 3; n++) e.Graphics.DrawLine(grip, Width - (int)((3 + n * 3) * scale), Height - (int)(3 * scale),
                    Width - (int)(3 * scale), Height - (int)((3 + n * 3) * scale));
        }

        private Color QuotaColor(double remaining)
        {
            return quotaCached ? Color.FromArgb(163, 180, 199) : remaining <= 10 ? Color.FromArgb(255, 143, 151) :
                remaining <= 25 ? Color.FromArgb(244, 198, 128) : Color.FromArgb(121, 237, 203);
        }

        private static void PaintQuotaBar(Graphics graphics, float x, float y, float width, double remaining, Color color)
        {
            using (var path = RoundedCard(new RectangleF(x, y, width, 5), 2.5f))
            using (var track = new SolidBrush(Color.FromArgb(49, 65, 85))) graphics.FillPath(track, path);
            if (remaining <= 0) return;
            float filled = Math.Max(5, width * (float)(remaining / 100));
            using (var path = RoundedCard(new RectangleF(x, y, filled, 5), 2.5f))
            using (var fill = new SolidBrush(color)) graphics.FillPath(fill, path);
        }

        private static void CornerText(Graphics graphics, string text, Font font, Color color, RectangleF bounds, bool right)
        {
            CornerText(graphics, text, font, color, bounds, right ? StringAlignment.Far : StringAlignment.Near);
        }

        private static void CornerText(Graphics graphics, string text, Font font, Color color, RectangleF bounds, StringAlignment alignment)
        {
            using (var brush = new SolidBrush(color))
            using (var format = StringFormat.GenericTypographic)
            {
                format.Alignment = alignment;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                graphics.DrawString(text ?? "", font, brush, bounds, format);
            }
        }

        private void DrawPart(Graphics graphics, string text, Font font, Color color, ref int x, int maximum)
        {
            int wanted = TextRenderer.MeasureText(graphics, text, font, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + (int)(7 * scale);
            int width = maximum > 0 ? Math.Min(wanted, maximum) : wanted;
            width = Math.Min(width, Math.Max(0, Width - x - (int)(60 * scale)));
            if (width > 0) TextRenderer.DrawText(graphics, text, font, new Rectangle(x, 0, width, Height), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            x += width;
        }
        private void Divider(Graphics graphics, ref int x)
        {
            x += (int)(9 * scale);
            using (var pen = new Pen(Color.FromArgb(66, 84, 99))) graphics.DrawLine(pen, x, (int)(11 * scale), x, Height - (int)(11 * scale));
            x += (int)(12 * scale);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (configuration.CornerMode) return;
            if (e.Button != MouseButtons.Left) return;
            if (closeRect.Contains(e.Location)) Close();
            else if (infoRect.Contains(e.Location)) tooltip.Show(helpText, this, Math.Max(0, Width - (int)(520 * scale)), -(int)(180 * scale), 20000);
        }
        protected override void WndProc(ref Message m)
        {
            const int hitTest = 0x0084, calcSize = 0x0083, enterSizeMove = 0x0231, exitSizeMove = 0x0232;
            if (m.Msg == calcSize && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == hitTest)
            {
                if (configuration.CornerMode) { m.Result = new IntPtr(cornerClickThrough ? -1 : 1); return; }
                long coordinates = m.LParam.ToInt64();
                var point = PointToClient(new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff)));
                int edge = Math.Max(5, (int)(6 * scale));
                bool left = point.X < edge, right = point.X >= Width - edge;
                bool top = point.Y < edge, bottom = point.Y >= Height - edge;
                int hit = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 :
                    left ? 10 : right ? 11 : top ? 12 : bottom ? 15 :
                    closeRect.Contains(point) || infoRect.Contains(point) ? 1 : 2;
                m.Result = new IntPtr(hit);
                return;
            }
            if (m.Msg == enterSizeMove) { manualLayout = true; sizingOrMoving = true; }
            if (m.Msg == 0x00a5) { BuildMenu(); menu.Show(Cursor.Position); m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x00a3) { m.Result = IntPtr.Zero; return; } // Double-clicking does not maximize a status strip.
            base.WndProc(ref m);
            if (m.Msg == exitSizeMove) { sizingOrMoving = false; SaveLayout(); }
        }

        private void SaveLayout()
        {
            if (configuration.CornerMode) return;
            if (AwaitingView) { waitingBounds = Bounds; return; }
            if (!manualLayout || view == null || !Native.IsWindow(view.Handle)) return;
            Native.Point origin = new Native.Point();
            if (!Native.ClientToScreen(view.Handle, ref origin)) return;
            double dpiScale = Native.Scale(string.IsNullOrEmpty(manualBindingId) ? view.Handle : Handle);
            relativeX = (Left - origin.X) / dpiScale;
            relativeY = (Top - origin.Y) / dpiScale;
            layoutWidth = Width / dpiScale;
            layoutHeight = Height / dpiScale;
            Json.Write(LayoutPath, new Dictionary<string, object> {
                {"mode", "manual"}, {"x", relativeX}, {"y", relativeY}, {"width", layoutWidth}, {"height", layoutHeight} });
        }

        private void ResetLayout()
        {
            manualLayout = false;
            waitingBounds = Rectangle.Empty;
            Json.Write(LayoutPath, new Dictionary<string, object> {{"mode", "auto"}});
            PositionHud();
        }
        private bool IsStartupEnabled()
        {
            var saved = Json.Read(Path.Combine(configuration.DataDirectory, "app-follow", "config.json"));
            return Json.Text(saved, "enabled").Equals("True", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Json.Text(saved, "installation"), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        private void SetStartup(bool enabled)
        {
            configuration.ConfigureAppFollowing(enabled);
        }

        private void BuildMenu()
        {
            while (menu.Items.Count > 0)
            {
                var item = menu.Items[0];
                menu.Items.RemoveAt(0);
                item.Dispose();
            }
            if (configuration.CornerMode)
            {
                var location = menu.Items.Add("副屏右上角 · 常驻置顶");
                location.Enabled = false;
                var passthrough = menu.Items.Add("鼠标穿透（临时关闭 30 秒）") as ToolStripMenuItem;
                passthrough.Checked = cornerClickThrough;
                passthrough.Click += delegate { SetCornerClickThrough(!cornerClickThrough); };
                var opacityMenu = menu.Items.Add("不透明度") as ToolStripMenuItem;
                foreach (int percentage in new[] { 60, 78, 90, 100 })
                {
                    int chosen = percentage;
                    var opacityItem = opacityMenu.DropDownItems.Add(percentage + "%" + (percentage == 78 ? "（默认）" : "")) as ToolStripMenuItem;
                    opacityItem.Checked = Math.Abs(Opacity - percentage / 100.0) < 0.01;
                    opacityItem.Click += delegate { Opacity = configuration.CornerOpacity = chosen / 100.0; SetCornerClickThrough(cornerClickThrough); configuration.SaveCornerSettings(); };
                }
                var motionMenu = menu.Items.Add("面板动效") as ToolStripMenuItem;
                foreach (string mode in new[] { "on", "system", "off" })
                {
                    string chosen = mode;
                    var item = motionMenu.DropDownItems.Add(mode == "on" ? "完整动效" : mode == "off" ? "静态显示" : "跟随系统") as ToolStripMenuItem;
                    item.Checked = configuration.CornerMotion == mode;
                    item.Click += delegate { SetCornerMotion(chosen); };
                }
                var monitors = menu.Items.Add("显示屏幕") as ToolStripMenuItem;
                foreach (var screen in Screen.AllScreens)
                {
                    string device = screen.DeviceName;
                    var monitorItem = monitors.DropDownItems.Add((screen.Primary ? "主屏" : "副屏") + " · " + device.Replace("\\\\.\\", "")) as ToolStripMenuItem;
                    monitorItem.Checked = device == cornerScreen;
                    monitorItem.Click += delegate { configuration.CornerMonitor = device; configuration.SaveCornerSettings(); PositionHud(); };
                }
                menu.Items.Add("恢复副屏右上角").Click += delegate {
                    configuration.CornerMonitor = ""; configuration.SaveCornerSettings(); SetCornerClickThrough(true); PositionHud();
                };
                var resetAlerts = menu.Items.Add("重置排期通知") as ToolStripMenuItem;
                resetAlerts.Checked = configuration.ResetAlerts;
                resetAlerts.Click += delegate { configuration.ResetAlerts = !configuration.ResetAlerts; configuration.SaveCornerSettings(); };
                if (resetRadar != null) { var radarInfo = menu.Items.Add(resetRadar.Hint); radarInfo.Enabled = false; }
                menu.Items.Add("查看重置雷达来源").Click += delegate {
                    Process.Start(new ProcessStartInfo("https://didcodexreset.com/zh.html") { UseShellExecute = true });
                };
                menu.Items.Add(new ToolStripSeparator());
            }
            var heading = menu.Items.Add((string.IsNullOrEmpty(manualBindingId) ? "当前：" : "固定：") + (string.IsNullOrEmpty(selectedTitle) ? "未识别对话" : selectedTitle));
            heading.Enabled = false;
            var show = menu.Items.Add("显示状态栏");
            show.Enabled = !Displayed;
            show.Click += delegate { RequestShow(); };
            var candidates = Json.List(data, "matches").ToList();
            if (candidates.Count > 1)
            {
                foreach (var row in candidates)
                {
                    string id = Json.Text(row, "id");
                    string label = Json.Text(row, "model") + " · " + id.Substring(Math.Max(0, id.Length - 8));
                    var item = menu.Items.Add("绑定 " + label) as ToolStripMenuItem;
                    item.Checked = pinnedId == id;
                    item.Click += delegate { pinnedId = id; previousSelection = ""; UpdateView(view); };
                }
            }
            var refresh = menu.Items.Add("立即刷新数据");
            refresh.Click += delegate { RequestRefresh(); };
            menu.Items.Add("区间统计：按时间或首尾消息…").Click += delegate { OpenDashboard(); };
            menu.Items.Add("按对话 ID 固定状态栏…").Click += delegate { OpenDashboard(); };
            if (!string.IsNullOrEmpty(manualBindingId))
                menu.Items.Add("解除固定，自动跟随桌面对话").Click += delegate { Unpin(); };
            if (Json.Object(data, "range") != null)
                menu.Items.Add("返回最近响应统计").Click += delegate {
                    Json.Write(Path.Combine(runtime, "range-result.json"), new Dictionary<string, object>());
                };
            if (!configuration.CornerMode) menu.Items.Add("恢复底部默认位置和大小").Click += delegate { ResetLayout(); };
            var startup = menu.Items.Add(configuration.CornerMode ? "登录后常驻显示" : "随 Codex / CLI / IDE 启动") as ToolStripMenuItem;
            startup.Checked = IsStartupEnabled();
            startup.Enabled = !startupChanging;
            startup.Click += delegate {
                bool enabled = !IsStartupEnabled();
                startupChanging = true;
                startup.Enabled = false;
                Task.Run(delegate { SetStartup(enabled); }).ContinueWith(task => {
                    startupChanging = false;
                    if (task.IsFaulted && !IsDisposed)
                        try { BeginInvoke(new Action(delegate { MessageBox.Show("无法修改随应用启动：" + task.Exception.GetBaseException().Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); })); } catch { }
                });
            };
            var readme = menu.Items.Add("打开使用说明");
            readme.Click += delegate { Process.Start(Path.Combine(folder, "README.zh-CN.md")); };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出状态条").Click += delegate { Close(); };
        }

        internal void CreateShortcut(string path)
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = Application.ExecutablePath;
            link.WorkingDirectory = folder;
            link.Description = "在 Codex 窗口底部显示当前对话的 token 速率和缓存命中率，支持拖动和缩放";
            link.WindowStyle = 1;
            link.Save();
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            stopping = true;
            SaveLayout();
            ticker.Stop();
            motionTicker.Stop();
            motionTicker.Dispose();
            tray.Visible = false;
            try { if (backend != null && !backend.HasExited) backend.Kill(); } catch { }
            smallFont.Dispose(); labelFont.Dispose(); speedFont.Dispose(); tooltip.Dispose(); tray.Dispose();
            cornerHeaderFont.Dispose(); cornerSmallFont.Dispose(); cornerSpeedFont.Dispose(); cornerCacheFont.Dispose(); cornerQuotaFont.Dispose();
            cornerTimerFont.Dispose(); cornerNumberFont.Dispose(); cornerValueFont.Dispose(); cornerTitleFont.Dispose();
            cornerRadarFont.Dispose();
            cornerHeroFont.Dispose(); cornerBrandFont.Dispose(); cornerMicroFont.Dispose();
            if (cornerBackdrop != null) cornerBackdrop.Dispose();
            if (cornerBlueLight != null) cornerBlueLight.Dispose();
            if (cornerMintLight != null) cornerMintLight.Dispose();
            base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Application.ExitThread();
        }
    }

    // Observe exceptions without replacing WinForms' error handling or swallowing failures.
    internal static class HudDiagnostics
    {
        internal const int MaximumLogBytes = 256 * 1024;
        private static readonly object Gate = new object();
        private static string directory;
        private static DateTime nextResourceError;
        [ThreadStatic] private static bool recording;
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);

        internal static Dictionary<string, object> Resources()
        {
            using (var process = Process.GetCurrentProcess())
                return new Dictionary<string, object> {
                    {"pid", process.Id}, {"process_bits", IntPtr.Size * 8},
                    {"private_bytes", process.PrivateMemorySize64}, {"working_set_bytes", process.WorkingSet64},
                    {"handles", process.HandleCount}, {"gdi_objects", GetGuiResources(process.Handle, 0)},
                    {"user_objects", GetGuiResources(process.Handle, 1)} };
        }

        internal static void Install(string runtime)
        {
            directory = runtime;
            AppDomain.CurrentDomain.FirstChanceException += delegate(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) {
                if (e.Exception is OutOfMemoryException) Record(e.Exception, "allocation", true);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
                Record(e.ExceptionObject as Exception, "unhandled", false);
            };
        }

        internal static void Record(Exception error, string origin, bool throttle)
        {
            if (recording || directory == null || !Monitor.TryEnter(Gate)) return;
            recording = true;
            try
            {
                if (throttle && DateTime.UtcNow < nextResourceError) return;
                nextResourceError = DateTime.UtcNow.AddMinutes(1);
                var entry = Resources();
                entry["at_utc"] = DateTime.UtcNow.ToString("o");
                entry["version"] = Application.ProductVersion;
                entry["origin"] = origin;
                // First-chance notifications run at the throw site, before Exception.StackTrace
                // has unwound through its callers. Capture that live call chain as well.
                string currentStack = new StackTrace(1, false).ToString();
                entry["observer_stack"] = currentStack.Substring(0, Math.Min(currentStack.Length, 12000));
                // Stack/type only: no exception messages, conversation data or credentials.
                var chain = new List<object>();
                for (int i = 0; error != null && i < 6; i++, error = error.InnerException)
                {
                    string stack = error.StackTrace ?? "";
                    chain.Add(new Dictionary<string, object> {
                        {"type", error.GetType().FullName}, {"hresult", error.HResult},
                        {"stack", stack.Substring(0, Math.Min(stack.Length, 8000))} });
                }
                entry["exceptions"] = chain;
                string line = new JavaScriptSerializer().Serialize(entry) + Environment.NewLine;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "hud-errors.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaximumLogBytes)
                {
                    string previous = path + ".1";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch { /* Diagnostics must not cause a second application failure. */ }
            finally { recording = false; Monitor.Exit(Gate); }
        }
    }

    internal static class Program
    {
        [STAThread] private static int Main(string[] args)
        {
            RuntimeConfiguration configuration;
            try
            {
                int runtimeIndex = Array.IndexOf(args, "--runtime");
                if (runtimeIndex >= 0 && runtimeIndex + 1 < args.Length)
                {
                    string requested = Path.GetFullPath(args[runtimeIndex + 1]);
                    if (Path.GetFileName(requested) != "runtime") throw new ArgumentException("Windows HUD runtime must end with runtime.");
                    Environment.SetEnvironmentVariable("CODEX_TOKEN_HUD_HOME", Path.GetDirectoryName(requested));
                }
                configuration = RuntimeConfiguration.Load(AppDomain.CurrentDomain.BaseDirectory);
                int homeIndex = Array.IndexOf(args, "--codex-home");
                if (homeIndex >= 0 && homeIndex + 1 < args.Length) configuration.CodexHome = Path.GetFullPath(args[homeIndex + 1]);
                string pythonVersion = configuration.CheckPython();
                if (args.Length == 2 && args[0] == "--check")
                {
                    Json.Write(Path.GetFullPath(args[1]), new Dictionary<string, object> {
                        {"ok", true}, {"python_version", pythonVersion},
                        {"bundled_python", configuration.Pythonw.StartsWith(Path.Combine(configuration.Folder, "python"), StringComparison.OrdinalIgnoreCase)},
                        {"app_version", Application.ProductVersion} });
                    return 0;
                }
                configuration.PrepareData();
                if (args.Contains("--enable-app-follow") || args.Contains("--disable-app-follow"))
                {
                    configuration.ConfigureAppFollowing(args.Contains("--enable-app-follow"));
                    return 0;
                }
                if (args.Length == 0 && configuration.HasLegacyLogonStartup())
                {
                    configuration.ConfigureAppFollowing(true);
                    return 0; // The replacement watcher decides whether a client is running.
                }
            }
            catch (Exception error)
            {
                if (args.Length == 2 && args[0] == "--check")
                    Json.Write(Path.GetFullPath(args[1]), new Dictionary<string, object> {{"ok", false}, {"error", error.Message}});
                else if (args.Contains("--enable-app-follow") || args.Contains("--disable-app-follow"))
                    Console.Error.WriteLine(error.Message);
                else MessageBox.Show(error.Message, "Codex Token HUD", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            bool owner;
            using (var singleton = new Mutex(true, "Local\\CodexTokenHud-v1-" + Environment.UserName, out owner))
            {
                if (!owner)
                {
                    Json.Write(Path.Combine(configuration.Runtime, "show.request.json"), new Dictionary<string, object> {
                        {"requested_at", DateTime.UtcNow.ToString("o")} });
                    return 0;
                }
                HudDiagnostics.Install(configuration.Runtime);
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var hud = new Hud(configuration);
                int ownerIndex = Array.IndexOf(args, "--follow-owner");
                if (ownerIndex >= 0 && ownerIndex + 1 < args.Length) hud.FollowOwner = args[ownerIndex + 1];
                hud.StartMonitoring();
                Application.Run();
                singleton.ReleaseMutex();
            }
            return 0;
        }
    }
}

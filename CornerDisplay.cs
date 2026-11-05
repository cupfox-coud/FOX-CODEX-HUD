using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexTokenHud
{
    internal struct MemorySnapshot
    {
        internal ulong CommitLimit, CommitAvailable, PhysicalAvailable;
    }

    // System-wide commit headroom matters even when this small process uses little RAM.
    internal sealed class RenderMemoryGuard
    {
        private const ulong GiB = 1024UL * 1024 * 1024;
        private double healthySince = -1;
        internal bool Reduced { get; private set; }
        internal MemorySnapshot Last;

        [StructLayout(LayoutKind.Sequential)]
        private struct PerformanceInformation
        {
            internal uint Size;
            internal UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            internal uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

#if HUD_TEST
        internal static MemorySnapshot? TestSnapshot;
#endif
        internal static bool TryRead(out MemorySnapshot result)
        {
#if HUD_TEST
            if (TestSnapshot.HasValue) { result = TestSnapshot.Value; return true; }
#endif
            result = new MemorySnapshot();
            var info = new PerformanceInformation();
            info.Size = (uint)Marshal.SizeOf(typeof(PerformanceInformation));
            try
            {
                if (!GetPerformanceInfo(ref info, info.Size)) return false;
                ulong page = info.PageSize.ToUInt64();
                ulong limit = info.CommitLimit.ToUInt64(), used = info.CommitTotal.ToUInt64();
                if (page == 0 || limit == 0) return false;
                checked {
                    result.CommitLimit = limit * page;
                    result.CommitAvailable = (limit > used ? limit - used : 0) * page;
                    result.PhysicalAvailable = info.PhysicalAvailable.ToUInt64() * page;
                }
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (OverflowException) { return false; }
        }

        internal bool Sample(MemorySnapshot value, double now)
        {
            Last = value;
            bool before = Reduced;
            ulong enter = Math.Max(4 * GiB, value.CommitLimit / 20);
            ulong recover = Math.Max(6 * GiB, value.CommitLimit / 12);
            if (value.CommitAvailable <= enter || value.PhysicalAvailable <= GiB / 2)
            { Reduced = true; healthySince = -1; }
            else if (Reduced)
            {
                if (value.CommitAvailable >= recover && value.PhysicalAvailable >= 3 * GiB / 2)
                {
                    if (healthySince < 0) healthySince = now;
                    if (now - healthySince >= 30000) { Reduced = false; healthySince = -1; }
                }
                else healthySince = -1;
            }
            return before != Reduced;
        }

        internal bool AllocationFailed()
        {
            bool changed = !Reduced;
            Reduced = true; healthySince = -1;
            return changed;
        }
    }

    internal sealed class TodayQuotaDisplay
    {
        internal string State = "unavailable", Text = "等待额度", Label = "今日建议余量";
        internal double? Remaining, Spent;
        internal bool Cached;
        internal bool OverBudget { get { return Remaining.HasValue && Remaining.Value < 0; } }

        internal static TodayQuotaDisplay Read(Dictionary<string, object> value, double? balance,
            bool cached, double ageMs, double nowMs)
        {
            var result = new TodayQuotaDisplay();
            if (!balance.HasValue || double.IsNaN(balance.Value) || double.IsInfinity(balance.Value) || balance < 0 || balance > 100)
                return result;
            result.State = "stale"; result.Text = "等待同步";
            if (double.IsNaN(ageMs) || ageMs < -30000 || ageMs > 180000 || double.IsNaN(nowMs) || double.IsInfinity(nowMs)) return result;
            double? amount = QuotaRunwayDisplay.Number(value, "remaining_percent"),
                allocated = QuotaRunwayDisplay.Number(value, "allocated_percent"),
                spent = QuotaRunwayDisplay.Number(value, "spent_percent"),
                start = QuotaRunwayDisplay.Number(value, "day_start_ms"),
                end = QuotaRunwayDisplay.Number(value, "day_end_ms"),
                anchor = QuotaRunwayDisplay.Number(value, "anchor_at_ms"),
                until = QuotaRunwayDisplay.Number(value, "valid_until_ms");
            string state = Json.Text(value, "state");
            if (!amount.HasValue || !allocated.HasValue || !spent.HasValue || !start.HasValue ||
                !end.HasValue || !anchor.HasValue || !until.HasValue ||
                allocated < 0 || allocated > 100 || spent < 0 || spent > 100 || amount < -100 || amount > balance ||
                Math.Abs(amount.Value - Math.Min(balance.Value, allocated.Value - spent.Value)) > 0.000001 ||
                start > nowMs || end - start < 20 * 3600000 || end - start > 27 * 3600000 ||
                anchor < start || anchor > nowMs + 30000 || until <= anchor || until > end || nowMs >= until ||
                (amount < 0 ? state != "over_budget" : amount > 0 ? state != "ok" : state != "spent")) return result;
            result.Remaining = amount; result.Spent = spent; result.Cached = cached;
            result.State = cached ? "cached" : state;
            result.Label = cached ? (result.OverBudget ? "今日超额·暂存" : "今日建议·暂存") :
                result.OverBudget ? "今日余量·超额" : "今日建议余量";
            double shown = Math.Floor(Math.Abs(amount.Value) * 10 + 0.000000001) / 10;
            result.Text = (result.OverBudget ? "−" : "") +
                (amount != 0 && shown == 0 ? "<0.1%" : shown.ToString("0.#") + "%");
            return result;
        }
    }

    internal sealed class QuotaRunwayDisplay
    {
        internal string State = "observing", Text = "观察中", Basis = "近期观察中";
        internal string ForecastState = "observing";
        internal double? Hours, ResetHours, Fraction, GapHours, RatePerDay, AllowancePerDay, BaselinePerDay, Consumed;
        internal bool HasTimeline, GapUnder15Minutes, Provisional;

        internal static double? Number(Dictionary<string, object> value, string key)
        {
            object raw = Json.Get(value, key);
            if (!(raw is int || raw is long || raw is float || raw is double || raw is decimal)) return null;
            double number;
            try { number = Convert.ToDouble(raw); } catch { return null; }
            return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
        }

        private static QuotaRunwayDisplay WaitingForSync()
        { return new QuotaRunwayDisplay { State = "stale", Text = "等待同步", Basis = "估算暂停" }; }

        internal static string Duration(double hours)
        {
            int minutes = (int)Math.Round(hours * 60);
            if (minutes < 15) return "不足15分";
            int days = minutes / 1440, hour = (minutes % 1440) / 60, minute = minutes % 60;
            return days > 0 ? days + "天" + (hour > 0 ? hour + "小时" : "") :
                hour > 0 ? hour + "小时" + (minute > 0 ? minute + "分" : "") : minutes + "分钟";
        }

        internal static QuotaRunwayDisplay Read(Dictionary<string, object> value, bool hasWeekly, bool cached,
            double ageMs = double.PositiveInfinity)
        {
            var result = new QuotaRunwayDisplay();
            if (!hasWeekly) { result.State = "unavailable"; result.Text = "等待额度"; result.Basis = ""; return result; }
            if (cached && (double.IsNaN(ageMs) || ageMs < -30000 || ageMs > 180000)) return WaitingForSync();
            string state = Json.Text(value, "state");
            double? resetHours = Number(value, "reset_hours");
            if (resetHours.HasValue && resetHours.Value >= 0 && resetHours.Value <= 168) result.ResetHours = resetHours;
            double? baseline = Number(value, "baseline_pct_per_day"), allowance = Number(value, "allowance_pct_per_day");
            if (baseline.HasValue && baseline.Value > 0 && baseline.Value <= 100) result.BaselinePerDay = baseline;
            if (allowance.HasValue && allowance.Value >= 0 && allowance.Value <= 10000) result.AllowancePerDay = allowance;
            result.ForecastState = state;
            var consumed = Number(value, "consumed_since_reset_percent");
            if (consumed.HasValue && consumed >= 0 && consumed <= 100) result.Consumed = consumed;
            result.Provisional = Json.Get(value, "provisional") is bool && (bool)Json.Get(value, "provisional");
            double? liveRate = Number(value, "rate_pct_per_day");
            if (Json.Text(value, "method") == "weekly_cycle_v4" && liveRate.HasValue && liveRate >= 0 && liveRate <= 100000)
                result.RatePerDay = liveRate;
            if (state == "observing" && Json.Text(value, "method") == "weekly_cycle_v4")
            { result.Text = liveRate == 0 ? "新周期" : "积累样本"; result.Basis = "初期均耗"; }
            if (state == "exhausted")
            { result.State = cached ? "cached" : state; result.Text = "已耗尽"; result.Basis = cached ? "上次额度" : "当前额度"; result.HasTimeline = result.ResetHours.HasValue; return result; }
            if (state != "estimated" && state != "through_reset") return cached ? WaitingForSync() : result;
            double? span = Number(value, "span_hours");
            string basis = Json.Text(value, "basis");
            bool realtime = Json.Text(value, "method") == "weekly_cycle_v4";
            if ((!realtime && Json.Text(value, "method") != "weekly_cycle_v3") || (basis != "cycle" && basis != "after_refill") ||
                !span.HasValue || span.Value < (realtime ? .25 : 6) || span.Value > 168.01) return cached ? WaitingForSync() : result;
            result.Basis = cached ? "上次估算" : basis == "cycle" ? "周期均耗" : "补额后均耗";
            double? fraction = Number(value, "runway_fraction"), gap = Number(value, "gap_hours"), rate = Number(value, "rate_pct_per_day");
            if (result.ResetHours.HasValue && fraction.HasValue && fraction.Value >= 0 && fraction.Value <= 1 &&
                gap.HasValue && gap.Value >= 0 && gap.Value <= 168 && rate.HasValue && rate.Value > 0 && rate.Value <= (realtime ? 100000 : 400) &&
                (state == "estimated" ? fraction.Value < 1 : fraction.Value == 1 && gap.Value == 0))
            { result.Fraction = fraction; result.GapHours = gap; result.RatePerDay = rate; result.HasTimeline = true;
              result.GapUnder15Minutes = Json.Get(value, "gap_under_15_minutes") is bool && (bool)Json.Get(value, "gap_under_15_minutes"); }
            if (state == "through_reset")
            { result.State = cached ? "cached" : state; result.Text = "至周重置"; return result; }
            double? hours = Number(value, "hours");
            if (!hours.HasValue || hours.Value < 0 || hours.Value > 168) return cached ? WaitingForSync() : new QuotaRunwayDisplay();
            result.State = cached ? "cached" : state; result.Hours = hours;
            result.Text = Duration(hours.Value);
            return result;
        }
    }

    internal sealed class QuotaTimeTick
    {
        internal double Fraction;
        internal string Label;
    }

    internal sealed class QuotaWeekScale
    {
        internal bool Available, Cached, Projected, ThroughReset, Exhausted;
        internal double Current, End;
        internal readonly List<QuotaTimeTick> Ticks = new List<QuotaTimeTick>();
        internal string ResetText = "等待账户同步", EndLabel = "", State = "unavailable";
        private const double WeekMs = 7 * 86400000.0;
        internal double FutureCoverage
        {
            get { return !Available || (!Projected && !Exhausted) || Current >= 1 ? 0 :
                Math.Max(0, Math.Min(1, (End - Current) / (1 - Current))); }
        }

        internal static QuotaWeekScale Read(Dictionary<string, object> window, QuotaRunwayDisplay view,
            double? calculatedAt, double snapshotAt, double now, bool cached)
        {
            var result = new QuotaWeekScale { Cached = cached };
            double? reset = QuotaRunwayDisplay.Number(window, "resets_at");
            if (QuotaRunwayDisplay.Number(window, "window_minutes") != 10080 || !reset.HasValue ||
                double.IsNaN(now) || double.IsInfinity(now)) return result;
            double end = reset.Value * 1000;
            if (end <= now || end > now + WeekMs + 120000) return result;
            double at = cached && !double.IsNaN(snapshotAt) && !double.IsInfinity(snapshotAt) &&
                snapshotAt <= now + 30000 && now - snapshotAt <= 900000 ? snapshotAt : now;
            result.Current = Math.Max(0, Math.Min(1, (at - (end - WeekMs)) / WeekMs));
            try
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                result.ResetText = epoch.AddMilliseconds(end).ToLocalTime().ToString("MM/dd HH:mm") + " 重置";
                double hours = (end - at) / 3600000;
                DateTime start = epoch.AddMilliseconds(at).ToLocalTime();
                // Calendar midnights for multi-day windows, clock ticks for short windows.
                // Normalize UTC durations so a local DST transition does not distort placement.
                bool days = hours > 36;
                int stepHours = hours > 12 ? 6 : hours > 4 ? 2 : 1;
                DateTime tick = days ? start.Date.AddDays(1) : start.Date.AddHours((Math.Floor(start.Hour / (double)stepHours) + 1) * stepHours);
                for (int i = 0; i < 10; i++, tick = days ? tick.AddDays(1) : tick.AddHours(stepHours))
                {
                    double stamp = (tick.ToUniversalTime() - epoch).TotalMilliseconds;
                    if (stamp >= end) break;
                    if (stamp <= at) continue;
                    result.Ticks.Add(new QuotaTimeTick { Fraction = (stamp - at) / (end - at), Label = tick.ToString(days ? "MM/dd" : "HH:mm") });
                }
            }
            catch (ArgumentOutOfRangeException) { return result; }
            result.Available = true; result.State = cached ? "cached" : "observing";
            if (view.State == "stale" || view.State == "unavailable") return result;
            result.Exhausted = view.ForecastState == "exhausted";
            if (result.Exhausted) { result.End = result.Current; result.State = "exhausted"; return result; }
            if (!view.HasTimeline) return result;
            if (view.ForecastState == "through_reset")
            { result.Projected = result.ThroughReset = true; result.End = 1; result.State = cached ? "cached" : "through_reset"; return result; }
            if (view.ForecastState != "estimated" || !view.Hours.HasValue || !calculatedAt.HasValue ||
                double.IsNaN(calculatedAt.Value) || double.IsInfinity(calculatedAt.Value) ||
                calculatedAt > now + 30000 || now - calculatedAt > 14 * 60000) return result;
            double predicted = calculatedAt.Value + view.Hours.Value * 3600000;
            // A rounded '<15 min' duration is an interval, not an exact calendar deadline.
            if (view.Hours.Value == 0) { predicted = at; result.EndLabel = "不足15分内用尽"; }
            else
            {
                if (predicted < at || (predicted > end && view.GapHours != 0)) return result;
                bool nearReset = predicted > end;
                predicted = Math.Min(predicted, end);
                try { result.EndLabel = nearReset ? "临近重置用尽" : "约" + new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMilliseconds(predicted).ToLocalTime().ToString("MM/dd HH'时'") + "用尽"; }
                catch (ArgumentOutOfRangeException) { return result; }
            }
            result.End = Math.Max(result.Current, Math.Min(1, (predicted - (end - WeekMs)) / WeekMs));
            result.Projected = true; result.State = cached ? "cached" : "estimated";
            return result;
        }


    }

    internal sealed partial class Hud
    {
        private const int CornerWidth = 400, CornerHeight = 738;
        private ActivityDisplay activity = new ActivityDisplay();
        private readonly Font cornerTimerFont = new Font("Consolas", 29, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerNumberFont = new Font("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerValueFont = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerTitleFont = new Font("Microsoft YaHei UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerRadarFont = new Font("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerHeroFont = new Font("Segoe UI Semilight", 48, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font cornerBrandFont = new Font("Segoe UI Semibold", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font cornerMicroFont = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font cornerRunwayFont = new Font("Microsoft YaHei UI", 21, FontStyle.Bold, GraphicsUnit.Pixel);
        private Bitmap cornerBackdrop;
        private Bitmap cornerBlueLight, cornerMintLight;
        private double? previewMotionClock = null;
        private static readonly Color Ink = Color.FromArgb(238, 242, 248);
        private static readonly Color Muted = Color.FromArgb(182, 193, 208);
        private static readonly Color Mint = Color.FromArgb(115, 230, 195);
        private static readonly Color Blue = Color.FromArgb(143, 190, 255);
        private static readonly Color Amber = Color.FromArgb(244, 204, 118);
        private static readonly Color Orange = Color.FromArgb(255, 164, 106);
        private static readonly Color Rose = Color.FromArgb(255, 124, 145);
        private static readonly Color DebtBlack = Color.FromArgb(9, 11, 15);
        private static readonly Color DebtMetal = Color.FromArgb(226, 190, 126);
        private static readonly Color DebtText = Color.FromArgb(255, 238, 208);
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
        private static bool AnimationEnabled()
        { int enabled; return SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0; }
        private readonly System.Windows.Forms.Timer motionTicker = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer hoverTicker = new System.Windows.Forms.Timer();
        private bool hoverDimmed;
        private int hoverAppliedAlpha = -1;
        private double hoverOpacity, hoverFrom, hoverTarget, hoverChangedAt;
        private bool motionAllowed = true;
        private bool systemMotionAllowed = true;
        private long motionPaintCount;
        private double nextMotionPreference;
        private readonly RenderMemoryGuard renderMemory = new RenderMemoryGuard();
        private double nextMemoryCheck, emergencyPaintUntil;
#if HUD_TEST
        internal Action TestCornerRender;
        internal Func<Native.Point?> TestHoverCursor;
#endif
        private readonly NodeMotion aiMotion = new NodeMotion();
        private readonly NodeMotion generalMotion = new NodeMotion();
        private readonly ResponseMotion responseMotion = new ResponseMotion();
        private readonly RadarMotion radarMotion = new RadarMotion();
        private double MotionMs { get { return previewMotionClock ?? System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency; } }

        // One lightweight clock; animation never reads files or triggers probes.
        private void StartCornerMotion()
        {
            if (!configuration.CornerMode) return;
            motionTicker.Interval = SystemInformation.TerminalServerSession ? 100 : 40;
            motionTicker.Tick += delegate {
                motionTicker.Interval = SystemInformation.TerminalServerSession ? 100 :
                    ReadoutsMoving(MotionMs) ? 16 : 40;
                if (!stopping && Visible && motionAllowed && !menu.Visible) Invalidate();
            };
            motionTicker.Start();
        }

        private static bool ResolveMotion(string mode, bool systemEnabled)
        { return mode == "on" || (mode != "off" && systemEnabled); }

        private void StartCornerHover()
        {
            if (!configuration.CornerMode) return;
            RestoreCornerHover();
            hoverTicker.Interval = renderMemory.Reduced ? 125 : 60;
            hoverTicker.Tick += delegate {
                if (stopping) return;
                try
                {
                    if (!configuration.CornerHoverFade || !cornerClickThrough || menu.Visible || !Visible)
                    {
                        if (hoverDimmed || Math.Abs(hoverOpacity - configuration.CornerOpacity) > 0.0001)
                            RestoreCornerHover();
                        return;
                    }
                    Native.Point cursor;
                    bool available = ReadHoverCursor(out cursor);
                    UpdateCornerHover(cursor, available, MotionMs);
                }
                catch (OutOfMemoryException)
                {
                    try { if (renderMemory.AllocationFailed()) ApplyMemoryProtection(); }
                    catch (OutOfMemoryException) { }
                }
            };
            hoverTicker.Start();
        }

        private bool ReadHoverCursor(out Native.Point cursor)
        {
#if HUD_TEST
            if (TestHoverCursor != null)
            {
                Native.Point? test = TestHoverCursor();
                cursor = test.GetValueOrDefault();
                return test.HasValue;
            }
#endif
            return Native.GetCursorPos(out cursor);
        }

        private static bool CursorInsideRoundedCard(Native.Rect bounds, Native.Point cursor, double radius)
        {
            if (cursor.X < bounds.Left || cursor.X >= bounds.Right || cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom)
                return false;
            double width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            double r = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
            if (r == 0) return true;
            double x = cursor.X - bounds.Left, y = cursor.Y - bounds.Top;
            double dx = Math.Max(0, Math.Max(r - x, x - (width - r)));
            double dy = Math.Max(0, Math.Max(r - y, y - (height - r)));
            return dx * dx + dy * dy <= r * r;
        }

        private static bool ShouldDimOnHover(bool inside, bool enabled, bool clickThrough, bool menuOpen, bool visible)
        { return inside && enabled && clickThrough && !menuOpen && visible; }

        private void ApplyCornerOpacity(double opacity)
        {
            if (!IsHandleCreated) { hoverAppliedAlpha = -1; return; }
            int alpha = Math.Max(0, Math.Min(255, (int)Math.Round(opacity * 255)));
            if (alpha != hoverAppliedAlpha && Native.SetLayeredWindowAttributes(Handle, 0, (byte)alpha, 2))
                hoverAppliedAlpha = alpha;
        }

        private void RestoreCornerHover()
        {
            if (configuration == null || !configuration.CornerMode) return;
            hoverDimmed = false;
            hoverOpacity = hoverFrom = hoverTarget = configuration.CornerOpacity;
            hoverChangedAt = 0;
            ApplyCornerOpacity(hoverOpacity);
        }

        // A click-through window receives no mouse-enter events. Only the current
        // screen coordinate is sampled; cursor positions are never stored or logged.
        private void UpdateCornerHover(Native.Point cursor, bool cursorAvailable, double now)
        {
            if (!configuration.CornerMode || stopping || !IsHandleCreated) return;
            Native.Rect bounds;
            bool visible = Visible && Native.IsWindowVisible(Handle) && !Native.IsIconic(Handle);
            bool inside = cursorAvailable && visible && Native.GetWindowRect(Handle, out bounds) &&
                CursorInsideRoundedCard(bounds, cursor, 26 * scale);
            bool dim = ShouldDimOnHover(inside, configuration.CornerHoverFade, cornerClickThrough, menu.Visible, visible);
            double target = dim ? Math.Min(0.25, configuration.CornerOpacity * 0.75) : configuration.CornerOpacity;
            if (hoverDimmed != dim || Math.Abs(target - hoverTarget) > 0.0001)
            {
                hoverDimmed = dim;
                hoverFrom = hoverOpacity;
                hoverTarget = target;
                hoverChangedAt = now;
            }
            double t = motionAllowed ? Math.Max(0, Math.Min(1, (now - hoverChangedAt) / 180.0)) : 1;
            t = t * t * (3 - 2 * t);
            hoverOpacity = hoverFrom + (hoverTarget - hoverFrom) * t;
            ApplyCornerOpacity(hoverOpacity);
        }

        private void SetCornerMotion(string mode)
        {
            configuration.CornerMotion = mode == "on" || mode == "off" ? mode : "system";
            configuration.SaveCornerSettings();
            systemMotionAllowed = AnimationEnabled();
            motionAllowed = !renderMemory.Reduced && ResolveMotion(configuration.CornerMotion, systemMotionAllowed);
            nextMotionPreference = 0;
            Invalidate();
        }

        private void ReleaseMaterial()
        {
            if (cornerBackdrop != null) { cornerBackdrop.Dispose(); cornerBackdrop = null; }
            if (cornerBlueLight != null) { cornerBlueLight.Dispose(); cornerBlueLight = null; }
            if (cornerMintLight != null) { cornerMintLight.Dispose(); cornerMintLight = null; }
        }

        private void ApplyMemoryProtection()
        {
            if (renderMemory.Reduced)
            {
                motionAllowed = false;
                motionTicker.Stop();
                ticker.Interval = 1000;
                hoverTicker.Interval = 125;
                DoubleBuffered = false;
                ReleaseMaterial();
            }
            else
            {
                DoubleBuffered = true;
                ticker.Interval = 100;
                hoverTicker.Interval = 60;
                motionAllowed = ResolveMotion(configuration.CornerMotion, systemMotionAllowed);
                nextMotionPreference = 0;
                emergencyPaintUntil = 0;
                if (configuration.CornerMode && !stopping) motionTicker.Start();
            }
            Invalidate();
        }

        private void CheckMemoryPressure()
        {
            if (!configuration.CornerMode || MotionMs < nextMemoryCheck) return;
            nextMemoryCheck = MotionMs + 2000;
            MemorySnapshot sample;
            if (RenderMemoryGuard.TryRead(out sample) && renderMemory.Sample(sample, MotionMs))
                ApplyMemoryProtection();
        }

        private void RenderCorner(Graphics graphics)
        {
#if HUD_TEST
            if (TestCornerRender != null) TestCornerRender();
#endif
            PaintCorner(graphics);
        }

        private void PaintCornerSafely(Graphics graphics)
        {
            if (renderMemory.Reduced && MotionMs < emergencyPaintUntil)
            {
                try { PaintEmergency(graphics); } catch (OutOfMemoryException) { }
                return;
            }
            try { RenderCorner(graphics); }
            catch (OutOfMemoryException)
            {
                // GDI+ reports resource exhaustion as OutOfMemoryException even for DrawImage.
                // Retry once without the cached bitmap effects; do not let WinForms show a fatal dialog.
                try
                {
                    if (renderMemory.AllocationFailed()) ApplyMemoryProtection();
                    RenderCorner(graphics);
                }
                catch (OutOfMemoryException)
                {
                    renderMemory.AllocationFailed();
                    emergencyPaintUntil = MotionMs + 5000;
                    try { PaintEmergency(graphics); } catch (OutOfMemoryException) { }
                }
            }
        }

        private void PaintEmergency(Graphics graphics)
        {
            // No bitmap, gradient, blur, or animation. The next normal refresh retries the full card.
            graphics.Clear(BackColor);
            Action<string, Font, Color, int, int> line = delegate(string text, Font font, Color color, int y, int height) {
                TextRenderer.DrawText(graphics, text ?? "", font,
                    new Rectangle((int)(25 * scale), (int)(y * scale), (int)(350 * scale), (int)(height * scale)),
                    color, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            };
            line("FOX CODEX  ·  轻量绘制", cornerBrandFont, Ink, 24, 30);
            line(modelText, cornerSmallFont, Muted, 62, 22);
            line("输出速度", cornerSmallFont, Muted, 105, 22);
            line(rateText + " tok/s", cornerSpeedFont, Mint, 132, 42);
            line(cacheText, cornerSmallFont, Muted, 182, 22);
            line(weeklyText, cornerTitleFont, Ink, 235, 25);
            line("重置雷达", cornerSmallFont, Muted, 285, 22);
            line(resetRadar == null ? "等待同步" : resetRadar.Countdown, cornerValueFont, Blue, 312, 30);
            line(resetRadar == null ? "" : resetRadar.CalendarLabel, cornerSmallFont, Muted, 348, 22);
            line("周额度续航  " + quotaRunway.Text + (quotaRunway.State == "cached" ? "  · 上次估算" : ""),
                cornerTitleFont, quotaRunway.State == "estimated" || quotaRunway.State == "exhausted" ? Orange : Ink, 390, 24);
            line("AI服务  " + EmergencyNode("ai") + "    上网节点  " + EmergencyNode("general"),
                cornerSmallFont, Ink, 450, 24);
            line(activity.Title + "  " + activity.ElapsedText, cornerTitleFont, Ink, 510, 25);
        }

        private string EmergencyNode(string id)
        {
            var network = Json.Object(data, "network_latency");
            double now = EpochMs();
            if (now - (Json.Number(data, "updated_at_ms") ?? 0) > 6000 ||
                now - (Json.Number(network, "updated_at_ms") ?? 0) > 75000) return "等待探测";
            var node = Json.List(network, "nodes").FirstOrDefault(n => Json.Text(n, "id") == id);
            double? delay = Json.Number(node, "delay_ms");
            return Json.Text(node, "state") == "ok" && delay.HasValue ? delay.Value.ToString("0") + " ms" : "暂无数据";
        }

        private sealed class RadarMotion
        {
            internal bool Active, Confirmed, Urgent, BurstCompleted;
            internal string Key = "", Identity = "", Notice = "", ResetType = "unknown";
            internal double ChangedAt = -100000;
            internal bool Bursting(double time) { return Active && time >= ChangedAt && time - ChangedAt < 12000; }
            internal Color Tone(double time) { return ResetType == "banked" ? Color.FromArgb(186, 171, 255) :
                !ResetRadarDisplay.IsQuotaReset(ResetType) ? Blue : Confirmed || (BurstCompleted && Bursting(time)) ? Mint : Urgent ? Orange : Amber; }
            internal void Update(ResetRadarDisplay value, double time)
            {
                if (value == null || !value.HasSignal) { Active = Urgent = false; return; }
                if (Key != value.SignalKey)
                {
                    Notice = value.State == "completed" ? ResetRadarDisplay.EventName(value.ResetType) + "已记录" :
                        Identity == value.SignalIdentity && Key.StartsWith("schedule:", StringComparison.Ordinal) ?
                        ResetType != value.ResetType ? "事件类型已更新" : "预告时间已更新" : ResetRadarDisplay.EventName(value.ResetType) + "预告";
                    Key = value.SignalKey; Identity = value.SignalIdentity; ChangedAt = time;
                    BurstCompleted = value.State == "completed";
                }
                else if (value.Alert && !Urgent && Active)
                { Notice = value.PredictionKind == "deadline" ? "预估截止临近" : "预估时间临近"; ChangedAt = time; BurstCompleted = false; }
                Active = true; Confirmed = value.State == "completed"; Urgent = value.Alert; ResetType = value.ResetType;
            }
            internal void Announce(ResetNotice notice, double time)
            {
                // A completion/notice for another event must not recolor this
                // forecast or turn a card grant into an account-reset signal.
                if (!Active || notice == null || notice.EventId != Identity || notice.ResetType != ResetType ||
                    (notice.Kind == "completed") != Confirmed) return;
                ChangedAt = time; BurstCompleted = notice.Kind == "completed";
                Notice = notice.Kind == "completed" ? ResetRadarDisplay.EventName(ResetType) + "已记录" : notice.Kind == "schedule_changed" ?
                    "预告内容已更新" : notice.Kind == "soon" ? "预估时间临近" : ResetRadarDisplay.EventName(ResetType) + "预告";
            }
        }

        private sealed class NodeMotion
        {
            internal int Level = -1;
            internal double ChangedAt = -100000, SampleAt = -100000, Measurement = -1;
            internal string Notice = "";
            internal Color From = Muted, Target = Muted;
            internal Color ReadColor(double time, bool animate)
            {
                double t = animate ? Math.Max(0, Math.Min(1, (time - ChangedAt) / 700)) : 1;
                t = t * t * (3 - 2 * t);
                return Mix(From, Target, t);
            }
            internal void Update(int level, Color target, double measured, double time, string label)
            {
                if (level != Level)
                {
                    From = ReadColor(time, true);
                    Notice = level == 4 ? label + " · 探测超时" : level == 5 ? label + " · 暂不可用" :
                        level == -1 ? label + " · 等待新数据" : level >= 2 ? label + " · 延迟升高" :
                        Level >= 2 ? label + " · 已恢复" : "";
                    Level = level; Target = target; ChangedAt = time;
                }
                if (measured != Measurement) { Measurement = measured; SampleAt = time; }
            }
        }

        private static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        private sealed class ResponseMotion
        {
            internal string Session = "";
            internal double Stamp, ChangedAt = -100000;
            internal double[] From = new double[0], Target = new double[0];
            internal double Blend(double now, bool animate)
            {
                double t = animate ? Math.Max(0, Math.Min(1, (now - ChangedAt) / 900)) : 1;
                return 1 - Math.Pow(1 - t, 3);
            }
            internal void Update(ActivityDisplay value, double now, bool animate)
            {
                if (Session == value.SessionId && Stamp == value.LastResponseAt && Target.SequenceEqual(value.ResponseMs)) return;
                bool continuous = Session == value.SessionId && Target.Length > 0 && value.ResponseMs.Count > 0;
                double blend = Blend(now, animate);
                var next = value.ResponseMs.ToArray();
                var prior = new double[next.Length];
                for (int i = 0; i < next.Length; i++)
                {
                    int old = Math.Max(0, Math.Min(Target.Length - 1, i - (next.Length - Target.Length)));
                    prior[i] = continuous ? From[old] + (Target[old] - From[old]) * blend : next[i];
                }
                Session = value.SessionId; Stamp = value.LastResponseAt;
                From = prior; Target = next;
                // Switching sessions and first load must not look like new output.
                ChangedAt = continuous && animate ? now : now - 100000;
            }
        }

        private static Color ResponseColor(double ms)
        {
            double t = Math.Max(0, Math.Min(3, ms < 30000 ? ms / 30000 : ms < 60000 ?
                1 + (ms - 30000) / 30000 : 2 + (ms - 60000) / 60000));
            return t < 1 ? Mix(Mint, Amber, t) : t < 2 ? Mix(Amber, Orange, t - 1) : Mix(Orange, Rose, t - 2);
        }

        // Layered vector strokes provide a soft halo without bitmap blur or cached textures.
        private static void GlowPanel(Graphics g, RectangleF bounds, Color color, double strength, double pulse)
        {
            for (int i = 3; i >= 1; i--)
                using (var path = RoundedCard(bounds, 18))
                using (var pen = new Pen(Color.FromArgb((int)((5 + pulse * 7) * strength), color), i * 2))
                    g.DrawPath(pen, path);
            using (var brush = new LinearGradientBrush(bounds, Color.FromArgb((int)((12 + pulse * 14) * strength), color),
                Color.FromArgb(0, color), 90f))
            using (var path = RoundedCard(bounds, 18)) g.FillPath(brush, path);
        }

        private Color ActivityColor()
        {
            if (!activity.Active) return Muted;
            double level = Math.Max(0, Math.Min(3, activity.WaitLevel));
            Color from = level < 1 ? Mint : level < 2 ? Amber : Orange;
            Color to = level < 1 ? Amber : level < 2 ? Orange : Rose;
            double fraction = level < 1 ? level : level < 2 ? level - 1 : level - 2;
            return Color.FromArgb((int)Math.Round(from.R + (to.R - from.R) * fraction),
                (int)Math.Round(from.G + (to.G - from.G) * fraction),
                (int)Math.Round(from.B + (to.B - from.B) * fraction));
        }

        private static void Panel(Graphics g, RectangleF bounds, Color fill, Color border, float radius)
        {
            using (var path = RoundedCard(bounds, radius))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border))
            { g.FillPath(brush, path); g.DrawPath(pen, path); }
        }

        // The ambient material is rendered once. Live data and effects remain vector layers.
        private static void Ambient(Graphics g, RectangleF bounds, Color color, int alpha)
        {
            using (var shape = new GraphicsPath())
            {
                shape.AddEllipse(bounds);
                using (var light = new PathGradientBrush(shape))
                {
                    light.CenterColor = Color.FromArgb(alpha, color);
                    light.SurroundColors = new[] { Color.FromArgb(0, color) };
                    g.FillPath(light, shape);
                }
            }
        }

        private void PaintMaterial(Graphics g)
        {
            if (renderMemory.Reduced)
            {
                using (var fill = new SolidBrush(BackColor)) g.FillRectangle(fill, 0, 0, CornerWidth, CornerHeight);
                return;
            }
            if (cornerBackdrop == null)
            {
                cornerBackdrop = new Bitmap(CornerWidth, CornerHeight);
                using (var canvas = Graphics.FromImage(cornerBackdrop))
                using (var shape = RoundedCard(new RectangleF(.5f, .5f, CornerWidth - 1, CornerHeight - 1), 26))
                using (var fill = new LinearGradientBrush(new Rectangle(0, 0, CornerWidth, CornerHeight),
                    Color.FromArgb(26, 31, 40), Color.FromArgb(14, 18, 25), 80f))
                {
                    canvas.SmoothingMode = SmoothingMode.AntiAlias;
                    canvas.SetClip(shape);
                    canvas.FillPath(fill, shape);
                    Ambient(canvas, new RectangleF(-130, -180, 560, 410), Blue, 16);
                    Ambient(canvas, new RectangleF(220, 180, 360, 470), Mint, 5);
                    Ambient(canvas, new RectangleF(-130, 370, 330, 350), Blue, 12);
                    // Fine, fixed grain gives the translucent surface a material scale.
                    using (var grain = new Bitmap(64, 64))
                    {
                        var random = new Random(21);
                        for (int y = 0; y < 64; y++)
                            for (int x = 0; x < 64; x++)
                                grain.SetPixel(x, y, Color.FromArgb(random.Next(1, 5),
                                    random.Next(2) == 0 ? Color.White : Color.Black));
                        using (var texture = new TextureBrush(grain, WrapMode.Tile))
                            canvas.FillPath(texture, shape);
                    }
                }
            }
            g.DrawImage(cornerBackdrop, new Rectangle(0, 0, CornerWidth, CornerHeight));
            if (cornerBlueLight == null) cornerBlueLight = SoftLight(Blue, 32);
            if (cornerMintLight == null) cornerMintLight = SoftLight(Mint, 16);
            double clock = motionAllowed ? MotionMs : 0;
            var saved = g.Save();
            try
            {
                using (var clip = RoundedCard(new RectangleF(1, 1, CornerWidth - 2, CornerHeight - 2), 26))
                    g.SetClip(clip, CombineMode.Intersect);
                g.DrawImage(cornerBlueLight, (float)(-185 + 135 * Math.Sin(clock / 3100)),
                    (float)(-200 + 85 * Math.Cos(clock / 4200)), 540, 480);
                g.DrawImage(cornerMintLight, (float)(135 + 90 * Math.Cos(clock / 4700)),
                    (float)(260 + 85 * Math.Sin(clock / 3700)), 350, 360);
            }
            finally { g.Restore(saved); }
        }

        private static Bitmap SoftLight(Color color, int alpha)
        {
            var result = new Bitmap(192, 192);
            try
            {
                using (var canvas = Graphics.FromImage(result))
                {
                    canvas.SmoothingMode = SmoothingMode.AntiAlias;
                    Ambient(canvas, new RectangleF(0, 0, 192, 192), color, alpha);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        // A moving highlight is clipped to the rim; it never sweeps over the values.
        private void SpecularRim(Graphics g, RectangleF bounds, float radius, Color tone, double phase, int alpha)
        {
            float center = motionAllowed ? (float)(.5 + .31 * Math.Sin(MotionMs / 2000 + phase)) : .3f;
            using (var path = RoundedCard(bounds, radius))
            using (var light = new LinearGradientBrush(bounds, Color.Transparent, Color.Transparent, 25f))
            {
                light.InterpolationColors = new ColorBlend {
                    Colors = new[] { Color.FromArgb(0, tone), Color.FromArgb(alpha / 5, tone),
                        Color.FromArgb(alpha, Mix(tone, Color.White, .45)), Color.FromArgb(alpha / 5, tone), Color.FromArgb(0, tone) },
                    Positions = new[] { 0f, center - .16f, center, center + .16f, 1f } };
                using (var glow = new Pen(light, 1.6f)) g.DrawPath(glow, path);
                using (var edge = new Pen(light, 1)) g.DrawPath(edge, path);
            }
        }

        private static void ScanOrbit(Graphics g, float x, float y, float radius, Color color, double clock)
        {
            // Fixed-size arcs avoid the GDI+ failure mode of near-zero round-cap sweeps.
            float angle = (float)(clock % 4200 / 4200 * 360);
            for (int i = 0; i < 5; i++)
                using (var pen = new Pen(Color.FromArgb(180 - i * 30, color), 1.5f))
                    g.DrawArc(pen, x - radius, y - radius, radius * 2, radius * 2, angle - i * 16, 15);
            double radians = angle * Math.PI / 180;
            using (var pen = new Pen(Color.FromArgb(130, color)))
                g.DrawLine(pen, x, y, x + radius * (float)Math.Cos(radians), y + radius * (float)Math.Sin(radians));
        }

        private void GlassPanel(Graphics g, RectangleF bounds, Color accent, int emphasis)
        {
            using (var path = RoundedCard(bounds, 18))
            using (var fill = new LinearGradientBrush(bounds, Color.FromArgb(200, 33, 40, 51),
                Color.FromArgb(210, 23, 29, 38), 90f))
            using (var rim = new LinearGradientBrush(bounds, Color.FromArgb(46, 175, 194, 217),
                Color.FromArgb(20, 175, 194, 217), 90f))
            using (var pen = new Pen(rim))
            { g.FillPath(fill, path); g.DrawPath(pen, path); }
            if (!renderMemory.Reduced)
            {
                // Quiet inner bevel and a narrow top reflection give the common
                // graphite material depth without blurring or covering any text.
                var inset = new RectangleF(bounds.X + 1.5f, bounds.Y + 1.5f, bounds.Width - 3, bounds.Height - 3);
                using (var path = RoundedCard(inset, 16.5f))
                using (var bevel = new LinearGradientBrush(inset, Color.FromArgb(19, 209, 224, 239), Color.FromArgb(35, 4, 8, 14), 90f))
                using (var pen = new Pen(bevel)) g.DrawPath(pen, path);
                using (var light = new LinearGradientBrush(new RectangleF(bounds.Left + 24, bounds.Top, bounds.Width - 48, 2),
                    Color.FromArgb(0, Ink), Color.FromArgb(0, Ink), 0f))
                {
                    light.InterpolationColors = new ColorBlend { Colors = new[] {Color.FromArgb(0, Ink), Color.FromArgb(34, Ink), Color.FromArgb(0, Ink)}, Positions = new[] {0f,.45f,1f}};
                    using (var pen = new Pen(light)) g.DrawLine(pen, bounds.Left + 24, bounds.Top + .8f, bounds.Right - 24, bounds.Top + .8f);
                }
            }
            // Tinted edges are event feedback. Healthy cards share one quiet material.
            if (emphasis > 0)
                SpecularRim(g, bounds, 18, accent, bounds.X / 120 + bounds.Y / 200, Math.Min(125, emphasis + 32));
        }

        private static void CornerLayer(Graphics g, float y, Action paint)
        {
            var saved = g.Save();
            try { g.TranslateTransform(0, y); paint(); }
            finally { g.Restore(saved); }
        }

        private void PaintCorner(Graphics graphics)
        {
            var saved = graphics.Save();
            try
            {
                graphics.ScaleTransform((float)scale, (float)scale);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                double now = EpochMs(), motion = MotionMs;
                if (motion >= nextMotionPreference)
                {
                    systemMotionAllowed = AnimationEnabled();
                    motionAllowed = !renderMemory.Reduced && ResolveMotion(configuration.CornerMotion, systemMotionAllowed);
                    nextMotionPreference = motion + 2000;
                }
                if (motionAllowed) motionPaintCount++;
                Color signal = ActivityColor();
                double phase = motionAllowed && activity.Active ?
                    (Math.Sin(motion / (activity.Severity >= 2 ? 850 : 1600)) + 1) / 2 : .5;
                PaintMaterial(graphics);
                Color cardBorder = Color.FromArgb(60, 160, 177, 203);
                if (radarMotion.Bursting(motion))
                    cardBorder = Color.FromArgb(motionAllowed ? (int)(110 + 100 * (1 + Math.Sin(motion / 550)) / 2) : 180, radarMotion.Tone(motion));
                using (var path = RoundedCard(new RectangleF(.5f, .5f, CornerWidth - 1, CornerHeight - 1), 26))
                using (var border = new Pen(cardBorder)) graphics.DrawPath(border, path);
                SpecularRim(graphics, new RectangleF(1.3f, 1.3f, CornerWidth - 2.6f, CornerHeight - 2.6f), 26,
                    radarMotion.Bursting(motion) ? radarMotion.Tone(motion) : Blue, 0, radarMotion.Bursting(motion) ? 190 : 48);

                // A small orbital mark stays quiet while the status capsule carries activity.
                using (var halo = new SolidBrush(Color.FromArgb(14, Blue))) graphics.FillEllipse(halo, 22, 24, 27, 27);
                using (var orbit = new Pen(Color.FromArgb(140, Blue), 1.2f)) graphics.DrawArc(orbit, 27, 29, 17, 17, 35, 275);
                using (var dot = new SolidBrush(Ink)) graphics.FillEllipse(dot, 32.5f, 34.5f, 6, 6);
                using (var glint = new SolidBrush(Mint)) graphics.FillEllipse(glint, 42, 29, 3.5f, 3.5f);
                // The brand mark is static; the radar owns the scanning motion.
                CornerText(graphics, "FOX CODEX", cornerBrandFont, Ink, new RectangleF(56, 22, 150, 23), false);
                CornerText(graphics, modelText, cornerMicroFont, Muted, new RectangleF(57, 45, 200, 16), false);
                var status = new RectangleF(284, 28, 92, 27);
                Panel(graphics, status, Color.FromArgb(12, signal), Color.FromArgb(37, signal), 13);
                using (var halo = new SolidBrush(Color.FromArgb((int)(15 + phase * 23), signal))) graphics.FillEllipse(halo, 295, 35, 13, 13);
                using (var dot = new SolidBrush(signal)) graphics.FillEllipse(dot, 299, 39, 5, 5);
                CornerText(graphics, renderMemory.Reduced ? "轻量绘制" : !cornerClickThrough ? "临时解锁" : activity.Active ? "进行中" : activity.State == "idle" ? "已完成" : "待确认",
                    cornerSmallFont, signal, new RectangleF(308, 29, 62, 25), StringAlignment.Center);
                PaintSpeedInstrument(graphics, motion);
                PaintQuotaSurface(graphics, motion);
                PaintCornerQuota(graphics);
                PaintQuotaRuler(graphics);
                PaintQuotaRadarBridge(graphics, motion);
                PaintCornerRadar(graphics);

                var network = Json.Object(data, "network_latency");
                var nodes = Json.List(network, "nodes");
                double networkAge = now - (Json.Number(network, "updated_at_ms") ?? 0);
                bool heartbeatOk = now - (Json.Number(data, "updated_at_ms") ?? 0) <= 6000;
                string networkTime = Json.Text(network, "state") == "loading" ? "正在探测" :
                    !heartbeatOk || networkAge > 75000 ? "等待重新探测" :
                    (Json.Number(network, "interval_seconds") ?? 10).ToString("0") + " 秒探测 · " + Math.Max(0, (int)(networkAge / 1000)) + " 秒前";
                GlassPanel(graphics, new RectangleF(20, 558, 360, 64), Blue, 0);
                using (var divider = new Pen(Color.FromArgb(28, Muted))) graphics.DrawLine(divider, 200, 571, 200, 609);
                PaintNode(graphics, nodes.FirstOrDefault(n => Json.Text(n, "id") == "ai"), "AI 服务", 20, 174, now, heartbeatOk);
                PaintNode(graphics, nodes.FirstOrDefault(n => Json.Text(n, "id") == "general"), "上网节点", 206, 174, now, heartbeatOk);
                var notice = aiMotion.ChangedAt >= generalMotion.ChangedAt ? aiMotion : generalMotion;
                bool showNotice = notice.Notice.Length > 0 && motion - notice.ChangedAt < 8000;
                CornerText(graphics, "连接", cornerSmallFont, Muted, new RectangleF(24, 535, 90, 18), false);
                CornerText(graphics, showNotice ? notice.Notice : networkTime, cornerMicroFont,
                    showNotice ? notice.Target : Muted, new RectangleF(122, 535, 253, 18), true);
                PaintActivity(graphics, signal, phase);
            }

            finally { graphics.Restore(saved); }
        }

        private Color QuotaRunwayTone()
        {
            var view = quotaRunway;
            if (view.State == "cached") return Muted;
            if (view.ForecastState == "exhausted" || (view.HasTimeline && view.ForecastState == "estimated" && view.Hours <= 6)) return Rose;
            if (view.HasTimeline && view.ForecastState == "estimated") return Orange;
            return view.HasTimeline && view.ForecastState == "through_reset" ? Mint : Muted;
        }

        private double? LiveWeeklyRemaining()
        {
            if (quotaCached) return null;
            var window = quotaWindows.FirstOrDefault(w => Json.Number(w, "window_minutes") == 10080);
            return Json.Number(window, "remaining_percent");
        }

        private string QuotaResetLink()
        {
            double? remaining = LiveWeeklyRemaining();
            if (!remaining.HasValue || !radarMotion.Active || resetRadar == null || !resetRadar.HasSignal || !resetRadar.ResetsQuota) return "none";
            bool burst = radarMotion.Bursting(MotionMs);
            // A public completion is not proof that this account has been refilled.
            if (radarMotion.Confirmed || (radarMotion.BurstCompleted && burst)) return burst ? "completed" : "none";
            return remaining.Value > 0 ? "forecast" : "none";
        }

        private string QuotaHeading()
        {
            string link = QuotaResetLink();
            return link == "forecast" ? "周额度 · 优先使用" : link == "completed" ? "周额度 · 核对到账" :
                quotaWindows.Count == 0 ? "周额度" : "周额度剩余";
        }

        private static readonly Color[] QuotaSpectrum = {
            Color.FromArgb(137, 230, 255), Color.FromArgb(165, 190, 255),
            Color.FromArgb(209, 168, 255), Color.FromArgb(255, 166, 215),
            Color.FromArgb(255, 190, 157), Color.FromArgb(255, 225, 143), Color.FromArgb(143, 239, 193) };

        private static Color SpectrumColor(double phase, bool vivid)
        {
            double position = (phase - Math.Floor(phase)) * QuotaSpectrum.Length;
            int index = (int)position;
            Color color = Mix(QuotaSpectrum[index], QuotaSpectrum[(index + 1) % QuotaSpectrum.Length], position - index);
            return vivid ? color : Mix(color, Ink, .25);
        }

        private double QuotaRainbowPhase()
        {
            quotaFlow.Update(MotionMs, speedView.Ready ? speedView.Rate : null, QuotaResetLink() == "forecast",
                motionAllowed && !renderMemory.Reduced && LiveWeeklyRemaining().GetValueOrDefault() > 0);
            return quotaFlow.Phase;
        }

        private static LinearGradientBrush QuotaSpectrumBrush(RectangleF bounds, double phase, bool vivid, int alpha)
        {
            var brush = new LinearGradientBrush(bounds, Color.White, Color.White, 0f);
            try
            {
                var colors = new Color[9]; var positions = new float[9];
                for (int i = 0; i < 9; i++)
                { positions[i] = i / 8f; colors[i] = Color.FromArgb(alpha, SpectrumColor(phase + i / 8.0, vivid)); }
                brush.InterpolationColors = new ColorBlend { Colors = colors, Positions = positions };
                return brush;
            }
            catch { brush.Dispose(); throw; }
        }

        private static void QuotaBrushText(Graphics g, string text, Font font, Brush brush, RectangleF bounds)
        {
            using (var format = StringFormat.GenericTypographic)
            {
                format.Alignment = StringAlignment.Near; format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter; format.FormatFlags |= StringFormatFlags.NoWrap;
                g.DrawString(text, font, brush, bounds, format);
            }
        }

        private void PaintQuotaNumber(Graphics g, string value, Font hero, double remaining, bool hasWindow)
        {
            float valueWidth;
            using (var format = StringFormat.GenericTypographic) valueWidth = g.MeasureString(value, hero, 190, format).Width;
            var numberBounds = new RectangleF(42, 291, 150, 55);
            var unitBounds = new RectangleF(Math.Min(166, 47 + valueWidth), 313, 24, 25);
            if (hasWindow && !quotaCached && remaining > 0)
            {
                double phase = QuotaRainbowPhase();
                using (var spectrum = QuotaSpectrumBrush(new RectangleF(30, 295, Math.Max(80, unitBounds.Right - 30), 55),
                    phase, QuotaResetLink() == "forecast", 255))
                {
                    MovingReadout(g, quotaDigits, value, hero, numberBounds, StringAlignment.Near, "weekly", delegate(string part, RectangleF box, double alpha) {
                        using (var glyphBrush = QuotaSpectrumBrush(new RectangleF(30, 295, Math.Max(80, unitBounds.Right - 30), 55),
                            phase, QuotaResetLink() == "forecast", (int)(255 * alpha))) QuotaBrushText(g, part, hero, glyphBrush, box);
                    });
                    QuotaBrushText(g, "%", cornerValueFont, spectrum, unitBounds);
                }
            }
            else
            {
                // Expiry/exhaustion is authoritative immediately; never animate a
                // positive old balance through an unavailable or empty state.
                quotaDigits.Set(value, "weekly", MotionMs, false);
                MovingText(g, quotaDigits, value, hero, !hasWindow || quotaCached ? Muted : remaining <= 0 ? Rose : Ink,
                    numberBounds, StringAlignment.Near, "weekly");
                if (hasWindow) CornerText(g, "%", cornerValueFont, Muted, unitBounds, false);
            }
        }

        private void PaintQuotaBattery(Graphics g, double? percent)
        {
            // The cell and its charge share the number's 0–100 scale. The
            // independently projected dates and hatched gap live below the cell.
            var body = new RectangleF(35, 298, 147, 44);
            var chamber = new RectangleF(38, 301, 141, 38);
            bool linked = percent.HasValue && QuotaResetLink() == "forecast";
            Color charge = quotaCached || !percent.HasValue ? Muted : percent <= 10 ? Amber : Mint;
            Panel(g, body, Color.FromArgb(205, 12, 18, 25), Color.FromArgb(110, charge), 8);
            using (var terminal = new SolidBrush(Color.FromArgb(145, charge)))
                g.FillRectangle(terminal, 184, 312, 3, 16);
            if (!percent.HasValue || percent <= 0) return;
            var state = g.Save();
            try
            {
                using (var shape = RoundedCard(chamber, 5)) g.SetClip(shape, CombineMode.Intersect);
                float width = chamber.Width * (float)(percent.Value / 100);
                using (Brush fill = linked ? (Brush)QuotaSpectrumBrush(chamber, QuotaRainbowPhase(), true, 86) :
                    new LinearGradientBrush(chamber, Color.FromArgb(37, charge), Color.FromArgb(74, charge), 0f))
                    g.FillRectangle(fill, chamber.X, chamber.Y, width, chamber.Height);
                using (Brush strip = linked ? (Brush)QuotaSpectrumBrush(chamber, QuotaRainbowPhase(), true, 240) :
                    new SolidBrush(Color.FromArgb(205, charge)))
                    g.FillRectangle(strip, chamber.X, chamber.Bottom - 3, width, 3);
                using (var marker = new Pen(Color.FromArgb(115, charge)))
                    if (width >= 1 && percent < 100) g.DrawLine(marker, chamber.X+width, chamber.Y, chamber.X+width, chamber.Bottom);
            }
            finally { g.Restore(state); }
        }

        private void PaintQuotaResetGlow(Graphics g, RectangleF bounds, double clock)
        {
            string link = QuotaResetLink();
            if (link == "none") return;
            bool animate = motionAllowed && !renderMemory.Reduced;
            bool burst = radarMotion.Bursting(clock);
            bool confirmed = link == "completed";
            double phase = QuotaRainbowPhase();
            double pulse = animate ? (1 + Math.Sin((clock - radarMotion.ChangedAt) * Math.PI * 2 / 3200)) / 2 : .5;
            Color tone = confirmed ? Mint : SpectrumColor(phase, true);
            if (!renderMemory.Reduced) GlowPanel(g, bounds, tone, burst ? .95 : .32, pulse);
            if (!confirmed)
                using (var surface = RoundedCard(bounds, 18))
                using (var wash = QuotaSpectrumBrush(bounds, phase, true, burst ? 26 : 16)) g.FillPath(wash, surface);
            using (var outline = RoundedCard(bounds, 18))
            using (Brush spectrum = confirmed ? (Brush)new SolidBrush(Color.FromArgb(180, Mint)) :
                QuotaSpectrumBrush(bounds, phase, true, burst ? 200 : 135))
            using (var border = new Pen(spectrum, burst ? 1.5f : 1.1f)) g.DrawPath(border, outline);
            if (!animate) return;
            // A brief upward light answers the radar's arrival pulse, then settles
            // to a slow rim glint. No extra timer, bitmap or transformed numeral.
            if (burst)
            {
                var saved = g.Save();
                try
                {
                    using (var clip = RoundedCard(bounds, 18)) g.SetClip(clip, CombineMode.Intersect);
                    float y = bounds.Bottom + 40 - (float)(((clock - radarMotion.ChangedAt) % 3000) / 3000) * (bounds.Height + 80);
                    var band = new RectangleF(bounds.Left, y - 40, bounds.Width, 40);
                    using (var light = new LinearGradientBrush(band, Color.Transparent, Color.Transparent, 90f))
                    {
                        light.InterpolationColors = new ColorBlend { Colors = new[] {Color.FromArgb(0, tone), Color.FromArgb(30, tone), Color.FromArgb(0, tone)},
                            Positions = new[] {0f, .5f, 1f} };
                        g.FillRectangle(light, band);
                    }
                }
                finally { g.Restore(saved); }
            }
            using (var rim = RoundedCard(bounds, 18))
            using (var glint = new Pen(Color.FromArgb(burst ? 225 : 135, Ink), 1.5f))
            {
                glint.DashPattern = new[] {18f, 700f}; glint.DashOffset = -(float)(phase * 718);
                g.DrawPath(glint, rim);
            }
        }

        private void PaintQuotaRadarBridge(Graphics g, double clock)
        {
            string link = QuotaResetLink();
            if (link == "none") return;
            bool animate = motionAllowed && !renderMemory.Reduced;
            Color tone = link == "completed" ? Mint : SpectrumColor(QuotaRainbowPhase(), true);
            using (var thread = new Pen(Color.FromArgb(100, tone), 1.2f)) g.DrawLine(thread, 44, 456, 44, 468);
            float y = animate ? 468 - (float)(((clock - radarMotion.ChangedAt) % 1800) / 1800) * 12 : 462;
            using (var halo = new SolidBrush(Color.FromArgb(45, tone))) g.FillEllipse(halo, 39.5f, y - 4.5f, 9, 9);
            using (var dot = new SolidBrush(tone)) g.FillEllipse(dot, 42.5f, y - 1.5f, 3, 3);
        }

        private void PaintCornerQuota(Graphics g)
        {
            var window = quotaWindows.FirstOrDefault(w => Json.Number(w, "window_minutes") == 10080);
            double? balance = QuotaRunwayDisplay.Number(window, "remaining_percent");
            if (balance < 0 || balance > 100) balance = null;
            double remaining = balance.GetValueOrDefault();
            Color tone = QuotaRunwayTone();
            var headingBounds = new RectangleF(36, 273, 157, 22);
            if (QuotaResetLink() == "forecast")
                using (var heading = QuotaSpectrumBrush(headingBounds, QuotaRainbowPhase(), false, 255))
                    QuotaBrushText(g, QuotaHeading(), cornerTitleFont, heading, headingBounds);
            else CornerText(g, QuotaHeading(), cornerTitleFont, Ink, headingBounds, false);
            CornerText(g, quotaRunway.State == "cached" ? "上次预计可用" : quotaRunway.Provisional ? "预计可用·初估" : "预计可用", cornerSmallFont, Muted,
                new RectangleF(205, 273, 159, 22), true);
            string value = balance.HasValue ? remaining.ToString("0.#") : "—";
            Font hero = value.Length > 4 ? cornerQuotaFont : cornerHeroFont;
            PaintQuotaBattery(g, balance);
            PaintQuotaNumber(g, value, hero, remaining, balance.HasValue);
            CornerText(g, quotaRunway.Text, cornerRunwayFont, tone, new RectangleF(198, 298, 166, 35), true);
            string ending = quotaScale.Projected && !quotaScale.ThroughReset ? quotaScale.EndLabel : "";
            CornerText(g, ending, cornerMicroFont, quotaScale.Cached ? Muted : DebtMetal, new RectangleF(192, 327, 172, 18), true);
            // Equal-width columns: label and the complete number/unit group share one center.
            CornerText(g, quotaToday.Label, cornerMicroFont,
                quotaToday.OverBudget && !quotaToday.Cached ? DebtMetal : Muted, new RectangleF(32, 406, 104, 18), StringAlignment.Center);
            string advice = quotaToday.Text;
            Color todayTone = !quotaToday.Remaining.HasValue || quotaToday.Cached ? Muted :
                quotaToday.OverBudget ? DebtText : quotaToday.Remaining == 0 ? Orange : Mint;
            if (advice.EndsWith("%")) PaintQuotaMetric(g, advice.Substring(0, advice.Length - 1), "%", 84, todayTone);
            else CornerText(g, advice, cornerRadarFont, todayTone, new RectangleF(32, 425, 104, 24), StringAlignment.Center);
            CornerText(g, quotaCached ? "本轮已用·暂存" : "本轮已用", cornerMicroFont, Muted,
                new RectangleF(148, 406, 104, 18), StringAlignment.Center);
            PaintQuotaMetric(g, quotaRunway.Consumed.HasValue ? quotaRunway.Consumed.Value.ToString("0.#") : "—",
                quotaRunway.Consumed.HasValue ? "%" : "", 200, quotaCached ? Muted : Ink);
            PaintDailyQuota(g, quotaCached ? "每日可用·暂存" : "每日可用", quotaRunway.AllowancePerDay, 264, tone);
        }

        private void PaintDailyQuota(Graphics g, string label, double? value, float x, Color color)
        {
            CornerText(g, label, cornerMicroFont, Muted, new RectangleF(x, 406, 104, 18), StringAlignment.Center);
            string number = value.HasValue ? (value.Value > 999 ? ">999" : value.Value.ToString("0.#")) : "—";
            PaintQuotaMetric(g, number, value.HasValue ? "%/天" : "", x + 52, color);
        }

        private void PaintQuotaMetric(Graphics g, string number, string unit, float center, Color color)
        {
            using (var format = StringFormat.GenericTypographic)
            {
                float numberWidth = g.MeasureString(number, cornerValueFont, 200, format).Width;
                float unitWidth = unit.Length == 0 ? 0 : g.MeasureString(unit, cornerMicroFont, 100, format).Width;
                float gap = unit.Length == 0 ? 0 : 3;
                float left = center - (numberWidth + gap + unitWidth) / 2;
                var transition = center < 140 ? todayDigits : center < 250 ? cycleDigits : allowanceDigits;
                MovingText(g, transition, number, cornerValueFont, color, new RectangleF(left, 425, numberWidth + 1, 24),
                    StringAlignment.Near, "quota-advice");
                if (unit.Length > 0) CornerText(g, unit, cornerMicroFont, Muted, new RectangleF(left + numberWidth + gap, 429, unitWidth + 1, 18), false);
            }
        }

        private void PaintQuotaSurface(Graphics g, double clock)
        {
            bool urgent = quotaRunway.State != "cached" && (quotaRunway.ForecastState == "exhausted" ||
                (quotaRunway.HasTimeline && quotaRunway.ForecastState == "estimated" && quotaRunway.Hours <= 6));
            var bounds = new RectangleF(20, 260, 360, 196);
            if (quotaToday.OverBudget)
                Panel(g, bounds, Color.FromArgb(17, 20, 26), Color.FromArgb(61, 67, 77), 18);
            else
            {
                GlassPanel(g, bounds, QuotaRunwayTone(), urgent ? 48 : 0);
                if (urgent) GlowPanel(g, bounds, Rose, .45, motionAllowed ? (1 + Math.Sin(clock / 1050)) / 2 : .35);
            }
            PaintQuotaResetGlow(g, bounds, clock);
            if (motionAllowed && !renderMemory.Reduced && !quotaCached && QuotaResetLink() == "none")
            {
                double receipt = quotaReceipt.Strength(clock);
                if (receipt > 0)
                {
                    Color tone = quotaReceipt.Refilled ? Mint : Blue;
                    using (var path = RoundedCard(bounds, 18))
                    using (var pen = new Pen(Color.FromArgb((int)(receipt * (quotaReceipt.Refilled ? 150 : 70)), tone), quotaReceipt.Refilled ? 1.5f : 1))
                        g.DrawPath(pen, path);
                    if (quotaReceipt.Refilled)
                        using (var wash = new LinearGradientBrush(bounds, Color.FromArgb((int)(receipt * 15), Mint), Color.FromArgb(0, Mint), 90f))
                        using (var path = RoundedCard(bounds, 18)) g.FillPath(wash, path);
                }
            }
            using (var divider = new Pen(Color.FromArgb(25, Muted))) g.DrawLine(divider, 36, 402, 364, 402);
            if (quotaToday.OverBudget) PaintTodayDebtSurface(g, clock);
        }

        // Local vector light over obsidian. Values and layout never animate;
        // cached advice, reduced motion and memory protection retain a static warning.
        private void PaintTodayDebtSurface(Graphics g, double clock)
        {
            bool animate = motionAllowed && !renderMemory.Reduced && !quotaToday.Cached;
            double depth = Math.Min(1, -quotaToday.Remaining.Value / 15.0);
            double breath = animate ? (1 + Math.Sin(clock * Math.PI * 2 / 4400)) / 2 : .35;
            var tile = new RectangleF(28, 405, 112, 48);
            Color metal = quotaToday.Cached ? Muted : DebtMetal;
            using (var shape = RoundedCard(tile, 10))
            {
                using (var halo = new Pen(Color.FromArgb(quotaToday.Cached ? 5 : (int)(10 + depth * 9 + breath * 12), metal), 5))
                    g.DrawPath(halo, shape);
                using (var fill = new SolidBrush(DebtBlack)) g.FillPath(fill, shape);
                if (!renderMemory.Reduced)
                {
                    var saved = g.Save();
                    try
                    {
                        g.SetClip(shape, CombineMode.Intersect);
                        using (var bevel = new LinearGradientBrush(tile, Color.FromArgb(25, Ink), Color.Transparent, 115f))
                            g.FillRectangle(bevel, tile);
                        if (animate)
                        {
                            double phase = (clock % 4400) / 4400;
                            float x = tile.Left - 42 + (float)phase * (tile.Width + 84);
                            var band = new RectangleF(x, tile.Top, 42, tile.Height);
                            using (var sheen = new LinearGradientBrush(band, Color.Transparent, Color.Transparent, 15f))
                            {
                                sheen.InterpolationColors = new ColorBlend {
                                    Colors = new[] { Color.FromArgb(0, metal), Color.FromArgb(24, metal), Color.FromArgb(0, metal) },
                                    Positions = new[] { 0f, .5f, 1f } };
                                g.FillRectangle(sheen, band);
                            }
                        }
                    }
                    finally { g.Restore(saved); }
                }
                using (var rim = new Pen(Color.FromArgb(quotaToday.Cached ? 48 : (int)(80 + depth * 35 + breath * 35), metal)))
                    g.DrawPath(rim, shape);
                // Light follows the closed bezel; there is no second progress track.
                if (animate)
                    using (var glint = new Pen(Color.FromArgb(190, DebtText), 1.4f))
                    {
                        glint.DashPattern = new[] { 10f, 210f };
                        glint.DashOffset = -(float)((clock % 4400) / 4400 * 220);
                        glint.DashCap = DashCap.Round;
                        g.DrawPath(glint, shape);
                    }
            }
        }

        private string QuotaGapCaption()
        {
            string prefix = quotaScale.Cached ? "上次空窗 " : "预计空窗 ";
            if (quotaRunway.State == "stale" || quotaRunway.State == "unavailable") return "空窗待同步";
            if (quotaScale.Exhausted) return quotaRunway.ResetHours.HasValue ?
                "待重置 " + QuotaRunwayDisplay.Duration(quotaRunway.ResetHours.Value) : "等待周重置";
            if (quotaScale.ThroughReset) return quotaScale.Cached ? "上次预计无空窗" : "预计无空窗";
            if (quotaScale.Projected && quotaRunway.GapHours.HasValue)
                return quotaRunway.GapHours.Value > 0 ? prefix + QuotaRunwayDisplay.Duration(quotaRunway.GapHours.Value) :
                    quotaRunway.GapUnder15Minutes ? prefix + "不足15分" : "预计临近重置用尽";
            return "空窗待估算";
        }

        private void PaintQuotaRuler(Graphics g)
        {
            const float left = 36, right = 364, top = 362, baseline = 377;
            var ruler = quotaScale;
            bool linked = !ruler.Cached && QuotaResetLink() == "forecast";
            double spectrumPhase = linked ? QuotaRainbowPhase() : 0;
            Color usable = ruler.Cached ? Muted : linked ? SpectrumColor(spectrumPhase, true) : Mint;
            Color risk = ruler.Cached ? Muted : linked ? SpectrumColor(spectrumPhase + ruler.FutureCoverage, true) : DebtMetal;
            var spectrumBounds = new RectangleF(left, top, right - left, baseline - top);
            float split = left + (right - left) * (float)ruler.FutureCoverage;
            bool gap = ruler.Available && ((ruler.Projected && !ruler.ThroughReset) || ruler.Exhausted);
            bool animate = gap && !ruler.Cached && motionAllowed && !renderMemory.Reduced;
            double clock = MotionMs;
            // An open calendar axis, not a capsule or a percentage fill. Diagonal
            // hatching denotes the unavailable interval even without color/motion.
            using (var axis = new Pen(Color.FromArgb(95, Muted))) g.DrawLine(axis, left, baseline, right, baseline);
            if (ruler.Projected && split > left)
            {
                var area = new RectangleF(left, top, split - left, baseline - top);
                if (linked)
                    using (var shade = QuotaSpectrumBrush(spectrumBounds, spectrumPhase, true, 35)) g.FillRectangle(shade, area);
                else if (renderMemory.Reduced)
                    using (var shade = new SolidBrush(Color.FromArgb(14, usable))) g.FillRectangle(shade, area);
                else
                    using (var shade = new LinearGradientBrush(new RectangleF(left, top, right - left, baseline - top), Color.FromArgb(0, usable), Color.FromArgb(28, usable), 90f))
                        g.FillRectangle(shade, area);
                using (Brush ink = linked ? (Brush)QuotaSpectrumBrush(spectrumBounds, spectrumPhase, true, 255) : new SolidBrush(usable))
                using (var line = new Pen(ink, linked ? 2.2f : 1.8f)) g.DrawLine(line, left, baseline, split, baseline);
            }
            if (gap && split < right)
            {
                var saved = g.Save();
                try
                {
                    var area = new RectangleF(split, top, right - split, baseline - top);
                    g.SetClip(area, CombineMode.Intersect);
                    using (var shade = new SolidBrush(Mix(DebtBlack, risk, ruler.Cached ? .08 : .13))) g.FillRectangle(shade, area);
                    float drift = animate ? (float)(linked ? spectrumPhase * 9 : (clock % 4800) / 4800 * 9) : 0;
                    using (Brush ink = linked ? (Brush)QuotaSpectrumBrush(spectrumBounds, spectrumPhase, true, 180) : new SolidBrush(Color.FromArgb(ruler.Cached ? 100 : 150, risk)))
                    using (var hatch = new Pen(ink, 1.3f))
                        for (float x = split - 24 + drift; x < right + 24; x += 9)
                            g.DrawLine(hatch, x, baseline, x + 15, top);
                }
                finally { g.Restore(saved); }
                using (Brush ink = linked ? (Brush)QuotaSpectrumBrush(spectrumBounds, spectrumPhase, true, 210) : new SolidBrush(Color.FromArgb(190, risk)))
                using (var dashed = new Pen(ink, 1))
                { dashed.DashPattern = new[] {3f, 3f}; g.DrawLine(dashed, split, baseline, right, baseline); }
            }
            // True date/hour tick positions; omit crowded labels, never move dates
            // to equally spaced slots. Reset remains the exact right endpoint.
            float lastLabelRight = left + (ruler.Cached ? 50 : 30);
            float resetLabelLeft;
            using (var format = StringFormat.GenericTypographic)
                resetLabelLeft = right - g.MeasureString(ruler.ResetText, cornerMicroFont, 300, format).Width;
            foreach (var tick in ruler.Ticks)
            {
                float x = left + (right - left) * (float)tick.Fraction;
                using (var grid = new Pen(Color.FromArgb(38, Muted))) g.DrawLine(grid, x, top, x, baseline);
                using (var mark = new Pen(Color.FromArgb(140, Muted))) g.DrawLine(mark, x, baseline + 1, x, baseline + 4);
                float width;
                using (var format = StringFormat.GenericTypographic) width = g.MeasureString(tick.Label, cornerMicroFont, 100, format).Width;
                if (x - width / 2 < lastLabelRight + 8 || x + width / 2 > resetLabelLeft - 8) continue;
                CornerText(g, tick.Label, cornerMicroFont, Muted, new RectangleF(x - width / 2 - 1, 383, width + 2, 16), StringAlignment.Center);
                lastLabelRight = x + width / 2;
            }
            using (var cap = new Pen(Color.FromArgb(200, Muted), 1)) g.DrawLine(cap, right, top - 2, right, baseline + 4);
            using (var dot = new SolidBrush(ruler.Projected ? usable : Muted)) g.FillEllipse(dot, left - 2.5f, baseline - 2.5f, 5, 5);
            if (gap)
            {
                using (var pin = new Pen(risk, 1.3f)) g.DrawLine(pin, split, top - 2, split, baseline + 3);
                using (var marker = new SolidBrush(risk)) g.FillPolygon(marker, new[] {
                    new PointF(split - 3, top - 3), new PointF(split + 3, top - 3), new PointF(split, top + 1)});
            }
            // Clock symbol identifies the time axis; the hatch key names its meaning.
            using (var clockPen = new Pen(Muted, 1))
            { g.DrawEllipse(clockPen, 36, 347, 8, 8); g.DrawLine(clockPen, 40, 348.5f, 40, 351.5f); g.DrawLine(clockPen, 40, 351.5f, 42, 352.5f); }
            CornerText(g, ruler.Cached ? "上次时间线" : "续航时间线", cornerMicroFont, Muted, new RectangleF(50, 343, 118, 17), false);
            string caption = QuotaGapCaption();
            CornerText(g, caption, cornerMicroFont, gap ? risk : ruler.ThroughReset ? usable : Muted, new RectangleF(174, 343, 190, 17), true);
            if (gap)
            {
                float width;
                using (var format = StringFormat.GenericTypographic) width = g.MeasureString(caption, cornerMicroFont, 230, format).Width;
                float x = right - width - 17;
                var saved = g.Save();
                try
                {
                    g.SetClip(new RectangleF(x, 347, 11, 8), CombineMode.Intersect);
                    using (var hatch = new Pen(Color.FromArgb(190, risk), 1))
                        for (float hx = x - 8; hx < x + 16; hx += 4) g.DrawLine(hatch, hx, 355, hx + 8, 347);
                }
                finally { g.Restore(saved); }
            }
            CornerText(g, ruler.Cached ? "上次同步" : "现在", cornerMicroFont, Muted, new RectangleF(36, 383, 52, 16), false);
            CornerText(g, ruler.ResetText, cornerMicroFont, Muted, new RectangleF(248, 383, 116, 16), true);
        }

        private void PaintCornerRadar(Graphics g)
        {
            var radar = resetRadar ?? ResetRadarDisplay.Read(data, EpochMs());
            double clock = MotionMs;
            bool present = radar.HasSignal, burst = radarMotion.Bursting(clock);
            Color tone = present ? radarMotion.Tone(clock) : Muted;
            double pulse = motionAllowed && present ? (1 + Math.Sin(clock / (radar.Alert ? 550 : 1100))) / 2 : .5;
            var bounds = new RectangleF(20, 468, 360, 64);
            GlassPanel(g, bounds, tone, present ? 45 : 0);
            if (present) GlowPanel(g, bounds, tone, burst ? 1.1 : .32, pulse);
            if (burst && motionAllowed)
            {
                var saved = g.Save();
                try
                {
                    using (var clip = RoundedCard(bounds, 18)) g.SetClip(clip, CombineMode.Intersect);
                    float x = bounds.Left - 90 + (float)(((clock - radarMotion.ChangedAt) % 3000) / 3000) * 540;
                    using (var sweep = new LinearGradientBrush(new RectangleF(x, bounds.Top, 90, bounds.Height), Color.Transparent, Color.Transparent, 0f))
                    {
                        sweep.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, tone), Color.FromArgb(30, tone), Color.FromArgb(0, tone) }, Positions = new[] { 0f, .5f, 1f } };
                        g.FillRectangle(sweep, x, bounds.Top, 90, bounds.Height);
                    }
                }
                finally { g.Restore(saved); }
            }
            const float cx = 44, cy = 499;
            using (var ring = new Pen(Color.FromArgb(70, tone))) g.DrawEllipse(ring, cx - 12, cy - 12, 24, 24);
            using (var ring = new Pen(Color.FromArgb(100, tone))) g.DrawEllipse(ring, cx - 4, cy - 4, 8, 8);
            using (var dot = new SolidBrush(tone)) g.FillEllipse(dot, cx - 1.5f, cy - 1.5f, 3, 3);
            if (motionAllowed && (present || radar.State == "none" || radar.State == "expired" || radar.State == "loading"))
                ScanOrbit(g, cx, cy, 12, present ? tone : Blue, clock);
            if (present && motionAllowed)
            {
                double wave = clock % (burst ? 1800 : 3600) / (burst ? 1800 : 3600);
                float r = (float)(4 + wave * 12);
                using (var ring = new Pen(Color.FromArgb((int)(110 * (1 - wave)), tone))) g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
            }
            CornerText(g, radar.EventHeading, cornerTitleFont, present ? tone : Ink, new RectangleF(66, 479, 110, 22), false);
            string badge = radar.Title.StartsWith("重置雷达", StringComparison.Ordinal) ? radar.Title.Substring(4).Trim(' ', '·') : radar.Title;
            CornerText(g, radar.EventBadge.Length > 0 ? radar.EventBadge : badge.Length == 0 ? "公共信号" : badge + " · 公共信号",
                cornerMicroFont, radar.ResetType == "banked" && present ? tone : Muted, new RectangleF(66, 502, 113, 18), false);
            Font dateFont = radar.CountingDown ? cornerRunwayFont : radar.CalendarTime ? cornerRadarFont : cornerValueFont;
            using (var format = StringFormat.GenericTypographic)
            {
                if (g.MeasureString(radar.Countdown, dateFont, 1000, format).Width > 184) dateFont = cornerRadarFont;
                if (g.MeasureString(radar.Countdown, dateFont, 1000, format).Width > 184) dateFont = cornerTitleFont;
            }
            MovingText(g, radarDigits, radar.Countdown, dateFont, present ? tone : Muted,
                new RectangleF(180, 475, 184, 30), StringAlignment.Far, radar.SignalIdentity + ":" + radar.ResetType);
            string detail = radar.CalendarLabel.Length > 0 ? radar.CalendarLabel : burst ? radarMotion.Notice : radar.Detail;
            // The account reset already has its own label above; this row is public information only.
            if (detail.StartsWith("周重置", StringComparison.Ordinal))
                detail = radar.State == "none" ? "等待新的公共预告" : radar.State == "limited" ? "稍后自动重试" :
                    radar.State == "loading" ? "等待公开排期" : radar.State == "stale" ? "等待排期更新" : "公开排期暂不可用";
            CornerText(g, detail, cornerMicroFont, Muted, new RectangleF(183, 505, 181, 17), true);
        }

        private static Color DelayColor(double delay)
        { return delay < 300 ? Mint : delay < 800 ? Amber : delay < 1500 ? Orange : Rose; }

        private void PaintNode(Graphics g, Dictionary<string, object> node, string label, float x, float width, double now, bool heartbeatOk)
        {
            double? measured = Json.Number(node, "measured_at_ms"), delay = Json.Number(node, "delay_ms");
            bool recent = heartbeatOk && measured.HasValue && measured.Value <= now + 30000 && now - measured.Value <= 75000;
            string state = Json.Text(node, "state");
            bool ok = recent && state == "ok" && delay.HasValue && delay.Value > 0 && !double.IsInfinity(delay.Value) && !double.IsNaN(delay.Value);
            bool timeout = recent && state == "timeout", unavailable = recent && state == "unavailable";
            int level = ok ? delay.Value < 300 ? 0 : delay.Value < 800 ? 1 : delay.Value < 1500 ? 2 : 3 : timeout ? 4 : unavailable ? 5 : -1;
            double time = MotionMs;
            var motion = x < 100 ? aiMotion : generalMotion;
            motion.Update(level, ok ? DelayColor(delay.Value) : timeout || unavailable ? Rose : Muted, measured ?? -1, time, label);
            Color color = motion.ReadColor(time, motionAllowed);
            double pulse = motionAllowed && level >= 0 ? (1 + Math.Sin(time / (level >= 2 ? 750 : 1700))) / 2 : .5;
            var bounds = new RectangleF(x, 558, width, 64);
            if (level >= 2) GlowPanel(g, bounds, color, level >= 4 ? .7 : .35, pulse);
            using (var dot = new SolidBrush(color)) g.FillEllipse(dot, x + 15, 574, 4, 4);
            CornerText(g, label, cornerSmallFont, Ink, new RectangleF(x + 23, 566, 91, 19), false);
            string status = ok ? delay.Value < 300 ? "流畅" : delay.Value < 800 ? "一般" : delay.Value < 1500 ? "偏慢" : "高延迟" :
                timeout ? "超时" : !recent && measured.HasValue ? "已过期" : state == "loading" || node == null ? "探测中" : "不可用";
            CornerText(g, status, cornerMicroFont, color, new RectangleF(x + 112, 567, 46, 18), true);
            string value = ok ? Math.Round(delay.Value).ToString() : "—";
            Font numberFont = value.Length > 4 ? cornerValueFont : cornerNumberFont;
            var digits = x < 100 ? aiDigits : generalDigits;
            if (!ok) digits.Set(value, label, time, false);
            MovingText(g, digits, value, numberFont, ok ? Ink : color, new RectangleF(x + 13, 586, 98, 31), StringAlignment.Near, label);
            if (ok)
            {
                float valueWidth;
                using (var format = StringFormat.GenericTypographic) valueWidth = g.MeasureString(value, numberFont, 150, format).Width;
                CornerText(g, "ms", cornerMicroFont, Muted, new RectangleF(x + 17 + valueWidth, 598, 21, 16), false);
            }
            var history = Json.List(node, "history").TakeLastCompat(12).ToList();
            for (int i = 0; i < 12; i++)
            {
                float bx = x + 112 + i * 3.7f;
                using (var track = new SolidBrush(Color.FromArgb(45, Muted))) g.FillRectangle(track, bx, 608, 2, 2);
                int index = i - (12 - history.Count);
                if (index < 0) continue;
                double? ms = Json.Number(history[index], "delay_ms");
                if (Json.Text(history[index], "state") == "ok" && ms.HasValue && ms.Value > 0 && !double.IsInfinity(ms.Value) && !double.IsNaN(ms.Value))
                {
                    double grow = !motionAllowed || !recent || i != 11 ? 1 :
                        1 - Math.Pow(1 - Math.Max(0, Math.Min(1, (time - motion.SampleAt) / 420)), 3);
                    float height = (float)((4 + 16 * Math.Min(1500, ms.Value) / 1500) * grow);
                    using (var bar = new SolidBrush(Color.FromArgb(150 + i * 8, recent ? DelayColor(ms.Value) : Muted)))
                        g.FillRectangle(bar, bx, 610 - height, 2, height);
                }
                else if (Json.Text(history[index], "state") == "timeout")
                    using (var pen = new Pen(recent ? Rose : Muted)) { g.DrawLine(pen, bx, 592, bx + 2, 596); g.DrawLine(pen, bx + 2, 592, bx, 596); }
            }
            double reveal = motionAllowed ? Math.Max(0, Math.Min(1, 1 - (time - motion.SampleAt) / 2400)) : 0;
            if (reveal > 0 && recent)
            {
                float r = (float)(3 + (1 - reveal) * 9);
                using (var ring = new Pen(Color.FromArgb((int)(reveal * 185), color)))
                    g.DrawEllipse(ring, x + 17 - r, 576 - r, r * 2, r * 2);
            }
            if (motionAllowed && level >= 2)
            {
                double wave = (time % 2500) / 2500;
                float r = (float)(3 + wave * 7);
                using (var ring = new Pen(Color.FromArgb((int)(120 * (1 - wave)), color))) g.DrawEllipse(ring, x + 17 - r, 576 - r, r * 2, r * 2);
            }
        }

        private void PaintActivity(Graphics g, Color color, double phase)
        {
            double time = MotionMs;
            var bounds = new RectangleF(20, 634, 360, 92);
            GlassPanel(g, bounds, color, activity.Severity >= 2 ? 40 : 0);
            if (activity.Severity >= 2) GlowPanel(g, bounds, color, .45, phase);
            using (var dot = new SolidBrush(color)) g.FillEllipse(dot, 36, 648, 4, 4);
            if (activity.Active && motionAllowed)
                using (var halo = new SolidBrush(Color.FromArgb((int)(12 + phase * 25), color))) g.FillEllipse(halo, 32, 644, 12, 12);
            MovingText(g, activityLabel, activity.Title, cornerTitleFont, activity.Active ? color : Ink,
                new RectangleF(48, 640, 155, 21), StringAlignment.Near, activity.SessionId);
            CornerText(g, activity.State == "idle" ? "最近活动" : "距最新活动", cornerMicroFont, Muted, new RectangleF(227, 643, 69, 18), true);
            CornerText(g, activity.ElapsedText, activity.ElapsedText.Length > 5 ? cornerTitleFont : cornerValueFont,
                activity.Active ? color : Ink, new RectangleF(298, 638, 66, 26), true);
            CornerText(g, "平均响应", cornerMicroFont, Muted, new RectangleF(36, 664, 96, 17), false);
            MovingText(g, responseDigits, activity.AverageText, cornerValueFont,
                activity.AverageMs.HasValue ? ResponseColor(activity.AverageMs.Value) : Muted,
                new RectangleF(35, 681, 108, 25), StringAlignment.Near, activity.SessionId);
            CornerLayer(g, 220, delegate { PaintResponseHistory(g, time); });
            string detail = activity.Severity > 0 || activity.State == "unknown" ? activity.Detail :
                activity.CompletedCount > 1 ? "日志估算 · 虚线为平均响应" :
                activity.CompletedCount == 1 ? "仅 1 次响应 · 持续积累中" : "等待完整响应记录";
            CornerText(g, detail, cornerMicroFont, activity.Severity >= 2 ? color : Muted,
                new RectangleF(36, 709, 328, 15), false);
        }

        private void PaintResponseHistory(Graphics g, double time)
        {
            responseMotion.Update(activity, time, motionAllowed);
            var values = responseMotion.Target;
            var plot = new RectangleF(154, 464, 200, 23);
            CornerText(g, activity.CompletedCount > 1 ? "近 " + activity.CompletedCount + " 次响应" : "响应趋势",
                cornerMicroFont, Muted, new RectangleF(152, 444, 95, 17), false);
            if (values.Length == 0)
            {
                using (var pen = new Pen(Color.FromArgb(45, Muted))) { pen.DashStyle = DashStyle.Dot; g.DrawLine(pen, plot.Left, plot.Bottom, plot.Right, plot.Bottom); }
                CornerText(g, "等待完整记录", cornerMicroFont, Muted, new RectangleF(232, 444, 125, 17), true);
                return;
            }
            double maximum = values.Max();
            double ceiling = Math.Max(1000, maximum * 1.18);
            double blend = responseMotion.Blend(time, motionAllowed);
            // Scale morphs with the vertices so an outlier never causes a one-frame jump.
            double oldCeiling = Math.Max(1000, responseMotion.From.Max() * 1.18);
            ceiling = oldCeiling + (ceiling - oldCeiling) * blend;
            string peak = maximum < 3600000 ? (maximum / 1000).ToString("0.#") + "s" : ActivityDisplay.Elapsed(maximum);
            CornerText(g, "最高 " + peak, cornerMicroFont, Muted, new RectangleF(232, 444, 125, 17), true);
            var chartState = g.Save();
            try
            {
            g.SetClip(new RectangleF(148, 461, 216, 29), CombineMode.Intersect);
            var points = new PointF[values.Length];
            for (int i = 0; i < points.Length; i++)
            {
                double ms = responseMotion.From[i] + (values[i] - responseMotion.From[i]) * blend;
                points[i] = new PointF(plot.Left + (20 - points.Length + i) * plot.Width / 19,
                    plot.Bottom - (float)(ms / ceiling * plot.Height));
            }
            Color latest = ResponseColor(values[values.Length - 1]);
            using (var baseline = new Pen(Color.FromArgb(30, Blue))) g.DrawLine(baseline, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            // Reference uses the true mean; the history is chronological, oldest left.
            float meanY = plot.Bottom - (float)Math.Min(plot.Height, (activity.AverageMs ?? 0) / ceiling * plot.Height);
            using (var mean = new Pen(Color.FromArgb(110, Blue)))
            { mean.DashStyle = DashStyle.Dot; g.DrawLine(mean, plot.Left, meanY, plot.Right, meanY); }
            if (points.Length > 1)
            {
                using (var fill = new GraphicsPath())
                {
                    fill.AddLines(points); fill.AddLine(points[points.Length - 1], new PointF(plot.Right, plot.Bottom));
                    fill.AddLine(new PointF(plot.Right, plot.Bottom), new PointF(points[0].X, plot.Bottom)); fill.CloseFigure();
                    using (var brush = new LinearGradientBrush(plot, Color.FromArgb(42, Blue), Color.FromArgb(0, Blue), 90f)) g.FillPath(brush, fill);
                }
                using (var glow = new Pen(Color.FromArgb(20, Blue), 5)) g.DrawLines(glow, points);
                for (int i = 1; i < points.Length; i++)
                    using (var pen = new Pen(Color.FromArgb(210, Blue), 1.6f)) g.DrawLine(pen, points[i - 1], points[i]);
                // Moving illumination follows the real polyline; it never moves data points.
                if (motionAllowed && activity.Active)
                {
                    double head = (time % 4800) / 4800 * (points.Length - 1);
                    for (int trail = 0; trail < 6; trail++)
                    {
                        double position = head - trail * 0.14;
                        if (position < 0) continue;
                        int segment = Math.Min(points.Length - 2, (int)position);
                        float fraction = (float)(position - segment);
                        float px = points[segment].X + (points[segment + 1].X - points[segment].X) * fraction;
                        float py = points[segment].Y + (points[segment + 1].Y - points[segment].Y) * fraction;
                        using (var glint = new SolidBrush(Color.FromArgb(160 - trail * 23, Ink)))
                            g.FillEllipse(glint, px - 1.2f, py - 1.2f, 2.4f, 2.4f);
                    }
                }
            }
            foreach (var point in points)
                using (var dot = new SolidBrush(Color.FromArgb(190, Ink))) g.FillEllipse(dot, point.X - 1, point.Y - 1, 2, 2);
            PointF end = points[points.Length - 1];
            double reveal = motionAllowed ? Math.Max(0, Math.Min(1, 1 - (time - responseMotion.ChangedAt) / 1800)) : 0;
            if (reveal > 0)
            {
                float r = (float)(3 + (1 - reveal) * 8);
                using (var ring = new Pen(Color.FromArgb((int)(reveal * 170), latest))) g.DrawEllipse(ring, end.X - r, end.Y - r, r * 2, r * 2);
            }
            // A restrained endpoint breathes while working; historical points stay put.
            double pulse = motionAllowed && activity.Active ? (1 + Math.Sin(time / 1000)) / 2 : 0;
            using (var halo = new SolidBrush(Color.FromArgb((int)(22 + pulse * 25), latest))) g.FillEllipse(halo, end.X - 5, end.Y - 5, 10, 10);
            using (var dot = new SolidBrush(latest)) g.FillEllipse(dot, end.X - 2, end.Y - 2, 4, 4);
            }
            finally { g.Restore(chartState); }
        }
        private static void PaintWaitRing(Graphics g, RectangleF bounds, float sweep, Color color)
        {
            if (float.IsNaN(sweep) || float.IsInfinity(sweep) || sweep <= 0) return;
            sweep = Math.Min(360, sweep);
            // GDI+ DrawArc with round caps can report OutOfMemory for tiny sweeps.
            // Approximate the arc with short flat-cap segments and explicit end dots.
            using (var pen = new Pen(color, 2))
            using (var brush = new SolidBrush(color))
            {
                if (sweep >= 360) { g.DrawEllipse(pen, bounds); return; }
                int segments = Math.Max(1, (int)Math.Ceiling(sweep / 6));
                var points = new PointF[segments + 1];
                for (int i = 0; i <= segments; i++)
                {
                    double angle = (-90 + sweep * i / segments) * Math.PI / 180;
                    points[i] = new PointF(bounds.X + bounds.Width * (float)(1 + Math.Cos(angle)) / 2,
                        bounds.Y + bounds.Height * (float)(1 + Math.Sin(angle)) / 2);
                }
                if (sweep >= 0.25f) g.DrawLines(pen, points);
                foreach (var point in new[] { points[0], points[segments] })
                    g.FillEllipse(brush, point.X - 1, point.Y - 1, 2, 2);
            }
        }
    }

    internal static class CornerSequence
    {
        internal static IEnumerable<T> TakeLastCompat<T>(this IEnumerable<T> values, int count)
        { var list = values.ToList(); return list.Skip(Math.Max(0, list.Count - count)); }
    }
}

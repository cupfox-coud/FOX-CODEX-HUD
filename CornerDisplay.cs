using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexTokenHud
{
    internal sealed partial class Hud
    {
        private const int CornerWidth = 368, CornerHeight = 374;
        private ActivityDisplay activity = new ActivityDisplay();
        private readonly Font cornerTimerFont = new Font("Consolas", 29, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerNumberFont = new Font("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerValueFont = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerTitleFont = new Font("Microsoft YaHei UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        private static readonly Color Ink = Color.FromArgb(232, 240, 250);
        private static readonly Color Muted = Color.FromArgb(149, 169, 193);
        private static readonly Color Mint = Color.FromArgb(115, 230, 195);
        private static readonly Color Blue = Color.FromArgb(143, 190, 255);
        private static readonly Color Amber = Color.FromArgb(244, 204, 118);
        private static readonly Color Orange = Color.FromArgb(255, 164, 106);
        private static readonly Color Rose = Color.FromArgb(255, 124, 145);
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
        private static bool AnimationEnabled()
        { int enabled; return SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0; }
        private readonly System.Windows.Forms.Timer motionTicker = new System.Windows.Forms.Timer();
        private bool motionAllowed = true;
        private double nextMotionPreference;
        private readonly NodeMotion aiMotion = new NodeMotion();
        private readonly NodeMotion generalMotion = new NodeMotion();
        private static double MotionMs { get { return System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency; } }

        // One lightweight clock; animation never reads files or triggers probes.
        private void StartCornerMotion()
        {
            if (!configuration.CornerMode) return;
            motionTicker.Interval = SystemInformation.TerminalServerSession ? 100 : 40;
            motionTicker.Tick += delegate {
                if (!stopping && Visible && motionAllowed && !menu.Visible &&
                    (activity.Active || aiMotion.Level >= 0 || generalMotion.Level >= 0)) Invalidate();
            };
            motionTicker.Start();
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

        // Layered vector strokes provide a soft halo without bitmap blur or cached textures.
        private static void GlowPanel(Graphics g, RectangleF bounds, Color color, double strength, double pulse)
        {
            for (int i = 3; i >= 1; i--)
                using (var path = RoundedCard(bounds, 10))
                using (var pen = new Pen(Color.FromArgb((int)((5 + pulse * 7) * strength), color), i * 2))
                    g.DrawPath(pen, path);
            using (var brush = new LinearGradientBrush(bounds, Color.FromArgb((int)((12 + pulse * 14) * strength), color),
                Color.FromArgb(0, color), 90f))
            using (var path = RoundedCard(bounds, 10)) g.FillPath(brush, path);
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

        private void PaintCorner(Graphics graphics)
        {
            var saved = graphics.Save();
            try
            {
            graphics.ScaleTransform((float)scale, (float)scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            double now = EpochMs();
            double motion = MotionMs;
            if (motion >= nextMotionPreference)
            { motionAllowed = AnimationEnabled(); nextMotionPreference = motion + 2000; }
            Color signal = ActivityColor();
            double phase = motionAllowed && activity.Active ?
                (Math.Sin(motion / (activity.Severity == 3 ? 620 : activity.Severity == 2 ? 850 : 1150)) + 1) / 2 : 0.5;
            using (var path = RoundedCard(new RectangleF(.5f, .5f, CornerWidth - 1, CornerHeight - 1), 14))
            using (var fill = new LinearGradientBrush(new Rectangle(0, 0, CornerWidth, CornerHeight),
                Color.FromArgb(29, 42, 60), Color.FromArgb(14, 23, 36), 80f))
            using (var border = new Pen(activity.Severity >= 2 ? Color.FromArgb((int)(95 + phase * 65), signal) : Color.FromArgb(62, 82, 108)))
            { graphics.FillPath(fill, path); graphics.DrawPath(border, path); }

            // Header: the dot follows activity, never the age of a completed token sample.
            using (var halo = new SolidBrush(Color.FromArgb((int)(18 + phase * 22), signal))) graphics.FillEllipse(halo, 13, 13, 14, 14);
            using (var dot = new SolidBrush(signal)) graphics.FillEllipse(dot, 17, 17, 6, 6);
            CornerText(graphics, "FOX CODEX HUD", cornerHeaderFont, Ink, new RectangleF(32, 11, 105, 19), false);
            CornerText(graphics, modelText, cornerSmallFont, Muted, new RectangleF(140, 11, 127, 19), false);
            var statusBounds = new RectangleF(275, 10, 77, 21);
            Panel(graphics, statusBounds, Color.FromArgb(22, signal), Color.FromArgb(42, signal), 10);
            CornerText(graphics, !cornerClickThrough ? "临时解锁" : activity.Active ? "进行中" : activity.State == "idle" ? "已完成" : "待确认",
                cornerSmallFont, signal, statusBounds, StringAlignment.Center);

            PaintCornerQuota(graphics);

            var network = Json.Object(data, "network_latency");
            var nodes = Json.List(network, "nodes");
            double networkAge = now - (Json.Number(network, "updated_at_ms") ?? 0);
            bool heartbeatOk = now - (Json.Number(data, "updated_at_ms") ?? 0) <= 6000;
            string networkTime = Json.Text(network, "state") == "loading" ? "正在主动探测" :
                !heartbeatOk || networkAge > 75000 ? "等待重新探测" :
                "30 秒主动探测 · " + Math.Max(0, (int)(networkAge / 1000)).ToString() + " 秒前";
            PaintNode(graphics, nodes.FirstOrDefault(n => Json.Text(n, "id") == "ai"), "AI节点", 16, now, heartbeatOk);
            PaintNode(graphics, nodes.FirstOrDefault(n => Json.Text(n, "id") == "general"), "苏菲家宽", 190, now, heartbeatOk);
            var notice = aiMotion.Level >= 2 && aiMotion.Level >= generalMotion.Level ? aiMotion :
                generalMotion.Level >= 2 ? generalMotion : aiMotion.ChangedAt > generalMotion.ChangedAt ? aiMotion : generalMotion;
            bool showNotice = notice.Notice.Length > 0 && motion - notice.ChangedAt < 8000;
            CornerText(graphics, "节点延迟", cornerSmallFont, Muted, new RectangleF(16, 125, 90, 17), false);
            CornerText(graphics, showNotice ? notice.Notice : networkTime, cornerSmallFont,
                showNotice ? notice.Target : Muted, new RectangleF(113, 125, 239, 17), true);

            CornerText(graphics, stageText.StartsWith("区间平均", StringComparison.Ordinal) ? "区间均速" : "输出速度",
                cornerSmallFont, Muted, new RectangleF(18, 233, 59, 23), false);
            CornerText(graphics, rateText, cornerValueFont, fresh ? Ink : Muted, new RectangleF(82, 231, 64, 27), false);
            CornerText(graphics, "tok/s", cornerSmallFont, Muted, new RectangleF(143, 235, 35, 22), false);
            using (var pen = new Pen(Color.FromArgb(49, 66, 89))) graphics.DrawLine(pen, 184, 237, 184, 251);
            CornerText(graphics, "缓存命中", cornerSmallFont, Muted, new RectangleF(200, 233, 58, 23), false);
            string cache = cacheText.StartsWith("缓存 ", StringComparison.Ordinal) ? cacheText.Substring(3) : "--";
            if (!cache.Contains("%")) cache = "--";
            CornerText(graphics, cache, cornerValueFont, Blue, new RectangleF(264, 231, 86, 27), true);

            PaintActivity(graphics, signal, phase);
            }
            finally { graphics.Restore(saved); }
        }

        private void PaintCornerQuota(Graphics g)
        {
            {
                var window = quotaWindows.FirstOrDefault(w => Json.Number(w, "window_minutes") == 10080);
                double remaining = Math.Max(0, Math.Min(100, Json.Number(window, "remaining_percent") ?? 0));
                Color color = window == null ? Muted : QuotaColor(remaining);
                CornerText(g, window == null ? "账户额度剩余" : QuotaLabel(window), cornerSmallFont, Muted, new RectangleF(17, 42, 180, 18), false);
                CornerText(g, window == null ? "--" : remaining.ToString("0.#") + "%", cornerQuotaFont, color, new RectangleF(13, 57, 147, 48), false);
                var radar = resetRadar ?? ResetRadarDisplay.Read(data, EpochMs());
                Color radarColor = radar.Alert ? Amber : Blue;
                Panel(g, new RectangleF(190, 42, 162, 65), Color.FromArgb(17, radarColor), Color.FromArgb(45, radarColor), 9);
                CornerText(g, radar.Title, cornerSmallFont, Muted, new RectangleF(202, 47, 132, 17), false);
                using (var dot = new SolidBrush(radar.State == "none" || radar.Alert ? radarColor : Muted)) g.FillEllipse(dot, 336, 53, 4, 4);
                CornerText(g, radar.Countdown, cornerValueFont, radarColor, new RectangleF(201, 64, 140, 24), false);
                CornerText(g, radar.Detail, cornerSmallFont, Muted, new RectangleF(202, 88, 140, 16), false);
                PaintQuotaBar(g, 17, 112, 334, remaining, color);
            }
        }

        private static Color DelayColor(double delay)
        { return delay < 300 ? Mint : delay < 800 ? Amber : delay < 1500 ? Orange : Rose; }

        private void PaintNode(Graphics g, Dictionary<string, object> node, string label, float x, double now, bool heartbeatOk)
        {
            double? measured = Json.Number(node, "measured_at_ms");
            bool recent = heartbeatOk && measured.HasValue && measured.Value <= now + 30000 && now - measured.Value <= 75000;
            string state = Json.Text(node, "state");
            double? delay = Json.Number(node, "delay_ms");
            bool ok = recent && state == "ok" && delay.HasValue && delay.Value > 0 && !double.IsInfinity(delay.Value) && !double.IsNaN(delay.Value);
            bool timeout = recent && state == "timeout";
            bool unavailable = recent && state == "unavailable";
            int level = ok ? delay.Value < 300 ? 0 : delay.Value < 800 ? 1 : delay.Value < 1500 ? 2 : 3 :
                timeout ? 4 : unavailable ? 5 : -1;
            double time = MotionMs;
            var motion = x < 100 ? aiMotion : generalMotion;
            motion.Update(level, ok ? DelayColor(delay.Value) : timeout || unavailable ? Rose : Muted,
                measured ?? -1, time, label);
            Color color = motion.ReadColor(time, motionAllowed);
            double pulse = motionAllowed && level >= 0 ? (1 + Math.Sin(time / (level >= 4 ? 460 : level >= 2 ? 700 : 1500))) / 2 : 0.5;
            var bounds = new RectangleF(x, 148, 162, 76);
            Panel(g, bounds, Color.FromArgb(38, 7, 17, 30), Color.FromArgb(level >= 2 ? 115 : 65, color), 10);
            GlowPanel(g, bounds, color, level >= 4 ? 1.6 : level >= 2 ? 1.1 : level >= 0 ? 0.55 : 0.15, pulse);
            // A small expanding ring calls attention to trouble without flashing the card.
            if (motionAllowed && level >= 2)
            {
                double cycle = (time % (level >= 4 ? 1900 : 3000)) / (level >= 4 ? 1900 : 3000);
                float radius = (float)(3 + cycle * 7);
                using (var ring = new Pen(Color.FromArgb((int)(110 * (1 - cycle)), color), 1))
                    g.DrawEllipse(ring, x + 14 - radius, 163 - radius, radius * 2, radius * 2);
                if (level >= 4)
                {
                    float inset = (float)(8 * (1 - cycle));
                    using (var contour = RoundedCard(new RectangleF(x + inset, 148 + inset, 162 - inset * 2, 76 - inset * 2), 9))
                    using (var pen = new Pen(Color.FromArgb((int)(60 * (1 - cycle)), color), 1))
                        g.DrawPath(pen, contour);
                }
            }
            using (var dot = new SolidBrush(color)) g.FillEllipse(dot, x + 12, 161, 4, 4);
            CornerText(g, label, cornerSmallFont, Ink, new RectangleF(x + 22, 154, 128, 18), false);
            string value = ok ? Math.Round(delay.Value).ToString() : timeout ? "超时" : "--";
            CornerText(g, value, timeout ? cornerValueFont : cornerNumberFont, color, new RectangleF(x + 10, 172, 103, 34), false);
            if (ok)
            {
                float width;
                using (var format = StringFormat.GenericTypographic)
                    width = g.MeasureString(value, cornerNumberFont, 150, format).Width;
                CornerText(g, "ms", cornerSmallFont, Muted, new RectangleF(x + 14 + width, 184, 22, 17), false);
            }
            string status = ok ? delay.Value < 300 ? "流畅" : delay.Value < 800 ? "一般" : delay.Value < 1500 ? "偏慢" : "高延迟" :
                timeout ? "5 秒未响应" : !recent && measured.HasValue ? "数据已过期" : state == "loading" || node == null ? "正在探测" :
                Json.Text(node, "reason") == "proxy_missing" ? "未找到节点" : "暂不可用";
            CornerText(g, status, cornerSmallFont, color, new RectangleF(x + 12, 205, 135, 15), false);
            // Reveal each new real measurement briefly; the numeric value remains exact.
            double reveal = motionAllowed ? Math.Max(0, 1 - (time - motion.SampleAt) / 1600) : 0;
            if (reveal > 0 && recent)
                using (var pen = new Pen(Color.FromArgb((int)(reveal * 150), color), 2))
                    g.DrawLine(pen, x + 12, 222, x + 150, 222);

            // Recent history: gaps stay empty; failed probes have a short cross.
            var history = Json.List(node, "history").TakeLastCompat(12).ToList();
            for (int i = 0; i < 12; i++)
            {
                float bx = x + 113 + i * 3;
                using (var track = new SolidBrush(Color.FromArgb(46, 65, 89))) g.FillRectangle(track, bx, 198, 2, 2);
                int index = i - (12 - history.Count);
                if (index < 0) continue;
                var point = history[index];
                double? ms = Json.Number(point, "delay_ms");
                if (Json.Text(point, "state") == "ok" && ms.HasValue && ms.Value > 0 && !double.IsInfinity(ms.Value) && !double.IsNaN(ms.Value))
                {
                    float height = (float)(4 + 18 * Math.Min(1500, ms.Value) / 1500);
                    using (var bar = new SolidBrush(recent ? DelayColor(ms.Value) : Muted)) g.FillRectangle(bar, bx, 200 - height, 2, height);
                }
                else if (Json.Text(point, "state") == "timeout")
                    using (var pen = new Pen(recent ? Rose : Muted)) { g.DrawLine(pen, bx, 179, bx + 2, 183); g.DrawLine(pen, bx + 2, 179, bx, 183); }
            }
        }

        private void PaintActivity(Graphics g, Color color, double phase)
        {
            int tint = !activity.Active ? 12 : (int)(16 + activity.WaitLevel * 5 + phase * activity.WaitLevel * 7);
            Panel(g, new RectangleF(14, 272, 340, 88), Color.FromArgb(tint, color), Color.FromArgb(55 + (int)(activity.WaitLevel * 25), color), 11);
            if (activity.Active)
            {
                GlowPanel(g, new RectangleF(14, 272, 340, 88), color, 0.3 + activity.WaitLevel * 0.3, phase);
                // Thin moving light carries the activity signal, away from readable text.
                var rail = new RectangleF(28, 330, 312, 2);
                using (var track = new SolidBrush(Color.FromArgb(28, color))) g.FillRectangle(track, rail);
                float pos = motionAllowed ? (float)((MotionMs % 4200) / 4200) : 0.5f;
                float width = 34 + (float)activity.WaitLevel * 12;
                var light = new RectangleF(rail.X + pos * (rail.Width - width), rail.Y, width, 2);
                using (var brush = new LinearGradientBrush(light, Color.FromArgb(0, color), Color.FromArgb(170, color), 0f))
                    g.FillRectangle(brush, light);
            }
            using (var edge = new Pen(Color.FromArgb((int)(130 + phase * 100), color), activity.Severity >= 2 ? 3 : 2))
            { edge.StartCap = LineCap.Round; edge.EndCap = LineCap.Round; g.DrawLine(edge, 15, 290, 15, 324); }
            CornerText(g, activity.Title, cornerTitleFont, color, new RectangleF(28, 281, 169, 21), false);
            string ageLabel = activity.State == "idle" ? "最近活动" : "距最新活动";
            CornerText(g, ageLabel, cornerSmallFont, Muted, new RectangleF(227, 280, 112, 16), true);
            Font timer = activity.ElapsedText.Length > 5 ? cornerValueFont : cornerTimerFont;
            CornerText(g, activity.ElapsedText, timer, activity.Active ? color : Ink, new RectangleF(209, 294, 131, 33), true);
            var clock = new RectangleF(222, 301, 18, 18);
            using (var track = new Pen(Color.FromArgb(45, color), 2)) g.DrawEllipse(track, clock);
            double fullAt = activity.State == "tools" ? 600000 : 120000;
            float sweep = activity.Active ? (float)(360 * Math.Min(1, (activity.EventAgeMs ?? 0) / fullAt)) : 0;
            PaintWaitRing(g, clock, sweep, color);
            using (var hands = new Pen(Color.FromArgb(160, color), 1))
            { g.DrawLine(hands, 231, 305, 231, 310); g.DrawLine(hands, 231, 310, 234, 312); }
            CornerText(g, "平均响应", cornerSmallFont, Muted, new RectangleF(28, 306, 66, 18), false);
            CornerText(g, activity.AverageText, cornerValueFont, activity.CompletedCount > 0 ? Blue : Muted,
                new RectangleF(98, 301, 88, 27), false);
            CornerText(g, activity.CompletedCount > 0 ? activity.CompletedCount + "次" : "暂无",
                cornerSmallFont, Muted, new RectangleF(187, 307, 30, 17), false);
            string detail = activity.Severity > 0 || activity.State == "unknown" ? activity.Detail :
                activity.CompletedCount > 1 ? "最近 " + activity.CompletedCount + " 次响应 · 日志估算" :
                activity.CompletedCount == 1 ? "仅 1 次响应 · 持续积累中" : "等待有完整计时的模型响应";
            CornerText(g, detail, cornerSmallFont, activity.Severity >= 2 ? color : Muted, new RectangleF(28, 339, 312, 16), false);
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

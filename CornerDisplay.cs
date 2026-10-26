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
        private const int CornerWidth = 400, CornerHeight = 536;
        private ActivityDisplay activity = new ActivityDisplay();
        private readonly Font cornerTimerFont = new Font("Consolas", 29, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerNumberFont = new Font("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerValueFont = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerTitleFont = new Font("Microsoft YaHei UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerRadarFont = new Font("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font cornerHeroFont = new Font("Segoe UI Semilight", 54, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font cornerBrandFont = new Font("Segoe UI Semibold", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font cornerMicroFont = new Font("Microsoft YaHei UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
        private Bitmap cornerBackdrop;
        private Bitmap cornerBlueLight, cornerMintLight;
        private double? previewMotionClock = null;
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
        private bool systemMotionAllowed = true;
        private long motionPaintCount;
        private double nextMotionPreference;
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
                if (!stopping && Visible && motionAllowed && !menu.Visible) Invalidate();
            };
            motionTicker.Start();
        }

        private static bool ResolveMotion(string mode, bool systemEnabled)
        { return mode == "on" || (mode != "off" && systemEnabled); }

        private void SetCornerMotion(string mode)
        {
            configuration.CornerMotion = mode == "on" || mode == "off" ? mode : "system";
            configuration.SaveCornerSettings();
            systemMotionAllowed = AnimationEnabled();
            motionAllowed = ResolveMotion(configuration.CornerMotion, systemMotionAllowed);
            nextMotionPreference = 0;
            Invalidate();
        }

        private sealed class RadarMotion
        {
            internal bool Active, Confirmed, Urgent, BurstCompleted;
            internal string Key = "", Identity = "", Notice = "";
            internal double ChangedAt = -100000;
            internal bool Bursting(double time) { return Active && time >= ChangedAt && time - ChangedAt < 12000; }
            internal Color Tone(double time) { return Confirmed || (BurstCompleted && Bursting(time)) ? Mint : Urgent ? Orange : Amber; }
            internal void Update(ResetRadarDisplay value, double time)
            {
                if (value == null || !value.HasSignal) { Active = Urgent = false; return; }
                if (Key != value.SignalKey)
                {
                    Notice = value.State == "completed" ? "检测到公共重置" :
                        Identity == value.SignalIdentity && Key.StartsWith("schedule:", StringComparison.Ordinal) ?
                        "预告时间已更新" : "发现重置预告";
                    Key = value.SignalKey; Identity = value.SignalIdentity; ChangedAt = time;
                    BurstCompleted = value.State == "completed";
                }
                else if (value.Alert && !Urgent && Active)
                { Notice = value.PredictionKind == "deadline" ? "预估截止临近" : "预估时间临近"; ChangedAt = time; BurstCompleted = false; }
                Active = true; Confirmed = value.State == "completed"; Urgent = value.Alert;
            }
            internal void Announce(ResetNotice notice, double time)
            {
                if (!Active || notice == null) return;
                ChangedAt = time; BurstCompleted = notice.Kind == "completed";
                Notice = notice.Kind == "completed" ? "检测到公共重置" : notice.Kind == "schedule_changed" ?
                    "预告时间已更新" : notice.Kind == "soon" ? "预估时间临近" : "发现重置预告";
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
            if (cornerBackdrop == null)
            {
                cornerBackdrop = new Bitmap(CornerWidth, CornerHeight);
                using (var canvas = Graphics.FromImage(cornerBackdrop))
                using (var shape = RoundedCard(new RectangleF(.5f, .5f, CornerWidth - 1, CornerHeight - 1), 26))
                using (var fill = new LinearGradientBrush(new Rectangle(0, 0, CornerWidth, CornerHeight),
                    Color.FromArgb(29, 33, 43), Color.FromArgb(13, 17, 25), 80f))
                {
                    canvas.SmoothingMode = SmoothingMode.AntiAlias;
                    canvas.SetClip(shape);
                    canvas.FillPath(fill, shape);
                    Ambient(canvas, new RectangleF(-130, -180, 560, 410), Blue, 27);
                    Ambient(canvas, new RectangleF(220, 180, 360, 470), Mint, 9);
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
            if (cornerBlueLight == null) cornerBlueLight = SoftLight(Blue, 68);
            if (cornerMintLight == null) cornerMintLight = SoftLight(Mint, 38);
            double clock = motionAllowed ? MotionMs : 0;
            var saved = g.Save();
            using (var clip = RoundedCard(new RectangleF(1, 1, CornerWidth - 2, CornerHeight - 2), 26))
                g.SetClip(clip, CombineMode.Intersect);
            g.DrawImage(cornerBlueLight, (float)(-185 + 135 * Math.Sin(clock / 3100)),
                (float)(-200 + 85 * Math.Cos(clock / 4200)), 540, 480);
            g.DrawImage(cornerMintLight, (float)(135 + 90 * Math.Cos(clock / 4700)),
                (float)(260 + 85 * Math.Sin(clock / 3700)), 350, 360);
            g.Restore(saved);
        }

        private static Bitmap SoftLight(Color color, int alpha)
        {
            var result = new Bitmap(192, 192);
            using (var canvas = Graphics.FromImage(result))
            {
                canvas.SmoothingMode = SmoothingMode.AntiAlias;
                Ambient(canvas, new RectangleF(0, 0, 192, 192), color, alpha);
            }
            return result;
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
                using (var glow = new Pen(light, 3.4f)) g.DrawPath(glow, path);
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
            using (var shadow = RoundedCard(new RectangleF(bounds.X, bounds.Y + 3, bounds.Width, bounds.Height), 18))
            using (var brush = new SolidBrush(Color.FromArgb(55, Color.Black))) g.FillPath(brush, shadow);
            using (var path = RoundedCard(bounds, 18))
            using (var fill = new LinearGradientBrush(bounds, Color.FromArgb(25 + emphasis / 5, accent),
                Color.FromArgb(4, accent), 110f))
            using (var rim = new LinearGradientBrush(bounds, Color.FromArgb(35 + emphasis, accent),
                Color.FromArgb(14 + emphasis / 2, accent), 90f))
            using (var pen = new Pen(rim))
            { g.FillPath(fill, path); g.DrawPath(pen, path); }
            using (var inner = RoundedCard(new RectangleF(bounds.X + 1.4f, bounds.Y + 1.4f, bounds.Width - 2.8f, bounds.Height - 2.8f), 17))
            using (var shade = new Pen(Color.FromArgb(35, Color.Black))) g.DrawPath(shade, inner);
            SpecularRim(g, bounds, 18, accent, bounds.X / 120 + bounds.Y / 200, 95 + emphasis);
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
                    motionAllowed = ResolveMotion(configuration.CornerMotion, systemMotionAllowed);
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
                    radarMotion.Bursting(motion) ? radarMotion.Tone(motion) : Blue, 0, radarMotion.Bursting(motion) ? 210 : 125);

                // A small orbital mark stays quiet while the status capsule carries activity.
                using (var halo = new SolidBrush(Color.FromArgb(14, Blue))) graphics.FillEllipse(halo, 22, 24, 27, 27);
                using (var orbit = new Pen(Color.FromArgb(140, Blue), 1.2f)) graphics.DrawArc(orbit, 27, 29, 17, 17, 35, 275);
                using (var dot = new SolidBrush(Ink)) graphics.FillEllipse(dot, 32.5f, 34.5f, 6, 6);
                using (var glint = new SolidBrush(Mint)) graphics.FillEllipse(glint, 42, 29, 3.5f, 3.5f);
                if (motionAllowed) ScanOrbit(graphics, 35.5f, 37.5f, 11.5f, Blue, motion);
                CornerText(graphics, "FOX CODEX", cornerBrandFont, Ink, new RectangleF(56, 22, 150, 23), false);
                CornerText(graphics, modelText, cornerMicroFont, Muted, new RectangleF(57, 45, 200, 16), false);
                var status = new RectangleF(284, 28, 92, 27);
                Panel(graphics, status, Color.FromArgb(12, signal), Color.FromArgb(37, signal), 13);
                using (var halo = new SolidBrush(Color.FromArgb((int)(15 + phase * 23), signal))) graphics.FillEllipse(halo, 295, 35, 13, 13);
                using (var dot = new SolidBrush(signal)) graphics.FillEllipse(dot, 299, 39, 5, 5);
                CornerText(graphics, !cornerClickThrough ? "临时解锁" : activity.Active ? "进行中" : activity.State == "idle" ? "已完成" : "待确认",
                    cornerSmallFont, signal, new RectangleF(308, 29, 62, 25), StringAlignment.Center);
                PaintCornerQuota(graphics);

                var network = Json.Object(data, "network_latency");
                var nodes = Json.List(network, "nodes");
                double networkAge = now - (Json.Number(network, "updated_at_ms") ?? 0);
                bool heartbeatOk = now - (Json.Number(data, "updated_at_ms") ?? 0) <= 6000;
                string networkTime = Json.Text(network, "state") == "loading" ? "正在主动探测" :
                    !heartbeatOk || networkAge > 75000 ? "等待重新探测" :
                    (Json.Number(network, "interval_seconds") ?? 10).ToString("0") + " 秒探测 · " + Math.Max(0, (int)(networkAge / 1000)) + " 秒前";
                PaintNode(graphics, nodes.FirstOrDefault(n => Json.Text(n, "id") == "ai"), "AI服务", 24, 170, now, heartbeatOk);
                PaintNode(graphics, nodes.FirstOrDefault(n => Json.Text(n, "id") == "general"), "上网节点", 206, 170, now, heartbeatOk);
                var notice = aiMotion.ChangedAt >= generalMotion.ChangedAt ? aiMotion : generalMotion;
                bool showNotice = notice.Notice.Length > 0 && motion - notice.ChangedAt < 8000;
                CornerText(graphics, "线路状态", cornerSmallFont, Ink, new RectangleF(25, 225, 90, 18), false);
                CornerText(graphics, showNotice ? notice.Notice : networkTime, cornerMicroFont,
                    showNotice ? notice.Target : Muted, new RectangleF(122, 226, 253, 17), true);

                CornerText(graphics, stageText.StartsWith("区间平均", StringComparison.Ordinal) ? "区间均速" : "输出速度",
                    cornerSmallFont, Muted, new RectangleF(25, 349, 120, 18), false);
                CornerText(graphics, rateText, cornerSpeedFont, fresh ? Ink : Muted, new RectangleF(22, 368, 143, 32), false);
                float rateWidth;
                using (var format = StringFormat.GenericTypographic) rateWidth = graphics.MeasureString(rateText, cornerSpeedFont, 180, format).Width;
                CornerText(graphics, "tok/s", cornerMicroFont, Muted, new RectangleF(Math.Min(156, 29 + rateWidth), 380, 35, 16), false);
                using (var pen = new Pen(Color.FromArgb(27, Muted))) graphics.DrawLine(pen, 199, 353, 199, 391);
                CornerText(graphics, "缓存命中", cornerSmallFont, Muted, new RectangleF(219, 349, 120, 18), false);
                string cache = cacheText.StartsWith("缓存 ", StringComparison.Ordinal) ? cacheText.Substring(3) : "--";
                if (!cache.Contains("%")) cache = "--";
                CornerText(graphics, cache, cornerCacheFont, fresh ? Blue : Muted, new RectangleF(216, 369, 158, 31), false);
                PaintActivity(graphics, signal, phase);
            }
            finally { graphics.Restore(saved); }
        }

        private void PaintCornerQuota(Graphics g)
        {
            var window = quotaWindows.FirstOrDefault(w => Json.Number(w, "window_minutes") == 10080);
            double remaining = Math.Max(0, Math.Min(100, Json.Number(window, "remaining_percent") ?? 0));
            Color color = window == null ? Muted : remaining < 20 ? Orange : Mint;
            CornerText(g, window == null ? "账户额度剩余" : QuotaLabel(window), cornerSmallFont, Muted, new RectangleF(25, 85, 166, 20), false);
            string value = window == null ? "—" : remaining.ToString("0.#");
            Font hero = value.Length > 4 ? cornerQuotaFont : cornerHeroFont;
            CornerText(g, value, hero, window == null ? Muted : Ink, new RectangleF(17, 107, 171, 70), false);
            if (window != null)
            {
                float valueWidth;
                using (var format = StringFormat.GenericTypographic) valueWidth = g.MeasureString(value, hero, 190, format).Width;
                CornerText(g, "%", cornerValueFont, Muted, new RectangleF(Math.Min(166, 24 + valueWidth), 139, 23, 27), false);
            }
            PaintQuotaBar(g, 25, 181, 150, remaining, color);
            if (motionAllowed && window != null && remaining > 3)
            {
                float filled = (float)(150 * remaining / 100);
                float head = (float)(MotionMs % 5200 / 5200) * filled;
                using (var shine = new Pen(Color.FromArgb(190, Color.White), 1.5f))
                    g.DrawLine(shine, 25 + Math.Max(1, head - 14), 183, 25 + Math.Max(1.1f, head), 183);
            }
            CornerText(g, quotaCached ? "上次同步的额度" : window == null ? "等待账户同步" : "每周使用预算", cornerMicroFont, Muted, new RectangleF(25, 195, 162, 17), false);

            var radar = resetRadar ?? ResetRadarDisplay.Read(data, EpochMs());
            double clock = MotionMs;
            bool present = radar.HasSignal, burst = radarMotion.Bursting(clock);
            double pulse = motionAllowed && present ? (1 + Math.Sin(clock / (radar.Alert ? 550 : 1100))) / 2 : .5;
            Color tone = present ? radarMotion.Tone(clock) : Blue;
            var bounds = new RectangleF(198, 85, 178, 127);
            GlassPanel(g, bounds, present ? tone : Blue, present ? (int)(38 + pulse * 30) : 0);
            if (present)
            {
                GlowPanel(g, bounds, tone, burst ? 2.0 : 1.0, pulse);
                // The clipped sweep is tied to an actual new or updated public signal.
                if (burst && motionAllowed)
                {
                    var saved = g.Save();
                    using (var clip = RoundedCard(bounds, 18)) g.SetClip(clip, CombineMode.Intersect);
                    float sweepX = bounds.Left - 100 + (float)(((clock - radarMotion.ChangedAt) % 3000) / 3000) * 390;
                    using (var sweep = new LinearGradientBrush(new RectangleF(sweepX, bounds.Top, 90, bounds.Height),
                        Color.FromArgb(0, tone), Color.FromArgb(0, tone), 0f))
                    {
                        sweep.InterpolationColors = new ColorBlend { Colors = new[] {Color.FromArgb(0, tone), Color.FromArgb(42, tone), Color.FromArgb(0, tone)}, Positions = new[] {0f, .5f, 1f} };
                        g.FillRectangle(sweep, sweepX, bounds.Top, 90, bounds.Height);
                    }
                    g.Restore(saved);
                }
            }
            CornerText(g, "重置雷达", cornerTitleFont, present ? tone : Ink, new RectangleF(214, 98, 100, 22), false);
            using (var ring = new Pen(Color.FromArgb(65, tone))) g.DrawEllipse(ring, 340.5f, 96.5f, 24, 24);
            if (motionAllowed && (present || radar.State == "none" || radar.State == "expired" || radar.State == "loading"))
                ScanOrbit(g, 352.5f, 108.5f, 12, tone, clock);
            using (var ring = new Pen(Color.FromArgb(100, tone))) g.DrawEllipse(ring, 349, 105, 7, 7);
            using (var dot = new SolidBrush(present ? tone : Muted)) g.FillEllipse(dot, 351, 107, 3, 3);
            if (present && motionAllowed)
            {
                double wave = (clock % (burst ? 1800 : 3600)) / (burst ? 1800 : 3600);
                float r = (float)(4 + wave * 10);
                using (var ring = new Pen(Color.FromArgb((int)(140 * (1 - wave)), tone))) g.DrawEllipse(ring, 352.5f - r, 108.5f - r, r * 2, r * 2);
            }
            string badge = radar.Title.StartsWith("重置雷达", StringComparison.Ordinal) ? radar.Title.Substring(4).Trim(' ', '·') : radar.Title;
            CornerText(g, badge.Length == 0 ? "公共信号" : badge, cornerMicroFont, Muted, new RectangleF(214, 123, 146, 17), false);
            Font dateFont = radar.CalendarTime ? cornerRadarFont : cornerValueFont;
            if (g.MeasureString(radar.Countdown, dateFont).Width > 149) dateFont = cornerTitleFont;
            if (g.MeasureString(radar.Countdown, dateFont).Width > 149) dateFont = cornerSmallFont;
            CornerText(g, radar.Countdown, dateFont, tone, new RectangleF(212, 143, 151, 27), false);
            CornerText(g, burst ? radarMotion.Notice : radar.Detail, cornerMicroFont, burst ? tone : Muted, new RectangleF(214, 181, 147, 18), false);
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
            var bounds = new RectangleF(x, 251, width, 82);
            GlassPanel(g, bounds, level >= 2 ? color : Blue, level >= 2 ? 45 : 0);
            GlowPanel(g, bounds, color, level >= 4 ? 1.6 : level >= 2 ? 1.2 : .38, pulse);
            using (var dot = new SolidBrush(color)) g.FillEllipse(dot, x + 15, 267, 4, 4);
            CornerText(g, label, cornerSmallFont, Ink, new RectangleF(x + 23, 259, 91, 19), false);
            string status = ok ? delay.Value < 300 ? "流畅" : delay.Value < 800 ? "一般" : delay.Value < 1500 ? "偏慢" : "高延迟" :
                timeout ? "超时" : !recent && measured.HasValue ? "已过期" : state == "loading" || node == null ? "探测中" : "不可用";
            CornerText(g, status, cornerMicroFont, color, new RectangleF(x + 112, 260, 44, 18), true);
            string value = ok ? Math.Round(delay.Value).ToString() : "—";
            Font numberFont = value.Length > 4 ? cornerValueFont : cornerNumberFont;
            CornerText(g, value, numberFont, ok ? Ink : color, new RectangleF(x + 10, 282, 98, 36), false);
            if (ok)
            {
                float valueWidth;
                using (var format = StringFormat.GenericTypographic) valueWidth = g.MeasureString(value, numberFont, 150, format).Width;
                CornerText(g, "ms", cornerMicroFont, Muted, new RectangleF(x + 15 + valueWidth, 298, 21, 16), false);
            }
            var history = Json.List(node, "history").TakeLastCompat(12).ToList();
            for (int i = 0; i < 12; i++)
            {
                float bx = x + 112 + i * 3.7f;
                using (var track = new SolidBrush(Color.FromArgb(45, Muted))) g.FillRectangle(track, bx, 313, 2, 2);
                int index = i - (12 - history.Count);
                if (index < 0) continue;
                double? ms = Json.Number(history[index], "delay_ms");
                if (Json.Text(history[index], "state") == "ok" && ms.HasValue && ms.Value > 0 && !double.IsInfinity(ms.Value) && !double.IsNaN(ms.Value))
                {
                    float height = (float)(4 + 20 * Math.Min(1500, ms.Value) / 1500);
                    using (var bar = new SolidBrush(Color.FromArgb(150 + i * 8, recent ? DelayColor(ms.Value) : Muted)))
                        g.FillRectangle(bar, bx, 315 - height, 2, height);
                }
                else if (Json.Text(history[index], "state") == "timeout")
                    using (var pen = new Pen(recent ? Rose : Muted)) { g.DrawLine(pen, bx, 289, bx + 2, 293); g.DrawLine(pen, bx + 2, 289, bx, 293); }
            }
            double reveal = motionAllowed ? Math.Max(0, Math.Min(1, 1 - (time - motion.SampleAt) / 2400)) : 0;
            if (reveal > 0 && recent)
            {
                using (var path = RoundedCard(bounds, 18))
                using (var fill = new LinearGradientBrush(bounds, Color.FromArgb((int)(reveal * 25), color), Color.FromArgb(0, color), 90f))
                    g.FillPath(fill, path);
                using (var path = RoundedCard(bounds, 18))
                using (var pen = new Pen(Color.FromArgb((int)(reveal * 175), color), 1.8f)) g.DrawPath(pen, path);
                float r = (float)(3 + (1 - reveal) * 9);
                using (var ring = new Pen(Color.FromArgb((int)(reveal * 185), color)))
                    g.DrawEllipse(ring, x + 17 - r, 269 - r, r * 2, r * 2);
            }
            if (reveal > 0 && recent)
                using (var pen = new Pen(Color.FromArgb((int)(reveal * 140), color), 1.5f)) g.DrawLine(pen, x + 18, 332, x + width - 18, 332);
            if (motionAllowed && level >= 2)
            {
                double wave = (time % 2500) / 2500;
                float r = (float)(3 + wave * 7);
                using (var ring = new Pen(Color.FromArgb((int)(120 * (1 - wave)), color))) g.DrawEllipse(ring, x + 17 - r, 269 - r, r * 2, r * 2);
            }
        }

        private void PaintActivity(Graphics g, Color color, double phase)
        {
            double time = MotionMs;
            var bounds = new RectangleF(24, 409, 352, 103);
            GlassPanel(g, bounds, activity.Active ? color : Blue, activity.Severity >= 2 ? 55 : 4);
            if (activity.Active) GlowPanel(g, bounds, color, .38 + activity.WaitLevel * .3, phase);
            CornerText(g, activity.Title, cornerTitleFont, activity.Active ? color : Ink, new RectangleF(39, 419, 150, 21), false);
            CornerText(g, activity.State == "idle" ? "最近活动" : "距最新活动", cornerMicroFont, Muted, new RectangleF(228, 422, 67, 18), true);
            CornerText(g, activity.ElapsedText, activity.ElapsedText.Length > 5 ? cornerTitleFont : cornerValueFont,
                activity.Active ? color : Ink, new RectangleF(294, 415, 67, 27), true);
            var clock = new RectangleF(212, 425, 10, 10);
            using (var track = new Pen(Color.FromArgb(40, color), 1.5f)) g.DrawEllipse(track, clock);
            PaintWaitRing(g, clock, activity.Active ? (float)(360 * Math.Min(1, (activity.EventAgeMs ?? 0) / (activity.State == "tools" ? 600000 : 120000))) : 0, color);
            if (activity.Active && motionAllowed)
            {
                double angle = time / 1400;
                using (var dot = new SolidBrush(color)) g.FillEllipse(dot, 216 + (float)Math.Cos(angle) * 5, 429 + (float)Math.Sin(angle) * 5, 2, 2);
            }
            CornerText(g, "平均响应", cornerMicroFont, Muted, new RectangleF(40, 444, 90, 18), false);
            string average = activity.AverageText;
            CornerText(g, average, average.Length > 6 ? cornerValueFont : cornerNumberFont,
                activity.AverageMs.HasValue ? ResponseColor(activity.AverageMs.Value) : Muted, new RectangleF(36, 460, 108, 32), false);
            PaintResponseHistory(g, time);
            string detail = activity.Severity > 0 || activity.State == "unknown" ? activity.Detail :
                activity.CompletedCount > 1 ? "最近 " + activity.CompletedCount + " 次 · 虚线为均值 · 日志估算" :
                activity.CompletedCount == 1 ? "仅 1 次响应 · 持续积累中" : "等待有完整计时的模型响应";
            CornerText(g, detail, cornerMicroFont, activity.Severity >= 2 ? color : Muted, new RectangleF(40, 492, 320, 15), false);
        }

        private void PaintResponseHistory(Graphics g, double time)
        {
            responseMotion.Update(activity, time, motionAllowed);
            var values = responseMotion.Target;
            var plot = new RectangleF(154, 464, 200, 23);
            CornerText(g, "响应趋势", cornerMicroFont, Muted, new RectangleF(152, 444, 80, 17), false);
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
                    using (var pen = new Pen(ResponseColor(values[i]), 1.6f)) g.DrawLine(pen, points[i - 1], points[i]);
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

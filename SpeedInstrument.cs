using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;

namespace CodexTokenHud
{
    // Integrate color travel instead of multiplying an absolute clock by speed.
    // Rate changes alter velocity smoothly without jumping to a different hue.
    internal sealed class SpectrumFlow
    {
        internal double Phase, CyclesPerSecond = 1.0 / 18, LastTime;
        private bool initialized;
        internal static double TargetRate(double? speed, bool forecast)
        {
            double rate = SpeedDisplay.Valid(speed) ? speed.Value : 0;
            double cycles = 1.0 / 18 + (.4 - 1.0 / 18) * (1 - Math.Exp(-rate / 65));
            return Math.Min(.5, cycles * (forecast ? 1.25 : 1));
        }
        internal void Update(double clock, double? speed, bool forecast, bool animate)
        {
            if (double.IsNaN(clock) || double.IsInfinity(clock)) return;
            double target = TargetRate(speed, forecast);
            if (!initialized || clock < LastTime)
            { initialized = true; LastTime = clock; CyclesPerSecond = target; return; }
            double elapsed = Math.Max(0, (clock - LastTime) / 1000);
            LastTime = clock;
            if (!animate) { CyclesPerSecond = target; return; }
            // Do not fast-forward through a suspended app/menu or memory pause.
            double dt = Math.Min(.25, elapsed), decay = Math.Exp(-dt / .65);
            Phase += target * dt + (CyclesPerSecond - target) * .65 * (1 - decay);
            Phase -= Math.Floor(Phase);
            CyclesPerSecond = target + (CyclesPerSecond - target) * decay;
        }
    }

    internal sealed partial class Hud
    {
        private readonly Font instrumentFont = new Font("Segoe UI Semibold", 38, FontStyle.Regular, GraphicsUnit.Pixel);
        private SpeedDisplay speedView = new SpeedDisplay();
        private readonly HeatMotion speedMotion = new HeatMotion();
        private readonly HeatMotion speedColorMotion = new HeatMotion();
        private readonly SpectrumFlow quotaFlow = new SpectrumFlow();
        private Color displayedSpeedColor = Muted;

        // Analytic critically damped spring, adapted from Agent Speed's MIT-licensed
        // Springs.cs. Attribution and upstream revision are in docs/SPEED.md.
        private sealed class HeatMotion
        {
            internal double Value, Velocity, LastTime, CompletionAt = -100000, LastResponse;
            internal string Session = "";
            internal void Update(double target, double clock, bool animate, string session, double response)
            {
                bool switched = Session != session;
                if (switched) { Session = session; Value = target; Velocity = 0; LastResponse = response; CompletionAt = -100000; }
                else if (response > LastResponse && LastTime > 0)
                { CompletionAt = clock; LastResponse = response; }
                else LastResponse = Math.Max(LastResponse, response);
                double dt = Math.Max(0, Math.Min(1, (clock - LastTime) / 1000));
                LastTime = clock;
                if (!animate || switched) { Value = target; Velocity = 0; return; }
                double omega = 9.7225, offset = Value - target, c = Velocity + omega * offset, e = Math.Exp(-omega * dt);
                Value = target + (offset + c * dt) * e;
                Velocity = (Velocity - omega * c * dt) * e;
            }
        }

        private static Color SpeedColor(double? rate)
        {
            if (!SpeedDisplay.Valid(rate)) return Muted;
            double value = rate.Value;
            Color cyan = Color.FromArgb(125, 227, 241);
            return value < 25 ? Mix(Blue, cyan, value / 25) : value < 60 ? Mix(cyan, Mint, (value - 25) / 35) :
                value < 120 ? Mix(Mint, Amber, (value - 60) / 60) : Mix(Amber, Orange, Math.Min(1, (value - 120) / 80));
        }

        private void PaintSpeedInstrument(Graphics g, double clock)
        {
            speedView = SpeedDisplay.Read(data, selectedTitle, EpochMs());
            double target = speedView.Ready && speedView.Rate.HasValue ? Math.Min(1, Math.Pow(speedView.Rate.Value / configuration.SpeedRedline, .6)) : 0;
            speedMotion.Update(target, clock, motionAllowed && !renderMemory.Reduced, Json.Text(data, "thread_id"), speedView.LastResponseAt);
            speedColorMotion.Update(speedView.Ready ? speedView.Rate.GetValueOrDefault() : 0, clock,
                motionAllowed && !renderMemory.Reduced, Json.Text(data, "thread_id"), speedView.LastResponseAt);
            double heat = Math.Max(0, Math.Min(1, speedMotion.Value));
            Color tone = speedView.Ready ? SpeedColor(Math.Max(0, speedColorMotion.Value)) : Muted;
            displayedSpeedColor = tone;
            var bounds = new RectangleF(20, 70, 360, 178);
            GlassPanel(g, bounds, tone, speedView.Phase == "生成中" ? (int)(heat * 35) : 0);
            var saved = g.Save();
            using (var clip = RoundedCard(bounds, 18)) g.SetClip(clip, CombineMode.Intersect);
            try
            {
                CornerText(g, "输出速度", cornerTitleFont, Ink, new RectangleF(36, 80, 88, 22), false);
                var badge = new RectangleF(286, 81, 78, 21);
                Panel(g, badge, Color.FromArgb(10, speedView.Estimated ? Amber : tone), Color.FromArgb(25, speedView.Estimated ? Amber : tone), 10);
                CornerText(g, speedView.Mode, cornerMicroFont, speedView.Estimated ? Amber : Muted, badge, StringAlignment.Center);
                const float cx = 100, cy = 172, radius = 58;
                using (var halo = new SolidBrush(Color.FromArgb((int)(4 + heat * 9), tone))) g.FillEllipse(halo, cx - 64, cy - 64, 128, 128);
                using (var track = new Pen(Color.FromArgb(25, tone))) g.DrawEllipse(track, cx - 49, cy - 49, 98, 98);
                for (int i = 0; i < 36; i++)
                {
                    double angle = (135 + i * 270.0 / 35) * Math.PI / 180;
                    bool lit = i < (int)Math.Round(36 * heat);
                    Color segment = Mix(tone, Ink, i / 35.0 * .16);
                    float inner = lit ? 51 : 53;
                    var a = new PointF(cx + inner * (float)Math.Cos(angle), cy + inner * (float)Math.Sin(angle));
                    var b = new PointF(cx + radius * (float)Math.Cos(angle), cy + radius * (float)Math.Sin(angle));
                    if (lit)
                        using (var glow = new Pen(Color.FromArgb(32, segment), 5)) { glow.StartCap = glow.EndCap = LineCap.Round; g.DrawLine(glow, a, b); }
                    using (var pen = new Pen(lit ? Color.FromArgb(230, segment) : Color.FromArgb(37, Muted), 2.2f))
                    { pen.StartCap = pen.EndCap = LineCap.Round; g.DrawLine(pen, a, b); }
                }
                if (motionAllowed && speedView.Ready && speedView.Rate > .5 && speedView.Phase == "生成中")
                {
                    double head = clock / (1000 / (1.2 + heat * 4));
                    for (int i = 0; i < 9; i++)
                    {
                        double a = head - i * .085;
                        float x = cx + 46 * (float)Math.Cos(a), y = cy + 46 * (float)Math.Sin(a);
                        using (var dot = new SolidBrush(Color.FromArgb(195 - i * 20, tone))) g.FillEllipse(dot, x - 1.4f, y - 1.4f, 2.8f, 2.8f);
                    }
                }
                double burst = motionAllowed ? (clock - speedMotion.CompletionAt) / 1400 : 2;
                if (burst >= 0 && burst < 1)
                    for (int wave = 0; wave < 2; wave++)
                    {
                        float r = (float)(40 + burst * 40 + wave * 8);
                        using (var pen = new Pen(Color.FromArgb((int)(100 * (1 - burst)), tone), 1.2f)) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                    }
                Font valueFont = speedView.Value.Length > 5 ? cornerNumberFont : instrumentFont;
                CornerText(g, speedView.Value, valueFont, speedView.Ready ? Mix(tone, Ink, .12) : Muted, new RectangleF(39, 141, 122, 49), StringAlignment.Center);
                CornerText(g, "tok/s", cornerMicroFont, Muted, new RectangleF(60, 189, 80, 16), StringAlignment.Center);
                string title = !speedView.Ready ? "等待计时" : speedView.Phase == "工具中" ? "工具中" :
                    speedView.Phase == "待机" ? "静候" : heat < .4 ? "巡航" : heat < .65 ? "加速" : heat < .85 ? "疾驰" : "极速";
                CornerText(g, title, cornerSmallFont, tone, new RectangleF(65, 221, 70, 19), StringAlignment.Center);
                CornerText(g, "上次实测", cornerMicroFont, Muted, new RectangleF(181, 115, 88, 17), false);
                CornerText(g, SpeedDisplay.Format(speedView.LastRate), cornerValueFont, SpeedColor(speedView.LastRate), new RectangleF(179, 134, 95, 27), false);
                CornerText(g, "缓存命中", cornerMicroFont, Muted, new RectangleF(282, 115, 80, 17), false);
                CornerText(g, SpeedDisplay.Valid(speedView.Cache) ? speedView.Cache.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—",
                    cornerValueFont, Ink, new RectangleF(280, 134, 83, 27), false);
                PaintSpeedHistory(g);
                string footer = (speedView.State == "range" ? "区间 " : "本轮 ") +
                    (speedView.ConfirmedOutput > 0 ? SpeedDisplay.Tokens(speedView.ConfirmedOutput) : "—") + " tok  ·  峰值 " + SpeedDisplay.Format(speedView.Peak);
                CornerText(g, footer, cornerMicroFont, Muted, new RectangleF(181, 221, 178, 18), false);

            }
            finally { g.Restore(saved); }
        }

        private void PaintSpeedHistory(Graphics g)
        {
            var plot = new RectangleF(182, 181, 174, 30);
            CornerText(g, "60 秒趋势", cornerMicroFont, Muted, new RectangleF(181, 163, 80, 17), false);
            CornerText(g, "虚线估算", cornerMicroFont, Muted, new RectangleF(282, 163, 76, 17), true);
            using (var baseLine = new Pen(Color.FromArgb(30, Blue))) g.DrawLine(baseLine, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            if (speedView.History.Count == 0) return;
            double ceiling = Math.Max(10, speedView.History.Max() * 1.2);
            var points = speedView.History.Select((v, i) => new PointF(plot.Left + (60 - speedView.History.Count + i) * plot.Width / 59,
                plot.Bottom - (float)(Math.Min(ceiling, v) / ceiling * plot.Height))).ToArray();
            for (int i = 1; i < points.Length; i++)
                using (var pen = new Pen(Color.FromArgb(speedView.Estimates[i] ? 150 : 220, SpeedColor((speedView.History[i - 1] + speedView.History[i]) / 2)), 1.4f))
                { if (speedView.Estimates[i]) pen.DashStyle = DashStyle.Dash; g.DrawLine(pen, points[i - 1], points[i]); }
        }
    }
}

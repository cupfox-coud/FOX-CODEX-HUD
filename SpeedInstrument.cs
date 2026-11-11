using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;

namespace CodexTokenHud
{
    internal sealed class QuotaReceipt
    {
        internal double ChangedAt = -100000, Delta;
        internal bool Refilled;
        private double balance, reset, stamp;
        private bool ready;
        internal void Observe(double? remaining, double? resetAt, double sampleAt, double now, bool fresh)
        {
            if (!fresh || !SpeedDisplay.Valid(remaining) || remaining > 100 || !SpeedDisplay.Valid(resetAt))
            { ready = false; ChangedAt = -100000; return; }
            if (!ready) { balance = remaining.Value; reset = resetAt.Value; stamp = sampleAt; ready = true; return; }
            if (sampleAt <= stamp) return;
            double change = remaining.Value - balance;
            bool newCycle = resetAt.Value > reset + 120;
            if (Math.Abs(change) >= .1 || newCycle)
            { ChangedAt = now; Delta = change; Refilled = change > 1.0 + 1e-9 || (remaining == 100 && balance < 100) || newCycle; }
            balance = remaining.Value; reset = resetAt.Value; stamp = sampleAt;
        }
        internal double Strength(double now)
        {
            double t = (now - ChangedAt) / (Refilled ? 2600 : 1100);
            return t < 0 || t >= 1 ? 0 : Math.Sin(Math.PI * t) * (1 - t * .35);
        }
    }

    // Compare live readings within a short window. Paint cadence must not create
    // extra events, and switching tasks or resuming from idle starts a new window.
    internal sealed class SpeedSurge
    {
        private readonly double[] rates = new double[20], times = new double[20];
        private int count, next;
        private string session;
        private double sampledAt, previous;
        internal double Started = -100000, Gain;
        internal const double Duration = 1450;

        internal void Observe(double clock, double? rate, bool active, string key)
        {
            if (!active || !SpeedDisplay.Valid(rate) || session != key ||
                clock < sampledAt || clock - sampledAt > 1800)
            {
                count = next = 0;
                Started = -100000;
                Gain = 0;
                session = key;
            }
            if (!active || !SpeedDisplay.Valid(rate)) return;
            double current = rate.Value;
            if (count > 0 && current > previous && clock - Started > 1800)
            {
                double low = current;
                for (int i = 0; i < count; i++)
                    if (clock - times[i] <= 1600) low = Math.Min(low, rates[i]);
                double increase = current - low;
                if (increase >= Math.Max(4, low * .20))
                {
                    Gain = increase;
                    Started = clock;
                }
            }
            previous = current;
            if (count > 0 && clock - sampledAt < 85) return;
            rates[next] = current;
            times[next] = sampledAt = clock;
            next = (next + 1) % rates.Length;
            count = Math.Min(count + 1, rates.Length);
        }
    }

    internal sealed class InstrumentFlow
    {
        internal double Phase, Energy, Velocity, LastTime;
        private string key;
        internal void Update(double now, double heat, bool generating, bool animate, string session)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return;
            heat = double.IsNaN(heat) || double.IsInfinity(heat) ? 0 : Math.Max(0, Math.Min(1, heat));
            double target = generating ? heat : 0, velocity = generating ? .16 + heat * .48 : .025;
            if (key != session || now < LastTime)
            { key = session; LastTime = now; Energy = target; Velocity = velocity; return; }
            double dt = Math.Min(.1, Math.Max(0, (now - LastTime) / 1000)); LastTime = now;
            if (!animate) { Energy = target; Velocity = velocity; return; }
            double decay = Math.Exp(-dt / .32);
            Phase += velocity * dt + (Velocity - velocity) * .32 * (1 - decay);
            Phase -= Math.Floor(Phase);
            Velocity = velocity + (Velocity - velocity) * decay;
            Energy = target + (Energy - target) * Math.Exp(-dt / .22);
        }
    }

    // A changed glyph rolls to the actual new value; no interpolated fake readings.
    // Timing/changed-digit treatment follows Agent Speed's MIT BoardHeroNumber.
    internal sealed class ReadoutTransition
    {
        internal string Text = "", Previous = "", Key = "";
        internal double ChangedAt = -100000, Direction = 1;
        internal const double Duration = 260;
        private bool initialized;
        internal bool Moving(double now) { return now >= ChangedAt && now - ChangedAt < Duration; }
        internal double Progress(double now)
        { double t = Math.Max(0, Math.Min(1, (now - ChangedAt) / Duration)); return 1 - Math.Pow(1 - t, 3); }
        internal void Set(string text, string key, double now, bool animate)
        {
            text = text ?? ""; key = key ?? "";
            if (!animate || !initialized || key != Key || now < ChangedAt)
            { Text = Previous = text; Key = key; ChangedAt = -100000; initialized = true; return; }
            if (text == Text) return;
            Previous = Text; Text = text; ChangedAt = now;
            double before, after;
            Direction = double.TryParse(Previous.Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out before) &&
                double.TryParse(Text.Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out after) && after < before ? -1 : 1;
        }
    }

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
        private readonly HeatMotion speedSpectrumMotion = new HeatMotion();
        private bool speedSpectrum;
        private readonly SpectrumFlow quotaFlow = new SpectrumFlow();
        private Color displayedSpeedColor = Muted;
        private readonly ReadoutTransition speedDigits = new ReadoutTransition(), speedMode = new ReadoutTransition(),
            lastSpeedDigits = new ReadoutTransition(), cacheDigits = new ReadoutTransition(), quotaDigits = new ReadoutTransition(),
            todayDigits = new ReadoutTransition(), cycleDigits = new ReadoutTransition(), allowanceDigits = new ReadoutTransition();
        private readonly HeatMotion chartCeiling = new HeatMotion();
        private readonly InstrumentFlow instrumentFlow = new InstrumentFlow();
        private readonly SpeedSurge speedSurge = new SpeedSurge();
        private readonly QuotaReceipt quotaReceipt = new QuotaReceipt();
        private readonly ReadoutTransition aiDigits = new ReadoutTransition(), generalDigits = new ReadoutTransition(),
            radarDigits = new ReadoutTransition(), responseDigits = new ReadoutTransition(), activityLabel = new ReadoutTransition();

        private bool ReadoutsMoving(double now)
        {
            return motionAllowed && !renderMemory.Reduced && (speedDigits.Moving(now) || speedMode.Moving(now) ||
                lastSpeedDigits.Moving(now) || cacheDigits.Moving(now) || quotaDigits.Moving(now) || todayDigits.Moving(now) || paceDigits.Moving(now) ||
                cycleDigits.Moving(now) || allowanceDigits.Moving(now) || aiDigits.Moving(now) || generalDigits.Moving(now) ||
                radarDigits.Moving(now) || responseDigits.Moving(now) || activityLabel.Moving(now));
        }

        private void MovingText(Graphics g, ReadoutTransition transition, string text, Font font, Color color,
            RectangleF bounds, StringAlignment alignment, string key)
        {
            MovingReadout(g, transition, text, font, bounds, alignment, key, delegate(string part, RectangleF box, double alpha) {
                CornerText(g, part, font, Color.FromArgb((int)(color.A * alpha), color), box, false);
            });
        }

        private void MovingReadout(Graphics g, ReadoutTransition transition, string text, Font font,
            RectangleF bounds, StringAlignment alignment, string key, Action<string, RectangleF, double> draw)
        {
            double now = MotionMs;
            transition.Set(text, key, now, motionAllowed && !renderMemory.Reduced);
            bool moving = transition.Moving(now);
            double progress = moving ? transition.Progress(now) : 1;
            var state = g.Save();
            try
            {
                g.SetClip(bounds, CombineMode.Intersect);
                using (var format = StringFormat.GenericTypographic)
                {
                    float width = g.MeasureString(text, font, 1000, format).Width;
                    float x = alignment == StringAlignment.Center ? bounds.Left + (bounds.Width - width) / 2 :
                        alignment == StringAlignment.Far ? bounds.Right - width : bounds.Left;
                    bool digits = text.All(c => char.IsDigit(c) || ".−-<k%:s天小时分秒".IndexOf(c) >= 0);
                    if (!moving || !digits || text.Length != transition.Previous.Length)
                    {
                        // Width/sign/state changes crossfade in the same fixed slot.
                        if (moving)
                        {
                            float oldWidth = g.MeasureString(transition.Previous, font, 1000, format).Width;
                            float oldX = alignment == StringAlignment.Center ? bounds.Left + (bounds.Width - oldWidth) / 2 :
                                alignment == StringAlignment.Far ? bounds.Right - oldWidth : bounds.Left;
                            draw(transition.Previous, new RectangleF(oldX, bounds.Top - (float)(progress * 5), oldWidth + 4, bounds.Height), 1 - progress);
                        }
                        draw(text, new RectangleF(x, bounds.Top + (float)((1 - progress) * 5), width + 4, bounds.Height), progress);
                        return;
                    }
                    for (int i = 0; i < text.Length; i++)
                    {
                        string glyph = text[i].ToString();
                        float advance = g.MeasureString(glyph, font, 1000, format).Width;
                        bool changed = text[i] != transition.Previous[i];
                        float travel = Math.Min(bounds.Height * .7f, font.Size * .9f) * (float)transition.Direction;
                        if (changed)
                            draw(transition.Previous[i].ToString(), new RectangleF(x, bounds.Top - travel * (float)progress, advance + 4, bounds.Height), 1 - progress);
                        draw(glyph, new RectangleF(x, bounds.Top + (changed ? travel * (float)(1 - progress) : 0), advance + 4, bounds.Height), changed ? progress : 1);
                        x += advance;
                    }
                }
            }
            finally { g.Restore(state); }
        }

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
            for (int i = 1; i < HeatStops.Length; i++)
                if (value < HeatStops[i])
                    return Mix(HeatColors[i - 1], HeatColors[i], (value - HeatStops[i - 1]) / (HeatStops[i] - HeatStops[i - 1]));
            return HeatColors[HeatColors.Length - 1];
        }

        private static readonly double[] HeatStops = {0, 16, 32, 55, 85, 125, 165, 200};
        private static readonly Color[] HeatColors = {
            Color.FromArgb(113, 207, 191), Color.FromArgb(77, 223, 239), Color.FromArgb(110, 180, 255),
            Color.FromArgb(157, 151, 255), Color.FromArgb(232, 143, 247), Color.FromArgb(255, 140, 166),
            Color.FromArgb(255, 166, 103), Color.FromArgb(255, 218, 107)};

        private void PaintSpeedInstrument(Graphics g, double clock)
        {
            speedView = SpeedDisplay.Read(data, selectedTitle, EpochMs());
            string readoutKey = speedView.Global ? "codex-global:" + Json.Text(data, "collector_pid") :
                Json.Text(data, "thread_id") + ":" + Json.Text(Json.Object(data, "metrics"), "model");
            double target = speedView.Ready && speedView.Rate.HasValue ? Math.Min(1, speedView.Rate.Value / configuration.SpeedRedline) : 0;
            speedMotion.Update(target, clock, motionAllowed && !renderMemory.Reduced, readoutKey, speedView.LastResponseAt);
            speedColorMotion.Update(speedView.Ready ? speedView.Rate.GetValueOrDefault() : 0, clock,
                motionAllowed && !renderMemory.Reduced, readoutKey, speedView.LastResponseAt);
            double heat = Math.Max(0, Math.Min(1, speedMotion.Value));
            Color tone = speedView.Ready ? SpeedColor(Math.Max(0, speedColorMotion.Value)) : Muted;
            speedSpectrum = speedView.Ready && (speedView.Rate >= 200 || (speedSpectrum && speedView.Rate >= 195));
            speedSpectrumMotion.Update(speedSpectrum ? 1 : 0, clock, motionAllowed && !renderMemory.Reduced, readoutKey, 0);
            double spectrum = Math.Max(0, Math.Min(1, speedSpectrumMotion.Value));
            displayedSpeedColor = tone;
            bool flowing = speedView.Ready && speedView.Phase == "生成中";
            speedSurge.Observe(clock, speedView.LiveRate, flowing && motionAllowed && !renderMemory.Reduced &&
                (speedView.State == "measured" || speedView.State == "estimating"), readoutKey);
            instrumentFlow.Update(clock, heat, flowing, motionAllowed && !renderMemory.Reduced,
                readoutKey);
            // Keep one performance group; only its output inset owns speed effects.
            GlassPanel(g, new RectangleF(20, 70, 360, 336), Blue, 0);
            var bounds = new RectangleF(28, 78, 344, 170);
            InsetSection(g, bounds, tone, flowing ? (int)(55 + heat * 35) : 0);
            var saved = g.Save();
            using (var clip = RoundedCard(bounds, 10)) g.SetClip(clip, CombineMode.Intersect);
            try
            {
                PaintSpeedSurge(g, bounds, tone, clock);
                CornerText(g, speedView.Global ? "全局输出" : "输出速度", cornerTitleFont, Ink, new RectangleF(36, 80, 88, 22), false);
                if (speedView.Global)
                    CornerText(g, "生成 " + speedView.Generating + " · 工具 " + speedView.Tools,
                        cornerMicroFont, Muted, new RectangleF(144, 81, 132, 21), true);
                var badge = new RectangleF(286, 81, 78, 21);
                Panel(g, badge, Color.FromArgb(10, speedView.Estimated ? Amber : tone), Color.FromArgb(25, speedView.Estimated ? Amber : tone), 10);
                MovingText(g, speedMode, speedView.Mode, cornerMicroFont, speedView.Estimated ? Amber : Muted, badge, StringAlignment.Center, readoutKey);
                const float cx = 100, cy = 172, radius = 58;
                PaintInstrumentWell(g, cx, cy, tone, heat, clock);
                using (var halo = new SolidBrush(Color.FromArgb((int)(4 + heat * 9), tone))) g.FillEllipse(halo, cx - 64, cy - 64, 128, 128);
                using (var track = new Pen(Color.FromArgb(25, tone))) g.DrawEllipse(track, cx - 49, cy - 49, 98, 98);
                for (int i = 0; i < 36; i++)
                {
                    double angle = (135 + i * 270.0 / 35) * Math.PI / 180;
                    double illumination = Math.Max(0, Math.Min(1, 36 * heat - i));
                    bool lit = illumination > 0;
                    double position = Math.Min(1, (i + 1) / Math.Max(1, heat * 36));
                    Color segment = Mix(SpeedColor(Math.Max(0, speedColorMotion.Value) * (.42 + .58 * position)), tone, .15);
                    segment = Mix(segment, SpectrumColor((double)i / 36 + instrumentFlow.Phase, true), spectrum);
                    float inner = lit ? 51 : 53;
                    var a = new PointF(cx + inner * (float)Math.Cos(angle), cy + inner * (float)Math.Sin(angle));
                    var b = new PointF(cx + radius * (float)Math.Cos(angle), cy + radius * (float)Math.Sin(angle));
                    if (lit)
                        using (var glow = new Pen(Color.FromArgb((int)(32 * illumination), segment), 5)) { glow.StartCap = glow.EndCap = LineCap.Round; g.DrawLine(glow, a, b); }
                    using (var pen = new Pen(lit ? Color.FromArgb((int)(37 + 193 * illumination), segment) : Color.FromArgb(37, Muted), 2.2f))
                    { pen.StartCap = pen.EndCap = LineCap.Round; g.DrawLine(pen, a, b); }
                }
                if (motionAllowed && speedView.Ready && speedView.Rate > .5 && speedView.Phase == "生成中")
                {
                    double head = instrumentFlow.Phase * Math.PI * 2;
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
                var digitBounds = new RectangleF(39, 141, 122, 49);
                if (spectrum > .005)
                    MovingReadout(g, speedDigits, speedView.Value, valueFont, digitBounds, StringAlignment.Center, readoutKey,
                        delegate(string part, RectangleF box, double alpha) {
                            using (var brush = QuotaSpectrumBrush(digitBounds, instrumentFlow.Phase, true, (int)(255 * alpha)))
                            {
                                var blend = brush.InterpolationColors;
                                blend.Colors = blend.Colors.Select(c => Color.FromArgb(c.A, Mix(tone, c, spectrum))).ToArray();
                                brush.InterpolationColors = blend;
                                QuotaBrushText(g, part, valueFont, brush, box);
                            }
                        });
                else MovingText(g, speedDigits, speedView.Value, valueFont, speedView.Ready ? Mix(tone, Ink, .12) : Muted,
                    digitBounds, StringAlignment.Center, readoutKey);
                CornerText(g, "tok/s", cornerMicroFont, Muted, new RectangleF(60, 189, 80, 16), StringAlignment.Center);
                string title = !speedView.Ready ? "等待计时" : speedView.Phase == "工具中" ? "工具中" :
                    speedView.Phase == "待机" ? "静候" : speedSpectrum ? "高速" : heat < .4 ? "巡航" : heat < .65 ? "加速" : heat < .85 ? "疾驰" : "极速";
                CornerText(g, title, cornerSmallFont, tone, new RectangleF(65, 221, 70, 19), StringAlignment.Center);
                CornerText(g, "0", cornerMicroFont, Muted, new RectangleF(39, 214, 24, 17), false);
                CornerText(g, configuration.SpeedRedline.ToString("0"), cornerMicroFont, Muted, new RectangleF(139, 214, 38, 17), true);
                CornerText(g, speedView.Global ? "近30秒均速" : "上次实测", cornerMicroFont, Muted, new RectangleF(181, 115, 88, 17), false);
                MovingText(g, lastSpeedDigits, SpeedDisplay.Format(speedView.LastRate), cornerValueFont, SpeedColor(speedView.LastRate),
                    new RectangleF(179, 134, 95, 27), StringAlignment.Near, readoutKey);
                CornerText(g, "缓存命中", cornerMicroFont, Muted, new RectangleF(282, 115, 80, 17), false);
                MovingText(g, cacheDigits, SpeedDisplay.Valid(speedView.Cache) ? speedView.Cache.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—",
                    cornerValueFont, Ink, new RectangleF(280, 134, 83, 27), StringAlignment.Near, readoutKey);
                PaintSpeedHistory(g);
                string footer = (speedView.Global ? "采样 " : speedView.State == "range" ? "区间 " : "本轮 ") +
                    (speedView.ConfirmedOutput > 0 ? SpeedDisplay.Tokens(speedView.ConfirmedOutput) : "—") + " tok  ·  峰值 " + SpeedDisplay.Format(speedView.Peak);
                CornerText(g, footer, cornerMicroFont, Muted, new RectangleF(181, 221, 178, 18), false);
                PaintInstrumentReceipt(g, bounds, clock, tone);

            }
            finally { g.Restore(saved); }
        }

        private void PaintSpeedSurge(Graphics g, RectangleF bounds, Color tone, double clock)
        {
            if (!motionAllowed || renderMemory.Reduced) return;
            double t = (clock - speedSurge.Started) / SpeedSurge.Duration;
            if (t < 0 || t >= 1) return;
            // Feathered, slightly inclined light with a smooth attack and release.
            // The caller clips every part to the output section above response.
            double envelope = Math.Pow(Math.Sin(Math.PI * t), 1.3);
            Color hot = Mix(tone, Color.FromArgb(255, 242, 195), .65);
            float travel = (float)(t * t * (3 - 2 * t));
            using (var atmosphere = new LinearGradientBrush(bounds, Color.FromArgb((int)(25 * envelope), tone),
                Color.FromArgb(0, tone), 22f)) g.FillRectangle(atmosphere, bounds);
            var beamState = g.Save();
            try
            {
                float x = bounds.Left - 125 + travel * (bounds.Width + 300);
                using (var tilt = new Matrix(1, 0, -.24f, 1, x, bounds.Top)) g.MultiplyTransform(tilt, MatrixOrder.Prepend);
                var band = new RectangleF(-100, -24, 200, bounds.Height + 48);
                using (var beam = new LinearGradientBrush(band, Color.Transparent, Color.Transparent, 0f))
                {
                    beam.InterpolationColors = new ColorBlend {
                        Colors = new[] {Color.FromArgb(0, Blue), Color.FromArgb((int)(6 * envelope), Blue),
                            Color.FromArgb((int)(24 * envelope), Blue), Color.FromArgb((int)(34 * envelope), Color.FromArgb(220,154,255)),
                            Color.FromArgb((int)(32 * envelope), Color.FromArgb(255,183,199)), Color.FromArgb((int)(27 * envelope), Mint),
                            Color.FromArgb((int)(6 * envelope), Mint), Color.FromArgb(0, hot)},
                        Positions = new[] {0f,.12f,.3f,.46f,.59f,.73f,.88f,1f}};
                    g.FillRectangle(beam, band);
                }
            }
            finally { g.Restore(beamState); }
            using (var edge = new Pen(Color.FromArgb((int)(85 * envelope), tone), 1.3f))
            using (var outline = RoundedCard(bounds, 10)) g.DrawPath(edge, outline);
            float head = 140 + travel * 308;
            using (var ribbon = new Pen(Color.Empty, 3))
            {
                ribbon.StartCap = ribbon.EndCap = LineCap.Round;
                for (int i = 0; i < 7; i++)
                {
                    double light = 1 - i / 7.0;
                    ribbon.Color = Color.FromArgb((int)(175 * envelope * light), SpectrumColor(i / 7.0 + t, true));
                    g.DrawArc(ribbon, 35, 107, 130, 130, head - i * 9, 9.5f);
                }
            }
            using (var streak = new Pen(Color.Empty, 1.7f))
            {
                streak.StartCap = streak.EndCap = LineCap.Round;
                for (int i = 0; i < 19; i++)
                {
                    double angle = (137 + i * 14.5 + travel * 13) * Math.PI / 180;
                    float x = (float)Math.Cos(angle), y = (float)Math.Sin(angle);
                    float r = 61 + travel * (6 + (i * 7 % 12));
                    float length = (float)(2 + envelope * (3 + i % 5));
                    streak.Color = Color.FromArgb((int)(150 * envelope), SpectrumColor(i / 19.0 + t, true));
                    g.DrawLine(streak, 100 + r*x, 172 + r*y, 100 + (r+length)*x - 2*y, 172 + (r+length)*y + 2*x);
                }
            }
            var badge = new RectangleF(107, 81, 93, 20);
            Panel(g, badge, Color.FromArgb((int)(48 * envelope), tone), Color.FromArgb((int)(104 * envelope), hot), 9);
            CornerText(g, "↑ +" + speedSurge.Gain.ToString("0", CultureInfo.InvariantCulture) + " tok/s",
                cornerSmallFont, Color.FromArgb((int)(255 * envelope), Ink), badge, StringAlignment.Center);
        }

        private void PaintInstrumentWell(Graphics g, float cx, float cy, Color tone, double heat, double clock)
        {
            if (renderMemory.Reduced) return;
            var well = new RectangleF(cx - 63, cy - 63, 126, 126);
            using (var fill = new LinearGradientBrush(well, Color.FromArgb(100, 8, 13, 21), Color.FromArgb(25, 66, 82, 100), 110f))
                g.FillEllipse(fill, well);
            using (var bevel = new LinearGradientBrush(well, Color.FromArgb(75, 173, 199, 221), Color.FromArgb(12, 22, 31, 43), 100f))
            using (var pen = new Pen(bevel, 1)) g.DrawEllipse(pen, well);
            using (var inset = new Pen(Color.FromArgb(75, 7, 12, 19), 2)) g.DrawEllipse(inset, cx - 48, cy - 48, 96, 96);
            double energy = instrumentFlow.Energy;
            if (energy <= .005) return;
            using (var light = new LinearGradientBrush(well, Color.FromArgb((int)(55 * energy), tone),
                Color.FromArgb(0, tone), 55f)) g.FillEllipse(light, well);
            // A continuous filament travels only through the ring, outside the digits.
            double head = (motionAllowed ? instrumentFlow.Phase : .1) * 360;
            for (int segment = 0; segment < 12; segment++)
            {
                int alpha = (int)((10 + energy * 65) * (1 - segment / 12.0));
                using (var halo = new Pen(Color.FromArgb(alpha / 3, tone), 6))
                    g.DrawArc(halo, cx - 44, cy - 44, 88, 88, (float)(head - segment * 7), 8);
                using (var filament = new Pen(Color.FromArgb(alpha, tone), 1.2f))
                    g.DrawArc(filament, cx - 44, cy - 44, 88, 88, (float)(head - segment * 7), 8);
            }
            double angle = (135 + heat * 270) * Math.PI / 180;
            float px = cx + 63 * (float)Math.Cos(angle), py = cy + 63 * (float)Math.Sin(angle);
            using (var halo = new SolidBrush(Color.FromArgb((int)(energy * 45), tone))) g.FillEllipse(halo, px - 5, py - 5, 10, 10);
            using (var point = new SolidBrush(Color.FromArgb((int)(130 + energy * 100), Mix(tone, Ink, .45)))) g.FillEllipse(point, px - 1.5f, py - 1.5f, 3, 3);
        }

        private void PaintInstrumentReceipt(Graphics g, RectangleF bounds, double clock, Color tone)
        {
            if (!motionAllowed || renderMemory.Reduced) return;
            double t = (clock - speedMotion.CompletionAt) / 1400;
            if (t < 0 || t >= 1) return;
            float x = bounds.Left - 60 + (float)(1 - Math.Pow(1 - t, 2)) * (bounds.Width + 120);
            var beam = new RectangleF(x, bounds.Top, 70, bounds.Height);
            using (var wash = new LinearGradientBrush(beam, Color.Transparent, Color.Transparent, 15f))
            {
                wash.InterpolationColors = new ColorBlend { Colors = new[] {Color.FromArgb(0, tone), Color.FromArgb((int)(120 * Math.Sin(t * Math.PI)), tone), Color.FromArgb(0, tone)}, Positions = new[] {0f, .5f, 1f}};
                using (var path = RoundedCard(bounds, 18)) using (var edge = new Pen(wash, 1.6f)) g.DrawPath(edge, path);
            }
        }

        private void PaintSpeedHistory(Graphics g)
        {
            var plot = new RectangleF(182, 181, 174, 30);
            CornerText(g, "60 秒趋势", cornerMicroFont, Muted, new RectangleF(181, 163, 80, 17), false);
            CornerText(g, "虚线估算", cornerMicroFont, Muted, new RectangleF(282, 163, 76, 17), true);
            using (var baseLine = new Pen(Color.FromArgb(30, Blue))) g.DrawLine(baseLine, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            if (speedView.History.Count == 0) return;
            double targetCeiling = Math.Max(10, speedView.History.Max() * 1.2);
            chartCeiling.Update(targetCeiling, MotionMs, motionAllowed && !renderMemory.Reduced,
                speedView.Global ? "codex-global:" + Json.Text(data, "collector_pid") : Json.Text(data, "thread_id"), 0);
            double ceiling = Math.Max(10, chartCeiling.Value);
            double now = EpochMs();
            bool timed = speedView.HistoryTimes.Count == speedView.History.Count && speedView.HistoryTimes.All(t => t > 0);
            // Timestamp positions move continuously; no array-wide jump at a second boundary.
            var points = speedView.History.Select((v, i) => new PointF(timed ?
                plot.Right - (float)((now - speedView.HistoryTimes[i]) / 60000 * plot.Width) :
                plot.Left + (60 - speedView.History.Count + i) * plot.Width / 59,
                plot.Bottom - (float)(Math.Min(ceiling, v) / ceiling * plot.Height))).ToArray();
            var saved = g.Save();
            g.SetClip(new RectangleF(plot.Left, plot.Top - 2, plot.Width, plot.Height + 4), CombineMode.Intersect);
            try {
            if (points.Length > 1 && !renderMemory.Reduced)
                using (var area = new GraphicsPath())
                {
                    area.AddLines(points); area.AddLine(points[points.Length - 1], new PointF(points[points.Length - 1].X, plot.Bottom));
                    area.AddLine(new PointF(points[points.Length - 1].X, plot.Bottom), new PointF(points[0].X, plot.Bottom)); area.CloseFigure();
                    using (var wash = new LinearGradientBrush(plot, Color.FromArgb(28, displayedSpeedColor), Color.FromArgb(0, displayedSpeedColor), 90f)) g.FillPath(wash, area);
                }
            for (int i = 1; i < points.Length; i++)
                using (var pen = new Pen(Color.FromArgb(speedView.Estimates[i] ? 150 : 220,
                    (speedView.History[i - 1] + speedView.History[i]) / 2 >= 200 ? SpectrumColor(i / 60.0 + instrumentFlow.Phase, true) :
                    SpeedColor((speedView.History[i - 1] + speedView.History[i]) / 2)), 1.4f))
                { if (speedView.Estimates[i]) pen.DashStyle = DashStyle.Dash; g.DrawLine(pen, points[i - 1], points[i]); }
            if (points.Length > 0 && speedView.Ready)
            {
                var tip = points[points.Length - 1]; Color tone = SpeedColor(speedView.History.Last());
                double pulse = motionAllowed && speedView.Phase == "生成中" ? .5 + .5 * Math.Sin(instrumentFlow.Phase * Math.PI * 2) : .3;
                using (var glow = new SolidBrush(Color.FromArgb((int)(16 + 20 * pulse), tone))) g.FillEllipse(glow, tip.X - 4, tip.Y - 4, 8, 8);
                using (var dot = new SolidBrush(Color.FromArgb(220, tone))) g.FillEllipse(dot, tip.X - 1.7f, tip.Y - 1.7f, 3.4f, 3.4f);
            }
            } finally { g.Restore(saved); }
        }
    }
}

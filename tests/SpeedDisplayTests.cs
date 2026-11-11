using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CodexTokenHud;

class SpeedDisplayTests
{
    static int checks;
    static void Check(bool value, string name)
    { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static Dictionary<string, object> Fixture(double now)
    {
        return new Dictionary<string, object> {
            {"title", "current"}, {"binding", "ok"}, {"thread_id", "t"}, {"updated_at_ms", now},
            {"source", new Dictionary<string, object> {{"state", "ok"}}},
            {"metrics", new Dictionary<string, object> {{"model", "m"}, {"stage", "generating"},
                {"live_speed", new Dictionary<string, object> {{"state", "estimating"}, {"updated_at_ms", now},
                    {"rate", 40.0}, {"estimated_share", .8}, {"recent_rate", 70.0}, {"steady_rate", 42.0}, {"cache_percent", 99.5},
                    {"last_response_at_ms", now - 5000}, {"last_response_rate", 39.0}, {"peak_rate", 70.0}, {"confirmed_turn_output", 1200}}}}}
        };
    }
    static void Main()
    {
        const double now = 1800000000000;
        var data = Fixture(now);
        var view = SpeedDisplay.Read(data, "current", now);
        Check(view.Ready && view.Estimated && view.Value == "40.0" && view.Mode == "实时估算" && view.LiveRate == 40,
            "headline follows the current live window rather than the slower ten-response average");
        Check(view.Recent == 42 && view.Cache == 99.5 && view.ConfirmedOutput == 1200, "reference speed and confirmed counters remain separate");
        Check(!SpeedDisplay.Read(data, "other", now).Ready, "speed never crosses the selected conversation title");
        data["updated_at_ms"] = now - 6001;
        Check(!SpeedDisplay.Read(data, "current", now).Ready, "expired collector stops the speed instrument");
        data = Fixture(now); Json.Object(data, "source")["state"] = "catching_up";
        Check(!SpeedDisplay.Read(data, "current", now).Ready, "incomplete source replay cannot drive a live gauge");
        data = Fixture(now);
        var frame = Json.Object(Json.Object(data, "metrics"), "live_speed");
        frame["updated_at_ms"] = now - 3001;
        Check(!SpeedDisplay.Read(data, "current", now).Ready, "a stalled live frame is distinct from a running collector");
        frame["updated_at_ms"] = now;
        foreach (var invalid in new object[] {true, "40", double.NaN, double.PositiveInfinity, -1.0})
        {
            frame["rate"] = invalid;
            Check(!SpeedDisplay.Read(data, "current", now).Ready, "invalid numeric value cannot become a speed: " + invalid);
        }
        frame["state"] = "warming"; frame["rate"] = null; frame["estimated_share"] = 0;
        view = SpeedDisplay.Read(data, "current", now);
        Check(!view.Ready && view.Mode == "建立基线" && view.Value == "—", "missing references do not produce an invented live value");
        frame["state"] = "tools"; frame["rate"] = 0.0;
        Json.Object(data, "metrics")["stage"] = "tools";
        view = SpeedDisplay.Read(data, "current", now);
        Check(view.Ready && view.Value == "39.0" && view.Mode == "工具中·保留" && view.LiveRate == 0 && view.Phase == "工具中",
            "tool pause retains the measured headline while the live window can reach zero");
        frame["state"] = "idle"; Json.Object(data, "metrics")["stage"] = "idle";
        view = SpeedDisplay.Read(data, "current", now);
        Check(view.Ready && view.Value == "39.0" && view.Mode == "上次实测" && view.Phase == "待机", "completed task retains the last real measurement");
        Json.Object(data, "metrics")["stage"] = "generating";
        Json.Object(data, "metrics")["turn_active"] = false;
        Check(SpeedDisplay.Read(data, "current", now).Phase == "待机", "completed lifecycle cannot show a generation animation");
        frame["steady_rate"] = null; frame["recent_rate"] = null; frame["last_response_rate"] = null;
        view = SpeedDisplay.Read(data, "current", now);
        Check(!view.Ready && view.Value == "—", "no response baseline displays a dash instead of a fabricated zero");
        data["range"] = new Dictionary<string, object> {{"thread_id", "t"}, {"rate", 18.5}, {"cache_percent", 80.0}, {"output_tokens", 5000}};
        view = SpeedDisplay.Read(data, "current", now);
        Check(view.State == "range" && view.Value == "18.5" && view.Mode == "区间均速" && !view.Estimated, "confirmed ranges retain their qualified display mode");
        Check(SpeedDisplay.Format(12345) == "12.3k" && SpeedDisplay.Tokens(1200) == "1.2k", "large values remain within the instrument width");
        var globalData = Fixture(now);
        var globalFrame = new Dictionary<string, object>(Json.Object(Json.Object(globalData, "metrics"), "live_speed"));
        globalFrame["scope"] = "global"; globalFrame["complete"] = true;
        globalFrame["generating"] = 2; globalFrame["tools"] = 1;
        globalFrame["rate"] = 100.0; globalFrame["average_30s"] = 83.0;
        globalData["global_speed"] = globalFrame;
        globalData["binding"] = "unbound"; globalData["title"] = "another project";
        view = SpeedDisplay.Read(globalData, "current", now);
        Check(view.Global && view.Ready && view.Rate == 100 && view.Generating == 2 && view.Tools == 1 && view.LastRate == 83,
            "global throughput and task counts do not depend on the selected project's binding");
        globalFrame["state"] = "tools"; globalFrame["rate"] = 0.0; globalFrame["generating"] = 0;
        view = SpeedDisplay.Read(globalData, "current", now);
        Check(view.Ready && view.Rate == 0 && view.Phase == "工具中", "all-global tool pause shows actual zero instead of a retained response speed");
        globalFrame["state"] = "idle"; globalFrame["tools"] = 0;
        Check(SpeedDisplay.Read(globalData, "current", now).Phase == "待机", "global idle is explicit");
        globalFrame["complete"] = false;
        Check(!SpeedDisplay.Read(globalData, "current", now).Ready, "incomplete discovery cannot pretend no tasks are producing");
        globalFrame["rate"] = 100.0; globalFrame["state"] = "measured";
        Check(SpeedDisplay.Read(globalData, "current", now).Mode == "部分采集", "partial coverage is qualified");
        globalFrame["updated_at_ms"] = now - 3001;
        Check(!SpeedDisplay.Read(globalData, "current", now).Ready, "stalled global frames never fall back to selected-session data");
        var rolling = new ReadoutTransition();
        rolling.Set("32.4", "a", 1000, true);
        Check(!rolling.Moving(1000), "initial values appear immediately without replaying a fake count-up");
        rolling.Set("58.7", "a", 1100, true);
        Check(rolling.Moving(1100) && rolling.Text == "58.7" && rolling.Previous == "32.4" && rolling.Direction == 1,
            "new actual digits roll upward in place");
        Check(rolling.Progress(1230) > .5 && rolling.Progress(1360) == 1 && !rolling.Moving(1360),
            "digit roll decelerates and finishes within 260 ms");
        rolling.Set("54.1", "a", 1200, true);
        Check(rolling.Text == "54.1" && rolling.Direction == -1 && rolling.ChangedAt == 1200,
            "rapid updates replace an unfinished transition instead of queueing stale numbers");
        rolling.Set("54.1", "a", 1250, true);
        Check(rolling.ChangedAt == 1200, "identical snapshots never restart the animation");
        rolling.Set("87.0", "b", 1300, true);
        Check(!rolling.Moving(1300), "switching conversation or model cannot animate unrelated old values");
        rolling.Set("−2.1", "b", 1400, false);
        Check(!rolling.Moving(1400) && rolling.Text == "−2.1", "reduced motion shows the signed final number immediately");
        var heatType = typeof(Hud).GetNestedType("HeatMotion", BindingFlags.NonPublic);
        var heat = Activator.CreateInstance(heatType, true);
        var update = heatType.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        var value = heatType.GetField("Value", BindingFlags.Instance | BindingFlags.NonPublic);
        var burst = heatType.GetField("CompletionAt", BindingFlags.Instance | BindingFlags.NonPublic);
        update.Invoke(heat, new object[] {0.0, 1000.0, true, "a", 0.0});
        update.Invoke(heat, new object[] {1.0, 1040.0, true, "a", 0.0});
        Check((double)value.GetValue(heat) > 0 && (double)value.GetValue(heat) < 1, "speed heat rises with an analytic spring");
        update.Invoke(heat, new object[] {1.0, 1800.0, true, "a", 10.0});
        Check((double)burst.GetValue(heat) == 1800, "the first newly completed response produces a ripple");
        update.Invoke(heat, new object[] {.2, 2000.0, false, "a", 10.0});
        Check((double)value.GetValue(heat) == .2, "static mode snaps to the correct heat without interpolation");
        update.Invoke(heat, new object[] {.5, 2100.0, true, "b", 100.0});
        Check((double)burst.GetValue(heat) < 0, "switching sessions cannot announce historical completions");
        var colorMethod = typeof(Hud).GetMethod("SpeedColor", BindingFlags.NonPublic | BindingFlags.Static);
        var colors = new[] { 8.0, 40.0, 90.0, 180.0 }.Select(rate => (Color)colorMethod.Invoke(null, new object[] {(double?)rate})).ToArray();
        Check(colors.Select(c => c.ToArgb()).Distinct().Count() == 4 && colors[0].B > colors[0].R && colors[3].R > colors[3].B,
            "speed colors progress from cool aqua through violet and rose to gold as measured rate rises");
        Check(colors[2].R > colors[2].G && colors[2].B > colors[2].G, "middle-high speed has a distinct orchid hue");
        foreach (double boundary in new[] {16.0, 32.0, 55.0, 85.0, 125.0, 165.0, 200.0})
        {
            var before = (Color)colorMethod.Invoke(null, new object[] {(double?)(boundary - .01)});
            var after = (Color)colorMethod.Invoke(null, new object[] {(double?)(boundary + .01)});
            Check(Math.Abs(before.R - after.R) + Math.Abs(before.G - after.G) + Math.Abs(before.B - after.B) < 6,
                "speed color is continuous across threshold " + boundary);
        }
        var rates = new[] {0.0, 12.0, 40.0, 80.0, 160.0, 1000.0}.Select(rate => SpectrumFlow.TargetRate(rate, false)).ToArray();
        Check(rates.Zip(rates.Skip(1), (a,b) => a < b).All(increases => increases) && rates.Last() <= .4,
            "rainbow velocity increases monotonically with the displayed speed and remains bounded");
        Check(SpectrumFlow.TargetRate(40, true) > SpectrumFlow.TargetRate(40, false) && SpectrumFlow.TargetRate(double.MaxValue, true) <= .5,
            "reset forecasts strengthen flowing color without an unbounded strobe");
        Check(new double?[] {null, double.NaN, double.PositiveInfinity, -1}.All(rate => SpectrumFlow.TargetRate(rate, false) == SpectrumFlow.TargetRate(0, false)),
            "missing or invalid speed falls back to a calm flow rather than stale fast motion");
        var flow = new SpectrumFlow(); flow.Update(1000, 12, false, true); flow.Update(1200, 12, false, true);
        double phase = flow.Phase, velocity = flow.CyclesPerSecond;
        flow.Update(1200, 160, false, true);
        Check(flow.Phase == phase && flow.CyclesPerSecond == velocity, "a changed speed never jumps the rainbow phase");
        flow.Update(1240, 160, false, true);
        Check(flow.CyclesPerSecond > velocity && flow.CyclesPerSecond < SpectrumFlow.TargetRate(160, false), "rainbow acceleration eases toward the new speed");
        var fine = new SpectrumFlow(); var coarse = new SpectrumFlow();
        fine.Update(0, 12, false, true); coarse.Update(0, 12, false, true);
        for (int t = 20; t <= 1000; t += 20) fine.Update(t, 160, false, true);
        for (int t = 40; t <= 1000; t += 40) coarse.Update(t, 160, false, true);
        Check(Math.Abs(fine.Phase - coarse.Phase) < 1e-9 && Math.Abs(fine.CyclesPerSecond - coarse.CyclesPerSecond) < 1e-9,
            "integrated flow has the same travel at different frame rates");
        phase = flow.Phase; flow.Update(1500, 180, true, false); flow.Update(30000, 180, true, false);
        Check(flow.Phase == phase, "static or memory-protected frames freeze the current color phase");
        flow.Update(100000, 180, true, true);
        double resumedTravel = (flow.Phase - phase + 1) % 1;
        Check(resumedTravel > 0 && resumedTravel <= .1251, "resuming after a long pause cannot fast-forward the rainbow");
        phase = flow.Phase; flow.Update(500, 180, true, true); flow.Update(double.NaN, 180, true, true);
        Check(flow.Phase == phase && !double.IsNaN(flow.Phase), "clock rollback and invalid clocks cannot corrupt the color phase");
        var orbit = new InstrumentFlow(); orbit.Update(1000, .2, true, true, "a"); orbit.Update(1040, .2, true, true, "a");
        double orbitPhase = orbit.Phase, orbitVelocity = orbit.Velocity;
        orbit.Update(1040, .9, true, true, "a");
        Check(orbit.Phase == orbitPhase && orbit.Velocity == orbitVelocity, "speed changes cannot teleport the luminous orbit");
        orbit.Update(1080, .9, true, true, "a");
        Check(orbit.Phase > orbitPhase && orbit.Velocity > orbitVelocity && orbit.Energy < .9,
            "instrument energy and angular velocity ease toward the new activity");
        orbitPhase = orbit.Phase; orbit.Update(30000, .9, true, true, "a");
        Check((orbit.Phase - orbitPhase + 1) % 1 < .065, "resume caps orbital travel instead of fast-forwarding a pause");
        orbitPhase = orbit.Phase; orbit.Update(31000, .9, true, false, "a");
        Check(orbit.Phase == orbitPhase, "static and memory-protected frames freeze orbital phase");
        orbit.Update(32000, .9, false, false, "a");
        Check(orbit.Energy == 0, "tool pause cannot masquerade as active generation energy");
        var receipt = new QuotaReceipt(); receipt.Observe(95, 200000, 1000, 1000, true);
        Check(receipt.Strength(1200) == 0, "initial quota snapshot never announces an old refill");
        receipt.Observe(95, 200000, 2000, 2000, true);
        Check(receipt.Strength(2200) == 0, "unchanged quota polling cannot replay a data receipt");
        receipt.Observe(94, 200000, 3000, 3000, true);
        Check(receipt.Strength(3300) > 0 && !receipt.Refilled && receipt.Delta == -1, "real debit receives a quiet update receipt");
        receipt.Observe(100, 800000, 4000, 4000, true);
        Check(receipt.Refilled && receipt.Strength(4500) > 0 && receipt.Delta == 6, "actual account refill has a distinct bounded receipt");
        receipt.Observe(2, 200000, 3500, 4600, true);
        Check(receipt.Refilled && receipt.Delta == 6, "late snapshots cannot overwrite the refill animation identity");
        receipt.Observe(100, 800000, 5000, 5000, false);
        Check(receipt.Strength(5100) == 0, "stale quota immediately cancels live receipts");
        var correction = new QuotaReceipt(); correction.Observe(94, 200000, 1000, 1000, true);
        correction.Observe(95, 200000, 2000, 2000, true);
        Check(!correction.Refilled, "one-point balance correction cannot announce an account refill");
        correction.Observe(99, 200000, 3000, 3000, true);
        correction.Observe(100, 200000, 4000, 4000, true);
        Check(correction.Refilled, "a confirmed return to full balance still receives refill feedback");
        var surge = new SpeedSurge();
        surge.Observe(100, 20, true, "a");
        surge.Observe(300, 22, true, "a");
        Check(surge.Started < 0, "entry and small fluctuations stay quiet");
        surge.Observe(600, 28, true, "a");
        Check(surge.Started == 600 && surge.Gain == 8, "a multi-sample acceleration earns one delta from the actual lower reading");
        for (int tick = 700; tick <= 3500; tick += 100) surge.Observe(tick, 28, true, "a");
        Check(surge.Started == 600, "holding speed cannot replay a surge");
        surge.Observe(3600, 160, true, "b");
        Check(surge.Started < 0, "model/session changes rebase acceleration");
        surge.Observe(3700, 200, false, "b");
        surge.Observe(3800, 210, true, "b");
        Check(surge.Started < 0, "static, protected, tool and idle frames disarm surge feedback");
        Console.WriteLine("Speed display checks passed: " + checks);
    }
}

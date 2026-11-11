using System;
using System.Collections.Generic;
using CodexTokenHud;

class ContextDisplayTests
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    static Dictionary<string, object> Fixture()
    {
        return new Dictionary<string, object> {
            {"binding", "ok"}, {"binding_mode", "auto"}, {"title", "A"}, {"thread_id", "a"},
            {"updated_at_ms", 1000000.0}, {"view_observed_at_ms", 1000000.0},
            {"source", new Dictionary<string, object> {{"state", "ok"}}},
            {"metrics", new Dictionary<string, object> {{"model", "m"}, {"context", new Dictionary<string, object> {
                {"input_tokens", 72000}, {"window_tokens", 100000}, {"sample_at_ms", 999000}, {"sample_model", "m"},
                {"stale_model", false}, {"pending_compaction", false}}}}}};
    }
    static ContextDisplay Read(Dictionary<string, object> d) { return ContextDisplay.Read(d, "A", 1001000); }
    static void Main()
    {
        var d = Fixture(); var c = Json.Object(Json.Object(d, "metrics"), "context");
        Check(Read(d).Ready && Read(d).Percent == 72, "reported input/window is visible for confirmed current chat");
        d["binding_mode"] = "activity"; Check(!Read(d).Ready, "background activity is never advertised as current context");
        d["binding_mode"] = "manual"; Check(Read(d).Ready && Read(d).Heading.Contains("固定"), "manual binding is explicitly labeled");
        d["binding_mode"] = "auto"; d["view_observed_at_ms"] = 990000;
        Check(!Read(d).Ready, "expired window identity cannot trigger warnings");
        d["binding_mode"] = "follow";
        Check(Read(d).Ready && Read(d).Heading.Contains("跟随"), "last viewed chat stays available while another application has focus");
        Check(!ContextDisplay.Read(d, "B", 1001000).Ready, "old chat sample cannot be shown under a newly selected title");
        d["binding_mode"] = "auto";
        d["view_observed_at_ms"] = 1000000; d["binding"] = "ambiguous";
        Check(!Read(d).Ready, "duplicate titles require a manual identity"); d["binding"] = "ok";
        c["window_tokens"] = null; Check(!Read(d).Ready && Read(d).Value == "—", "missing denominator is unknown rather than zero");
        c["window_tokens"] = 100000; c["stale_model"] = true;
        Check(!Read(d).Ready, "old model sample is not relabeled"); c["stale_model"] = false;
        c["compacted_at_ms"] = 1000000; c["pending_compaction"] = true;
        Check(!Read(d).Ready && Read(d).Hint.Contains("已压缩"), "explicit compaction waits for a replacement sample");
        c["pending_compaction"] = false; c["input_tokens"] = 20000;
        Check(Read(d).Ready && Read(d).Compacted, "low occupancy does not erase confirmed compaction history");
        var tracker = new ContextAlertTracker(); var view = Read(d);
        Action<double, double> step = (p, t) => { view.Percent = p; tracker.Observe(view, t); };
        step(69.99, 1); Check(tracker.Level == 0, "below preparation threshold remains neutral");
        step(70, 2); Check(tracker.Level == 1 && tracker.BurstAt == 2, "70 percent begins preparation once");
        step(69, 3); Check(tracker.Level == 1 && tracker.BurstAt == 2, "boundary noise does not pulse repeatedly");
        step(85, 4); Check(tracker.Level == 2 && tracker.BurstAt == 4, "85 percent upgrades to handoff advice");
        step(83, 5); Check(tracker.Level == 2, "critical state has three-point hysteresis");
        step(20, 6); Check(tracker.Level == 0, "actual occupancy drop lowers capacity alert");
        step(86, 7); Check(tracker.BurstAt == 4, "same session warning does not replay after compaction");
        view.Key = "b:m"; step(86, 8); Check(tracker.BurstAt == 8, "another session owns a separate warning history");
        view.Ready = false; tracker.Observe(view, 9); Check(tracker.Level == 0, "unavailable data never triggers an alert");
        Check(new RuntimeConfiguration().SpeedRedline == 300, "default speed range is 300 tok/s");
        var pace = new Dictionary<string,object>{{"method","official_balance_5m"},{"state","ready"},
            {"rate_pct_per_hour",6.4},{"span_seconds",300},{"sample_at_ms",1000000},{"reset_at",2000}};
        var quota = new Dictionary<string,object>{{"state","ok"},{"pace",pace},
            {"windows",new object[]{new Dictionary<string,object>{{"window_minutes",10080},{"remaining_percent",60},{"resets_at",2000}}}}};
        Check(QuotaPaceDisplay.Read(quota,1001000).Rate==6.4,"quota pace uses official-window rate with explicit hourly units");
        Check(!QuotaPaceDisplay.Read(quota,1200000).Rate.HasValue,"stale pace is unknown rather than zero");
        pace["state"]="warming"; Check(!QuotaPaceDisplay.Read(quota,1001000).Rate.HasValue,"insufficient pace samples do not invent a rate");
        pace["state"]="ready";quota["state"]="unavailable";
        Check(QuotaPaceDisplay.Read(quota,1001000).Cached,"temporarily cached pace is visibly qualified");
        Console.WriteLine("Context checks: " + count);
    }
}

using System;
using System.Collections.Generic;
using CodexTokenHud;

class ActivityDisplayTests
{
    static int checks;
    const double Now = 1800000000000;

    static Dictionary<string, object> Fixture(bool? active, double eventAgeSeconds, string stage)
    {
        return new Dictionary<string, object> {
            {"title", "current"}, {"binding", "ok"}, {"updated_at_ms", Now},
            {"source", new Dictionary<string, object> {{"state", "ok"}}},
            {"metrics", new Dictionary<string, object> {
                {"stage", stage}, {"turn_active", active}, {"last_event_at_ms", Now - eventAgeSeconds * 1000},
                {"last", new Dictionary<string, object> {{"measured_at_ms", Now - 600000}}} }} };
    }

    static void Network(Dictionary<string, object> data, string id, string state, double? delay, double ageSeconds)
    {
        data["network_latency"] = new Dictionary<string, object> {
            {"updated_at_ms", Now}, {"nodes", new object[] {new Dictionary<string, object> {
                {"id", id}, {"state", state}, {"delay_ms", delay}, {"measured_at_ms", Now - ageSeconds * 1000} }}} };
    }

    static ActivityDisplay Read(Dictionary<string, object> data)
    { return ActivityDisplay.Read(data, "current", Now); }

    static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }

    static void Main()
    {
        var data = Fixture(true, 12, "generating");
        var display = Read(data);
        Check(display.Active && display.State == "generating" && display.Severity == 0 && display.ElapsedText == "00:12",
            "activity uses event time rather than a ten minute old token sample");
        Check(display.EventAgeMs == 12000, "event age remains available for rendering");
        Check(display.AverageText == "--:--" && display.CompletedCount == 0, "no completed task does not invent an average");
        Json.Object(data, "metrics")["response_duration"] = new Dictionary<string, object> {{"mean_ms", 142000}, {"count", 12}};
        display = Read(data);
        Check(display.AverageText == "142.0s" && display.CompletedCount == 12 && display.AverageSampleText.Contains("12 次"),
            "response mean is displayed in seconds alongside live event time");
        Json.Object(Json.Object(data, "metrics"), "response_duration")["count"] = 1;
        Check(Read(data).AverageSampleText.Contains("待积累"), "one sample is explicitly marked preliminary");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -1, 0 })
        {
            Json.Object(Json.Object(data, "metrics"), "response_duration")["mean_ms"] = invalid;
            Check(Read(data).AverageText == "--:--", "invalid average is hidden: " + invalid);
        }
        Json.Object(Json.Object(data, "metrics"), "response_duration")["mean_ms"] = 1000;
        foreach (double invalid in new[] { -1, 0, 21, 1.5 })
        {
            Json.Object(Json.Object(data, "metrics"), "response_duration")["count"] = invalid;
            Check(Read(data).CompletedCount == 0, "invalid sample count is hidden: " + invalid);
        }
        Check(Read(Fixture(true, 15, "generating")).WaitLevel == .5 &&
            Read(Fixture(true, 45, "generating")).WaitLevel == 1.5 &&
            Read(Fixture(true, 90, "generating")).WaitLevel == 2.5,
            "wait color changes continuously through green yellow orange red");
        Check(Read(Fixture(true, 60, "tools")).WaitLevel == .5 && Read(Fixture(false, 900, "idle")).WaitLevel == 0,
            "tools use longer color thresholds and idle never escalates");
        Json.Object(data, "metrics")["last"] = null;
        Check(Read(data).ElapsedText == "00:12", "a new turn without token statistics still shows activity");

        foreach (var boundary in new[] {new[] {29, 0}, new[] {30, 1}, new[] {59, 1}, new[] {60, 2}, new[] {119, 2}, new[] {120, 3}})
            Check(Read(Fixture(true, boundary[0], "generating")).Severity == boundary[1], "generation threshold " + boundary[0]);
        foreach (var boundary in new[] {new[] {119, 0}, new[] {120, 1}, new[] {299, 1}, new[] {300, 2}, new[] {599, 2}, new[] {600, 3}})
            Check(Read(Fixture(true, boundary[0], "tools")).Severity == boundary[1], "tool threshold " + boundary[0]);
        display = Read(Fixture(true, 600, "tools"));
        Check(display.Title == "工具仍在等待" && display.State == "tools", "long tools are not labeled network failure");
        Check(Read(Fixture(true, 0, "tools")).Severity == 0, "fresh tool activity clears waiting emphasis");

        display = Read(Fixture(false, 90000, "generating"));
        Check(!display.Active && display.State == "idle" && display.Severity == 0 && display.ElapsedText == "25:00:00",
            "explicit completion wins over stale stage and long inactivity never alarms");
        display = Read(Fixture(null, 130, "generating"));
        Check(!display.Active && display.State == "unknown" && display.Severity == 0, "history without explicit start remains unknown");
        Check(Read(Fixture(true, 1799, "generating")).Severity == 3, "recent active turn retains long wait emphasis");
        display = Read(Fixture(true, 1800, "generating"));
        Check(!display.Active && display.State == "unknown" && display.Severity == 0 && display.Title == "任务状态待确认",
            "thirty minute old unfinished log is not asserted to still be running");

        data = Fixture(true, 130, "generating");
        data["range"] = new Dictionary<string, object> {{"rate", 20.0}, {"sample_count", 12}};
        Check(Read(data).Severity == 3, "confirmed statistic ranges do not replace live activity");
        data["updated_at_ms"] = Now - 6001;
        display = Read(data);
        Check(display.State == "unknown" && display.Title == "采集暂停" && display.Severity == 0 && !display.EventAgeMs.HasValue,
            "collector expiry takes priority over old activity");
        data["updated_at_ms"] = Now - 6000;
        Check(Read(data).Active, "collector heartbeat boundary is inclusive");
        Json.Object(data, "source")["state"] = "unavailable";
        Check(Read(data).Severity == 0 && !Read(data).Active, "unreadable source cannot trigger a wait warning");
        Json.Object(data, "source")["state"] = "catching_up";
        Check(Read(data).Title == "同步活动中" && Read(data).Severity == 0, "incomplete replay waits for fresh source");
        data = Fixture(true, 130, "generating");
        data["binding"] = "unbound";
        Check(Read(data).Title == "等待绑定" && Read(data).Severity == 0, "unbound conversation does not inherit an alarm");
        data["binding"] = "ok";
        Check(ActivityDisplay.Read(data, "different", Now).State == "unknown", "activity never crosses selected conversation");

        data = Fixture(true, 60, "generating");
        Json.Object(data, "metrics").Remove("last_event_at_ms");
        Check(Read(data).ElapsedText == "--:--" && !Read(data).Active, "missing event time does not fall back to token statistics");
        Json.Object(data, "metrics")["last_event_at_ms"] = Now + 31000;
        Check(Read(data).State == "unknown", "large future clock skew remains unknown");
        Json.Object(data, "metrics")["last_event_at_ms"] = Now + 1000;
        Check(Read(data).ElapsedText == "00:00" && Read(data).Severity == 0, "small clock skew is safely clamped");
        Check(ActivityDisplay.Elapsed(62000) == "01:02" && ActivityDisplay.Elapsed(3723000) == "01:02:03",
            "elapsed display keeps stable minute and hour groups");

        data = Fixture(true, 60, "generating");
        Network(data, "ai", "ok", 800, 75);
        Check(Read(data).Detail.Contains("可能影响响应"), "fresh slow AI probe is associated cautiously with waiting");
        Network(data, "ai", "ok", 799, 0);
        Check(Read(data).Detail == "节点探测正常 · 持续等待", "normal probe does not claim the model service is healthy");
        Network(data, "ai", "timeout", null, 10);
        Check(Read(data).Detail.Contains("探测超时") && Read(data).Detail.Contains("可能影响响应"), "fresh AI timeout is explained conditionally");
        Network(data, "ai", "timeout", null, 75.001);
        Check(!Read(data).Detail.Contains("AI"), "expired AI results do not explain current waiting");
        Network(data, "general", "timeout", null, 0);
        Check(!Read(data).Detail.Contains("可能影响响应"), "general route failure is not blamed for Codex waiting");
        Network(data, "ai", "unavailable", null, 0);
        Check(!Read(data).Detail.Contains("可能影响响应"), "unavailable probe is not diagnosed as an outage");
        Network(data, "ai", "ok", null, 0);
        Check(!Read(data).Detail.Contains("正常"), "missing delay is not represented as healthy");
        data = Fixture(true, 29, "generating");
        Network(data, "ai", "timeout", null, 0);
        Check(!Read(data).Detail.Contains("可能影响响应"), "slow network alone does not imply a stalled turn");
        data = Fixture(false, 300, "idle");
        Network(data, "ai", "timeout", null, 0);
        Check(Read(data).State == "idle" && !Read(data).Detail.Contains("可能影响响应"), "idle state never acquires network wait diagnosis");
        Console.WriteLine("Activity display checks passed: " + checks);
    }
}

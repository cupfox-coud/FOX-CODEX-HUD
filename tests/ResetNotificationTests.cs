using System;
using System.Collections.Generic;
using CodexTokenHud;

class ResetNotificationTests
{
    static double now = 1792400000000;
    static int checks;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    static Dictionary<string, object> Fixture()
    {
        return new Dictionary<string, object> {{"updated_at_ms", now}, {"reset_radar", new Dictionary<string, object> {
            {"state", "ok"}, {"fetched_at_ms", now}, {"source_updated_at_ms", now},
            {"latest_events", new object[] {new Dictionary<string, object> {{"id", "old"}, {"kind", "reset_completed"}, {"at_ms", now - 1000}}}}}}};
    }
    static void Schedule(Dictionary<string, object> data, string precision, double start, double end)
    {
        Json.Object(data, "reset_radar")["upcoming"] = new Dictionary<string, object> {
            {"id", "schedule"}, {"precision", precision}, {"start_ms", start}, {"end_ms", end}};
    }
    static void Main()
    {
        var data = Fixture();
        var saved = new Dictionary<string, object>();
        var tracker = new ResetNotificationTracker(saved);
        Check(tracker.Read(data, now) == null && tracker.Changed, "first poll records historical completions without announcing them");
        Check(tracker.Read(data, now) == null && !tracker.Changed, "unchanged poll does not repeatedly write state");
        Schedule(data, "exact", now + 3600000, now + 3600000);
        Check(tracker.Read(data, now).Message.Contains("1 小时"), "new schedule gives time remaining");
        Check(tracker.Read(data, now) == null, "same schedule notifies once");
        tracker = new ResetNotificationTracker(saved);
        Check(tracker.Read(data, now) == null, "saved deduplication survives restart");
        double later = now + 3000000;
        data["updated_at_ms"] = later;
        Json.Object(data, "reset_radar")["fetched_at_ms"] = later;
        Json.Object(data, "reset_radar")["source_updated_at_ms"] = later;
        Check(tracker.Read(data, later).Title.Contains("即将"), "ten minute warning is delivered once");
        Check(tracker.Read(data, later) == null, "approaching warning is not repeated");
        data = Fixture(); tracker = new ResetNotificationTracker(new Dictionary<string, object>());
        Schedule(data, "date", now + 500000, now + 86400000);
        var dateNotice = tracker.Read(data, now);
        Check(dateNotice.Message.Contains("具体时刻未定") && !dateNotice.Message.Contains("分钟"), "date precision never becomes an exact countdown notification");
        Check(tracker.Read(data, now) == null, "new near schedule does not immediately produce a second alert");
        data = Fixture(); tracker = new ResetNotificationTracker(new Dictionary<string, object>());
        Schedule(data, "unknown", now + 10000, now + 10000);
        Check(tracker.Read(data, now) == null, "unknown schedule precision cannot generate a countdown notification");
        Schedule(data, "exact", now - 1000, now - 1000);
        Check(tracker.Read(data, now) == null, "expired schedule is not falsely announced as a reset");
        Schedule(data, "date", 253402300800000d, 253402300800000d);
        Check(tracker.Read(data, now) == null, "out of range dates cannot generate a notification or throw");
        Schedule(data, "exact", now - 1000, now + 1000);
        Check(tracker.Read(data, now) == null, "inconsistent exact timing cannot generate a notification");
        Schedule(data, "exact", now + 10000, now + 10000);
        Json.Object(data, "reset_radar")["source_updated_at_ms"] = now - 1800001;
        Check(tracker.Read(data, now) == null, "stale source cannot trigger a notification");
        data = Fixture(); tracker = new ResetNotificationTracker(new Dictionary<string, object>());
        tracker.Read(data, now);
        Json.Object(data, "reset_radar")["latest_events"] = new object[] {new Dictionary<string, object> {
            {"id", "new"}, {"kind", "reset_completed"}, {"at_ms", now}}};
        Check(tracker.Read(data, now).Message.Contains("个人到账以账户额度为准"), "new public completion does not claim personal account recovery");
        Check(tracker.Read(data, now) == null, "completion is not repeated");
        Json.Object(data, "reset_radar")["latest_events"] = new object[] {new Dictionary<string, object> {
            {"id", "bank-credit"}, {"kind", "reset_completed"}, {"reset_type", "banked"}, {"at_ms", now}}};
        Check(tracker.Read(data, now).Message.Contains("储备重置次数发放"), "banked credits are distinguished from an automatic quota reset");
        data = Fixture(); tracker = new ResetNotificationTracker(new Dictionary<string, object>());
        Schedule(data, "exact", now + 10000, now + 10000);
        data["updated_at_ms"] = now - 7000;
        Check(tracker.Read(data, now) == null, "collector pause cannot notify from a stale snapshot");
        Check(ResetNotificationTracker.Countdown(1) == "1 分钟", "remaining time never rounds a positive delay to zero");
        Console.WriteLine("Reset notification checks passed: " + checks);
    }
}

using System;
using System.Collections.Generic;
using CodexTokenHud;

class ResetRadarDisplayTests
{
    const double Now = 1800000000000;
    static int checks;
    static Dictionary<string, object> Fixture()
    {
        return new Dictionary<string, object> {
            {"updated_at_ms", Now},
            {"reset_radar", new Dictionary<string, object> {
                {"state", "ok"}, {"fetched_at_ms", Now}, {"source_updated_at_ms", Now}, {"upcoming", null} }},
            {"account_quota", new Dictionary<string, object> {
                {"state", "ok"}, {"updated_at_ms", Now}, {"windows", new object[] {
                    new Dictionary<string, object> {{"window_minutes", 10080}, {"resets_at", (Now + (6 * 24 + 22) * 3600000) / 1000}} } } } } };
    }
    static void Upcoming(Dictionary<string, object> data, string precision, object start, object end)
    {
        Json.Object(data, "reset_radar")["upcoming"] = new Dictionary<string, object> {
            {"id", "event-1"}, {"kind", "global"}, {"precision", precision}, {"start_ms", start}, {"end_ms", end} };
    }
    static ResetRadarDisplay Read(Dictionary<string, object> data) { return ResetRadarDisplay.Read(data, Now); }
    static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }

    static void Main()
    {
        var data = Fixture();
        var display = Read(data);
        Check(display.Title == "重置雷达" && display.State == "none" && display.Countdown == "暂无排期", "no public schedule is stated explicitly");
        Check(display.Detail == "周重置 6天22时" && !display.Alert, "official weekly window appears as a separately labeled fallback");
        Check(display.Hint.Contains("当前账户官方") && display.Hint.Contains("Did Codex Reset"), "hint distinguishes official account and public schedule sources");

        Json.Object(data, "reset_radar")["reason"] = "schedule_elapsed_unconfirmed";
        display = Read(data);
        Check(display.State == "expired" && display.Countdown == "等待确认" && display.Detail == "计划时间已过 · 未确认" && !display.Alert,
            "backend-cleared elapsed schedule remains awaiting confirmation instead of absent");
        Check(!display.UpcomingStartMs.HasValue && !display.UpcomingEndMs.HasValue,
            "elapsed schedule reason cannot revive an old countdown");
        foreach (string reason in new[] {"schedule_time_unknown", "invalid_schedule"})
        {
            Json.Object(data, "reset_radar")["reason"] = reason;
            display = Read(data);
            Check(display.State == "unknown" && display.Countdown == "时间待确认" && display.Detail == "预告未给出明确时刻" && !display.Alert,
                reason + " keeps incomplete public timing explicit");
        }
        Json.Object(data, "reset_radar")["state"] = "stale";
        Check(Read(data).State == "stale", "source freshness takes precedence over schedule reason");
        data = Fixture();

        Upcoming(data, "exact", Now + 120000, null);
        display = Read(data);
        Check(display.State == "scheduled" && display.Countdown == "02:00" && display.Alert, "near exact plan has countdown and attention signal");
        Check(display.Detail.Contains("计划") && display.Detail.Contains("非到账") && display.UpcomingId == "event-1" && display.UpcomingStartMs == Now + 120000,
            "precise schedule does not claim confirmed delivery and preserves notification identity");
        Upcoming(data, "exact", Now + 1800001, null);
        Check(!Read(data).Alert, "distant plans do not alert early");
        Upcoming(data, "exact", Now - 1, null);
        Check(Read(data).State == "expired" && Read(data).Countdown == "等待确认" && !Read(data).Alert, "past exact timestamp waits for confirmation");
        Upcoming(data, "exact", Now - 1, Now + 60000);
        Check(Read(data).State == "expired" && !Read(data).Alert, "exact plan does not remain pending because a later end exists");

        Upcoming(data, "window", Now + 3600000, Now + 7200000);
        Check(Read(data).Countdown == "01:00:00" && Read(data).Detail == "距预计窗口开始", "future window counts to its beginning with a qualified label");
        Upcoming(data, "window", Now - 60000, Now + 60000);
        display = Read(data);
        Check(display.State == "window" && display.Countdown == "窗口进行中" && display.Alert && display.UpcomingEndMs == Now + 60000,
            "inside estimated window does not assert completed reset");
        Upcoming(data, "window", Now - 120000, Now);
        Check(Read(data).State == "expired" && !Read(data).Alert, "expired window does not remain active");
        Upcoming(data, "window", Now, null);
        Check(Read(data).State == "unknown" && !Read(data).Alert, "window without an end is not treated as precise");

        Upcoming(data, "date", Now + 86400000, Now + 172800000);
        display = Read(data);
        Check(display.State == "date" && display.Countdown.Contains("月") && display.Detail.Contains("时刻未定") && !display.Alert,
            "date-only plan never invents a seconds countdown");
        Upcoming(data, "date", Now - 1000, Now + 86400000);
        Check(Read(data).Countdown == "预计今日" && !Read(data).Alert, "same-day date plan does not trigger an exact-time alert");
        Upcoming(data, "unknown", Now + 60000, null);
        Check(Read(data).State == "unknown" && Read(data).Countdown == "时间待确认", "unknown precision remains unknown despite a timestamp");

        foreach (string state in new[] {"stale", "unavailable", "limited", "loading"})
        {
            data = Fixture(); Upcoming(data, "exact", Now + 60000, null);
            Json.Object(data, "reset_radar")["state"] = state;
            display = Read(data);
            Check(display.State == state && !display.Alert && !display.UpcomingStartMs.HasValue && display.Detail == "周重置 6天22时",
                state + " disables old public schedule but can retain independently fresh official week");
        }
        foreach (string field in new[] {"fetched_at_ms", "source_updated_at_ms"})
        {
            data = Fixture(); Upcoming(data, "exact", Now + 60000, null);
            double boundary = field == "fetched_at_ms" ? 900000 : 1800000;
            Json.Object(data, "reset_radar")[field] = Now - boundary;
            Check(Read(data).Alert, field + " freshness boundary accepted");
            Json.Object(data, "reset_radar")[field] = Now - boundary - 1;
            Check(Read(data).State == "stale" && !Read(data).Alert, field + " expired source prevents alerts");
            Json.Object(data, "reset_radar")[field] = Now + 30001;
            Check(Read(data).State == "stale", field + " future clock anomaly is rejected");
        }

        data = Fixture(); Upcoming(data, "exact", Now + 60000, null);
        data["updated_at_ms"] = Now - 6001;
        Check(Read(data).State == "paused" && !Read(data).Detail.Contains("周重置") && !Read(data).Alert, "dead collector disables all countdowns");
        data["updated_at_ms"] = Now - 6000;
        Check(Read(data).Alert, "collector boundary remains live");

        foreach (object invalid in new object[] {double.NaN, double.PositiveInfinity, double.MaxValue, -1.0, true, "bad", 253402300800000.0})
        {
            data = Fixture(); Upcoming(data, "exact", invalid, null);
            Check(Read(data).State == "unknown" && !Read(data).Alert, "invalid schedule timestamp safely rejected: " + invalid);
        }
        data = Fixture(); Upcoming(data, "window", Now + 60000, Now);
        Check(Read(data).State == "unknown", "backwards window is rejected");
        Upcoming(data, "exact", Now + 60000, double.NaN);
        Check(Read(data).State == "unknown", "invalid provided end is not ignored");
        Check(ResetRadarDisplay.Read(data, double.NaN).State == "paused" && ResetRadarDisplay.Read(data, double.MaxValue).State == "paused",
            "invalid current clock is safe");
        Upcoming(data, "date", 253402300799999.0, null);
        Check(Read(data).State == "date" && !Read(data).Alert, "maximum supported date converts without overflowing");
        Upcoming(data, "exact", 253402300799999.0, null);
        Check(Read(data).Countdown.Length <= 10 && !Read(data).Alert, "extremely distant valid timestamps remain bounded on the card");

        data = Fixture();
        Json.Object(data, "account_quota")["updated_at_ms"] = Now - 900001;
        Check(!Read(data).Detail.Contains("周重置"), "expired official snapshot is not used");
        Json.Object(data, "account_quota")["updated_at_ms"] = Now - 900000;
        Check(Read(data).Detail == "周重置 6天22时", "official freshness boundary accepted");
        Json.Object(data, "account_quota")["state"] = "unavailable";
        Check(!Read(data).Detail.Contains("周重置"), "failed official fetch is not represented as current");
        data = Fixture();
        Json.Object(data, "account_quota")["windows"] = new object[] {new Dictionary<string, object> {{"window_minutes", 300}, {"resets_at", (Now + 60000) / 1000}}};
        Check(!Read(data).Detail.Contains("周重置"), "five-hour quota is not mislabeled as weekly");
        Json.Object(data, "account_quota")["windows"] = new object[] {new Dictionary<string, object> {{"window_minutes", 10080}, {"resets_at", Now / 1000}}};
        Check(!Read(data).Detail.Contains("周重置"), "past official reset waits for a fresh account window");
        Json.Object(data, "account_quota")["windows"] = new object[] {new Dictionary<string, object> {{"window_minutes", 10080}, {"resets_at", double.MaxValue}}};
        Check(!Read(data).Detail.Contains("周重置"), "official numeric overflow cannot create a bogus countdown");
        Json.Object(data, "reset_radar")["latest_events"] = new object[] {new Dictionary<string, object> {{"id", "old"}, {"at_ms", Now + 100000}}};
        Check(Read(data).Countdown == "暂无排期" && !Read(data).Alert, "historical events never become future schedules or account reset windows");
        Console.WriteLine("Reset radar display checks passed: " + checks);
    }
}

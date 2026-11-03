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
            {"id", "event-1"}, {"kind", "reset_scheduled"}, {"reset_type", "global"}, {"precision", precision}, {"start_ms", start}, {"end_ms", end} };
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
            Check(display.State == "unknown" && display.Countdown == "暂无日期" && display.Detail == "等待网站补充时间" && !display.Alert,
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
        Check(display.State == "window" && display.Countdown == "01:00" && display.CountingDown && display.Alert && display.UpcomingEndMs == Now + 60000,
            "an open estimated window counts to its end without asserting completed reset");
        Upcoming(data, "window", Now - 120000, Now);
        Check(Read(data).State == "expired" && !Read(data).Alert, "expired window does not remain active");
        Upcoming(data, "window", Now, null);
        Check(Read(data).State == "unknown" && !Read(data).Alert, "window without an end is not treated as precise");

        Upcoming(data, "date", Now + 86400000, Now + 172800000);
        display = Read(data);
        Check(display.State == "date" && display.Countdown == "2天 00:00:00" && display.CountingDown &&
            display.CalendarLabel.StartsWith("窗口截至 ") && display.Detail.Contains("时刻未定") && !display.Alert,
            "date window counts to its supplied upper bound with a visible estimate qualifier");
        Upcoming(data, "date", Now - 1000, Now + 86400000);
        Check(Read(data).Countdown == "1天 00:00:00" && !Read(data).Alert, "date window uses its end instead of its possibly past starting date");
        Upcoming(data, "date", Now - 23 * 3600000, Now + 3600000);
        display = Read(data);
        var nextSecond = ResetRadarDisplay.Read(data, Now + 1000);
        Check(display.Countdown == "01:00:00" && nextSecond.Countdown == "59:59",
            "the live date-window regression decrements locally across an hour boundary");
        Check(display.SignalKey == nextSecond.SignalKey && display.CalendarLabel == nextSecond.CalendarLabel,
            "seconds ticking cannot create repeated signals or replace the calendar reference");
        Check(ResetRadarDisplay.Read(data, Now + 3600000).State == "paused",
            "an expired collector never keeps an old countdown running");
        Upcoming(data, "date", Now + 86400000, null);
        Check(Read(data).CalendarTime && !Read(data).CountingDown && Read(data).Detail.Contains("时刻未定"),
            "a date without a supplied end never invents a cutoff");
        Upcoming(data, "unknown", Now + 60000, null);
        Check(Read(data).State == "estimated" && Read(data).CountingDown && Read(data).Countdown == "01:00" && Read(data).CalendarLabel.StartsWith("预计 "),
            "publisher timestamp with unspecified precision becomes a qualified countdown");

        Upcoming(data, "deadline", Now - 60000, Now + 600000);
        display = Read(data);
        string cutoff = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(Now + 600000)
            .ToLocalTime().ToString("MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Check(display.State == "deadline" && display.CountingDown && display.Countdown == "10:00" && display.CalendarLabel == "最晚 " + cutoff + " 前",
            "deadline leads with a countdown while retaining the API's upper-bound date");
        Check(display.Title.Contains("预计") && display.Detail == "距预估截止" && display.Alert,
            "deadline countdown keeps its estimate and target labels");
        Check(display.Hint.Contains("直接来自 API") && display.Hint.Contains("时区"),
            "deadline hint retains source and timezone attribution");
        Upcoming(data, "unknown", Now + 600000, Now + 600000);
        Json.Object(Json.Object(data, "reset_radar"), "upcoming")["constraint"] = "deadline";
        Check(Read(data).CalendarLabel == "最晚 " + cutoff + " 前" && Read(data).Countdown == "10:00", "older collectors' deadline constraint retains the cutoff and countdown");
        Upcoming(data, "estimated", Now + 600000, Now + 600000);
        Check(Read(data).CalendarLabel == "预计 " + cutoff && Read(data).CountingDown && Read(data).Countdown == "10:00",
            "inferred point counts to its supplied date without waiting for exact precision");
        Upcoming(data, "deadline", Now - 600000, Now - 600000);
        display = Read(data);
        Check(display.State == "expired" && !display.CountingDown && display.CalendarLabel.StartsWith("最晚 ") &&
            display.Countdown == "预估已过" && display.Detail == "等待网站更新" && !display.Alert && !display.UpcomingStartMs.HasValue,
            "elapsed prediction retains the reference date while stopping its alert");
        Upcoming(data, "deadline", double.NaN, Now + 600000);
        Check(Read(data).State == "unknown" && !Read(data).CalendarTime, "invalid deadline cannot create an absolute-date label");
        Upcoming(data, "deadline", Now + 600000, Now + 300000);
        Check(Read(data).State == "unknown", "backwards deadline bounds remain invalid");

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
        data = Fixture();
        var completion = new Dictionary<string, object> {{"id", "completed-1"}, {"kind", "reset_completed"},
            {"reset_type", "global"}, {"at_ms", Now - 480000}, {"time_basis", "announced"}};
        var radar = Json.Object(data, "reset_radar");
        radar["latest_events"] = new object[] { completion };
        display = Read(data);
        Check(display.State == "completed" && display.Countdown == "已重置" && display.Detail == "公告 · 8分钟前",
            "recent API completion replaces no-schedule with a timestamped public completion");
        Check(display.Title.Contains("公开记录") && display.Hint.Contains("实际重置时刻未提供") &&
            !display.Alert && !display.UpcomingStartMs.HasValue, "announcement time is qualified and is not an account confirmation or new countdown");
        completion["reset_type"] = "banked";
        Check(Read(data).Countdown == "重置卡已发放", "banked credit distribution is not labeled a quota reset");
        completion["reset_type"] = "global_and_banked";
        Check(Read(data).Countdown == "已重置" && Read(data).Hint.Contains("同时包含"), "combined reset retains credit context");
        completion["time_basis"] = "completed";
        completion["at_ms"] = Now - 20000;
        Check(Read(data).Detail == "完成 · 刚刚", "actual completion time receives its own label");
        completion["at_ms"] = Now - 36000000;
        Check(Read(data).State == "completed" && Read(data).Detail == "完成 · 10小时前", "10 hour completion boundary is retained");
        completion["at_ms"] = Now - 36000001;
        Check(Read(data).State == "none", "old completion expires instead of looking like a new reset forever");
        foreach (object invalid in new object[] {Now + 30001, double.NaN, double.PositiveInfinity, true, -1})
        {
            completion["at_ms"] = invalid;
            Check(Read(data).State == "none", "invalid or future completion is hidden: " + invalid);
        }
        completion["at_ms"] = Now - 480000;
        completion["scope"] = new Dictionary<string, object> {{"plans", new object[] {"plus"}}};
        Check(Read(data).State == "none", "other plan's completion does not appear as a Pro reset");
        completion["scope"] = new Dictionary<string, object> {{"plans", new object[] {"pro"}}, {"windows", new object[] {"five_hour"}}};
        Check(Read(data).State == "none", "five-hour-only completion does not appear as a weekly reset");
        completion["scope"] = null;
        foreach (string state in new[] {"unavailable", "stale", "limited"})
        {
            radar["state"] = state;
            Check(Read(data).State == state, "failed or stale API retains honest status instead of a fresh completion");
        }
        radar["state"] = "ok";
        Upcoming(data, "exact", Now + 600000, null);
        Check(Read(data).State == "scheduled" && Read(data).Countdown == "10:00", "new valid schedule takes priority over a recent completion");
        radar["upcoming"] = null;
        radar["reason"] = "schedule_elapsed_unconfirmed";
        Check(Read(data).State == "completed", "real completion remains visible when backend clears an elapsed plan");
        radar["latest_events"] = new object[] {completion, new Dictionary<string, object> {
            {"id", "newer"}, {"kind", "reset_completed"}, {"reset_type", "banked"}, {"at_ms", Now - 1000}}};
        Check(Read(data).Countdown == "重置卡已发放", "newest relevant event wins regardless of source list ordering");
        foreach (string precision in new[] {"deadline", "estimated", "exact", "date", "window"})
        {
            data = Fixture();
            Upcoming(data, precision, Now + 3600000, precision == "date" || precision == "window" ? Now + 7200000 : Now + 3600000);
            var upcoming = Json.Object(Json.Object(data, "reset_radar"), "upcoming");
            var global = Read(data);
            upcoming["reset_type"] = "banked";
            var cards = Read(data);
            Check(cards.HasSignal && !cards.ResetsQuota && cards.EventHeading == "重置卡发放" && cards.EventBadge == "不自动重置额度",
                precision + " card grant is explicitly labeled and cannot authorize quota encouragement");
            Check(cards.Countdown == global.Countdown && cards.CalendarLabel == global.CalendarLabel && cards.SignalKey != global.SignalKey,
                precision + " keeps API timing but distinguishes a changed event type");
            upcoming["reset_type"] = "global_and_banked";
            Check(Read(data).ResetsQuota && Read(data).EventHeading == "重置＋发卡", precision + " combined event explicitly retains both effects");
            foreach (string unknown in new[] {"", "unknown", "future_type", "GLOBAL"})
            {
                upcoming["reset_type"] = unknown;
                Check(!Read(data).ResetsQuota && Read(data).EventBadge == "事件类型未明" && Read(data).Hint.Contains("请勿据此提前清空"),
                    precision + " does not infer a reset from an unsupported type " + unknown);
            }
            upcoming.Remove("reset_type"); upcoming["kind"] = "global";
            Check(!Read(data).ResetsQuota, "missing type cannot be inferred from kind or timing precision");
        }
        data = Fixture(); Upcoming(data, "deadline", Now - 1000, Now - 1000);
        Json.Object(data, "reset_radar")["latest_events"] = new object[] {new Dictionary<string, object> {
            {"id", "card-after-global"}, {"kind", "reset_completed"}, {"reset_type", "banked"}, {"at_ms", Now - 500}}};
        var superseding = Read(data);
        Check(superseding.State == "completed" && !superseding.ResetsQuota && superseding.Hint.Contains("仅发放重置卡") &&
            !superseding.Hint.Contains("事件类型：全局额度重置"),
            "card completion replacing an expired global forecast discards the old forecast's advice");
        Console.WriteLine("Reset radar display checks passed: " + checks);
    }
}

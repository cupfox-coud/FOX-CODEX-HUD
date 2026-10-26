using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexTokenHud
{
    // Third-party schedules and the signed-in account's weekly window are separate facts.
    internal sealed class ResetRadarDisplay
    {
        internal string Title = "重置雷达";
        internal string Countdown = "正在同步";
        internal string Detail = "等待公开排期";
        internal string Hint = "公开排期来自 Did Codex Reset；时间可能变化，不代表本账号已到账。";
        internal string State = "loading";
        internal bool Alert;
        internal bool CalendarTime;
        internal string UpcomingId = "";
        internal double? UpcomingStartMs;
        internal double? UpcomingEndMs;
        internal string PredictionKind = "", CompletionEventId = "";
        internal bool HasSignal { get { return State == "completed" || State == "deadline" ||
            State == "estimated" || State == "scheduled" || State == "window" || State == "date"; } }
        internal string SignalIdentity { get { return State == "completed" ? CompletionEventId : UpcomingId; } }
        internal string SignalKey { get { return !HasSignal ? "" : State == "completed" ?
            "complete:" + CompletionEventId + ":" + Countdown : "schedule:" + UpcomingId + ":" + PredictionKind + ":" +
            UpcomingStartMs.GetValueOrDefault().ToString("0", CultureInfo.InvariantCulture) + ":" +
            UpcomingEndMs.GetValueOrDefault().ToString("0", CultureInfo.InvariantCulture); } }

        private const double MaxEpochMs = 253402300799999;
        private const double DayMs = 86400000;

        private static double? Number(Dictionary<string, object> value, string key)
        {
            if (Json.Get(value, key) is bool) return null;
            double? number = Json.Number(value, key);
            return number.HasValue && !double.IsNaN(number.Value) && !double.IsInfinity(number.Value) ? number : null;
        }

        private static bool Timestamp(double? value)
        { return value.HasValue && value.Value > 0 && value.Value <= MaxEpochMs; }

        private static bool Fresh(double? value, double nowMs, double maxAgeMs)
        { return Timestamp(value) && value.Value <= nowMs + 30000 && nowMs - value.Value <= maxAgeMs; }

        private static string Duration(double durationMs)
        {
            // Inputs have already been bounded to valid Unix dates; clamp before casting.
            long seconds = (long)Math.Max(0, Math.Min(MaxEpochMs / 1000, Math.Ceiling(durationMs / 1000)));
            long days = seconds / 86400;
            if (days >= 100) return days.ToString(CultureInfo.InvariantCulture) + "天";
            if (days > 0) return days.ToString(CultureInfo.InvariantCulture) + "天" + ((seconds % 86400) / 3600).ToString(CultureInfo.InvariantCulture) + "时";
            if (seconds >= 3600) return (seconds / 3600).ToString("00", CultureInfo.InvariantCulture) + ":" +
                ((seconds % 3600) / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
            return (seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static DateTime LocalDate(double timestamp)
        { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(timestamp).ToLocalTime(); }

        private static string OfficialWeekly(Dictionary<string, object> data, double nowMs)
        {
            var quota = Json.Object(data, "account_quota");
            if (Json.Text(quota, "state") != "ok" || !Fresh(Number(quota, "updated_at_ms"), nowMs, 900000)) return null;
            double? earliest = null;
            foreach (var window in Json.List(quota, "windows"))
            {
                if (Number(window, "window_minutes") != 10080) continue;
                double? seconds = Number(window, "resets_at");
                if (!seconds.HasValue || seconds.Value <= 0 || seconds.Value > MaxEpochMs / 1000) continue;
                double resetMs = seconds.Value * 1000;
                if (resetMs <= nowMs || resetMs - nowMs > 7 * DayMs + 30000) continue;
                if (!earliest.HasValue || resetMs < earliest.Value) earliest = resetMs;
            }
            return earliest.HasValue ? "周重置 " + Duration(earliest.Value - nowMs) : null;
        }

        private static bool RelevantScope(Dictionary<string, object> item)
        {
            var scope = Json.Object(item, "scope");
            foreach (string key in new[] { "plans", "windows" })
            {
                var values = Json.Get(scope, key) as System.Collections.IEnumerable;
                if (values == null || values is string) continue;
                var names = values.Cast<object>().OfType<string>().Select(s => s.ToLowerInvariant()).ToArray();
                var allowed = key == "plans" ? new[] { "pro", "pro_5x", "pro_20x", "pro5x", "pro20x" } :
                    new[] { "weekly", "week", "seven_day" };
                if (names.Length > 0 && !names.Any(s => allowed.Contains(s) ||
                    new[] { "unknown", "all", "all_plans", "all_windows" }.Contains(s))) return false;
            }
            return true;
        }

        private static bool ShowRecentCompletion(ResetRadarDisplay result, Dictionary<string, object> radar, double nowMs)
        {
            // Use the publisher's event time, never the fetch time or an elapsed plan.
            var item = Json.List(radar, "latest_events").Where(e =>
                Json.Text(e, "kind") == "reset_completed" && Json.Text(e, "id").Length > 0 &&
                new[] { "global", "banked", "global_and_banked" }.Contains(Json.Text(e, "reset_type")) &&
                Fresh(Number(e, "at_ms"), nowMs, 10 * 3600000) && RelevantScope(e))
                .OrderByDescending(e => Number(e, "at_ms")).FirstOrDefault();
            if (item == null) return false;
            double age = Math.Max(0, nowMs - Number(item, "at_ms").Value);
            string ago = age < 60000 ? "刚刚" : age < 3600000 ?
                ((int)(age / 60000)).ToString(CultureInfo.InvariantCulture) + "分钟前" :
                ((int)(age / 3600000)).ToString(CultureInfo.InvariantCulture) + "小时前";
            string basis = Json.Text(item, "time_basis") == "announced" ? "公告" : "完成";
            result.State = "completed";
            result.CompletionEventId = Json.Text(item, "id");
            result.Title = "重置雷达 · 公开记录";
            result.Countdown = Json.Text(item, "reset_type") == "banked" ? "次数已发放" : "已重置";
            result.Detail = basis + " · " + ago;
            result.Alert = false;
            result.UpcomingId = "";
            result.UpcomingStartMs = result.UpcomingEndMs = null;
            result.Hint += " 网站已记录本次完成事件，最近 10 小时保留此提示；个人到账请查看左侧账户额度。" +
                (basis == "公告" ? " 距今时间按公告发布时间计算，实际重置时刻未提供。" : "") +
                (Json.Text(item, "reset_type") == "global_and_banked" ? " 同时包含储备重置次数发放。" : "");
            return true;
        }

        internal static ResetRadarDisplay Read(Dictionary<string, object> data, double nowMs)
        {
            var result = new ResetRadarDisplay();
            if (!Timestamp(nowMs) || !Fresh(Number(data, "updated_at_ms"), nowMs, 6000))
            {
                result.State = "paused";
                result.Countdown = "采集暂停";
                result.Detail = "等待重新同步";
                result.Hint += " 采集心跳已失效，排期与官方窗口均停止显示倒计时。";
                return result;
            }

            string weekly = OfficialWeekly(data, nowMs);
            if (weekly != null) result.Hint += " “周重置”来自当前账户官方周额度窗口，与公共额外重置排期不同。";
            var radar = Json.Object(data, "reset_radar");
            string state = Json.Text(radar, "state");
            string reason = null;
            if (state == "loading" || radar == null)
            { result.State = "loading"; result.Countdown = "正在同步"; reason = "等待公开排期"; }
            else if (state == "limited")
            { result.State = "limited"; result.Countdown = "请求受限"; reason = "稍后自动重试"; }
            else if (state == "unavailable")
            { result.State = "unavailable"; result.Countdown = "暂时离线"; reason = "公开排期暂不可用"; }
            else if (state == "stale" || !Fresh(Number(radar, "fetched_at_ms"), nowMs, 900000) ||
                     !Fresh(Number(radar, "source_updated_at_ms"), nowMs, 1800000))
            { result.State = "stale"; result.Countdown = "数据过期"; reason = "等待排期更新"; }
            else if (state != "ok")
            { result.State = "unavailable"; result.Countdown = "状态待确认"; reason = "公开排期暂不可用"; }
            if (reason != null)
            {
                result.Detail = weekly ?? reason;
                result.Hint += " " + reason + "；旧排期不触发提醒。";
                return result;
            }

            var upcoming = Json.Object(radar, "upcoming");
            if (upcoming == null)
            {
                if (ShowRecentCompletion(result, radar, nowMs)) return result;
                string scheduleReason = Json.Text(radar, "reason");
                if (scheduleReason == "schedule_elapsed_unconfirmed")
                {
                    result.State = "expired";
                    result.Countdown = "等待确认";
                    result.Detail = "计划时间已过 · 未确认";
                    result.Hint += " 预告时间或窗口已过，仍需确认事件完成和本账号到账。";
                    return result;
                }
                if (scheduleReason == "schedule_time_unknown" || scheduleReason == "invalid_schedule")
                {
                    result.State = "unknown";
                    result.Countdown = "暂无日期";
                    result.Detail = "等待网站补充时间";
                    result.Hint += " 网站尚未提供有效日期。";
                    return result;
                }
                result.State = "none";
                result.Countdown = "暂无排期";
                result.Detail = weekly ?? "尚无新的重置预告";
                return result;
            }

            string precision = Json.Text(upcoming, "precision");
            double? start = Number(upcoming, "start_ms");
            double? end = Number(upcoming, "end_ms");
            bool endWasProvided = Json.Get(upcoming, "end_ms") != null;
            bool deadline = precision == "deadline" ||
                (precision != "date" && Json.Text(upcoming, "constraint") == "deadline");
            bool calendar = deadline || precision == "estimated" || precision == "unknown";
            if (!Timestamp(start) || (endWasProvided && (!Timestamp(end) || end.Value < start.Value)) ||
                (!calendar && precision != "exact" && precision != "window" && precision != "date") ||
                (precision == "window" && (!Timestamp(end) || end.Value <= start.Value)))
            {
                result.State = "unknown";
                result.Countdown = "暂无日期";
                result.Detail = "等待网站补充时间";
                return result;
            }

            if (calendar)
            {
                result.PredictionKind = deadline ? "deadline" : "estimated";
                double target = deadline && end.HasValue ? end.Value : start.Value;
                bool elapsed = target <= nowMs;
                if (elapsed && ShowRecentCompletion(result, radar, nowMs)) return result;
                DateTime date = LocalDate(target);
                string format = date.Year == LocalDate(nowMs).Year ? "MM/dd HH:mm" : "yyyy/MM/dd HH:mm";
                result.Title = "重置雷达 · 预计";
                result.State = elapsed ? "expired" : deadline ? "deadline" : "estimated";
                result.CalendarTime = true;
                result.Countdown = (deadline ? "最晚 " : "预计 ") + date.ToString(format, CultureInfo.InvariantCulture) + (deadline ? " 前" : "");
                result.Detail = elapsed ? "预估已过 · 等待更新" : "剩余 " + Duration(target - nowMs);
                result.Alert = !elapsed && target - nowMs <= 1800000;
                if (!elapsed)
                {
                    result.UpcomingId = Json.Text(upcoming, "id");
                    result.UpcomingStartMs = start;
                    result.UpcomingEndMs = end;
                }
                result.Hint += " 网站" + (deadline ? "预计最晚 " : "推测时间 ") +
                    date.ToString("yyyy年MM月dd日 HH:mm", CultureInfo.InvariantCulture) + (deadline ? " 之前。" : "。") +
                    " 此日期直接来自 API，按本机时区显示，会随网站预告更新。";
                return result;
            }

            result.UpcomingId = Json.Text(upcoming, "id");
            result.PredictionKind = precision;
            result.UpcomingStartMs = start;
            result.UpcomingEndMs = end;
            bool expired = precision == "exact" ? nowMs >= start.Value : end.HasValue ? nowMs >= end.Value :
                precision == "date" && LocalDate(nowMs).Date > LocalDate(start.Value).Date;
            if (expired)
            {
                if (ShowRecentCompletion(result, radar, nowMs)) return result;
                result.State = "expired";
                result.Countdown = "等待确认";
                result.Detail = "计划时间已过 · 未确认";
                result.Hint += " 预告时间或窗口已过，仍需确认事件完成和本账号到账。";
                return result;
            }

            if (precision == "date")
            {
                result.State = "date";
                result.Countdown = LocalDate(nowMs).Date == LocalDate(start.Value).Date ? "预计今日" : LocalDate(start.Value).ToString("MM月dd日", CultureInfo.InvariantCulture);
                result.Detail = "日期预告 · 时刻未定";
                result.Hint += " 此预告只有日期，不提供精确到账倒计时。";
                return result;
            }

            bool windowOpen = precision == "window" && nowMs >= start.Value;
            result.State = windowOpen ? "window" : "scheduled";
            result.Countdown = windowOpen ? "窗口进行中" : Duration(start.Value - nowMs);
            result.Detail = windowOpen ? "预计窗口 · 等待确认" : precision == "window" ? "距预计窗口开始" : "距计划时间 · 非到账";
            result.Alert = windowOpen || start.Value - nowMs <= 1800000;
            result.Hint += " " + (precision == "window" ? "这是预计时间范围，不是精确到账时刻。" : "这是公开计划时间，不是本账号额度已重置的确认。");
            return result;
        }
    }
}

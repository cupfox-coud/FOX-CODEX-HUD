using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CodexTokenHud
{
    internal sealed class ResetNotice
    {
        internal string Title, Message, Kind;
    }

    // Persistent event IDs suppress old announcements and repeated notifications after restart.
    internal sealed class ResetNotificationTracker
    {
        internal readonly Dictionary<string, object> Saved;
        internal bool Changed;
        internal ResetNotificationTracker(Dictionary<string, object> saved) { Saved = saved; }
        private bool Seen(string key) { return Saved.ContainsKey(key); }
        private void Mark(string key, double now) { Saved[key] = now; Changed = true; }
        private static bool Recent(double? at, double now, double maxAge)
        { return at.HasValue && !double.IsNaN(at.Value) && !double.IsInfinity(at.Value) && at.Value > 0 && now - at.Value >= -30000 && now - at.Value <= maxAge; }

        internal ResetNotice Read(Dictionary<string, object> data, double now)
        {
            Changed = false;
            var radar = Json.Object(data, "reset_radar");
            if (Json.Text(radar, "state") != "ok" || !Recent(Json.Number(data, "updated_at_ms"), now, 6000) ||
                !Recent(Json.Number(radar, "fetched_at_ms"), now, 900000) ||
                !Recent(Json.Number(radar, "source_updated_at_ms"), now, 1800000)) return null;
            var events = Json.List(radar, "latest_events");
            bool baseline = !Seen("baseline");
            ResetNotice notice = null;
            foreach (var item in events)
            {
                string id = Json.Text(item, "id");
                if (id.Length == 0 || id.Length > 180 || Json.Text(item, "kind") != "reset_completed") continue;
                string key = "complete:" + id;
                if (Seen(key)) continue;
                double? at = Json.Number(item, "at_ms");
                if (baseline || !Recent(at, now, 21600000)) { Mark(key, now); continue; }
                Mark(key, now);
                string eventType = Json.Text(item, "reset_type");
                string eventLabel = eventType == "banked" ? "储备重置次数发放" :
                    eventType == "global_and_banked" ? "额度重置及储备次数发放" : "额度重置";
                notice = new ResetNotice { Kind = "completed", Title = "检测到新的公共重置信号", Message =
                    "Did Codex Reset 报告了新的" + eventLabel + "。个人到账以账户额度为准；HUD 正在重新同步。" };
                break;
            }
            if (baseline) Mark("baseline", now);
            if (notice != null) return notice;

            var upcoming = Json.Object(radar, "upcoming");
            string scheduleId = Json.Text(upcoming, "id"), precision = Json.Text(upcoming, "precision");
            double? start = Json.Number(upcoming, "start_ms"), end = Json.Number(upcoming, "end_ms");
            if (scheduleId.Length == 0 || scheduleId.Length > 180 || !start.HasValue || !end.HasValue ||
                double.IsNaN(start.Value) || double.IsNaN(end.Value) || double.IsInfinity(start.Value) || double.IsInfinity(end.Value) ||
                start.Value <= 0 || end.Value > 253402300799999d || end.Value < start.Value || end.Value <= now ||
                ((precision == "exact" || precision == "estimated") && (start.Value != end.Value || start.Value < now)) ||
                (precision != "exact" && precision != "estimated" && precision != "deadline" && precision != "date" && precision != "window")) return null;
            double target = precision == "deadline" ? end.Value : start.Value;
            string token = scheduleId + ":" + precision + ":" + start.Value.ToString("0", CultureInfo.InvariantCulture) +
                ":" + end.Value.ToString("0", CultureInfo.InvariantCulture);
            string keySchedule = "schedule:" + token, keySoon = "soon:" + token;
            double remaining = target - now;
            string absolute = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMilliseconds(target).ToLocalTime().ToString("MM月dd日 HH:mm", CultureInfo.InvariantCulture);
            if (!Seen(keySchedule))
            {
                bool changed = Json.Text(Saved, "active-schedule-id") == scheduleId && Json.Text(Saved, "active-schedule") != token;
                Mark(keySchedule, now);
                Saved["active-schedule-id"] = scheduleId;
                Saved["active-schedule"] = token;
                if (remaining <= 600000) Mark(keySoon, now);
                string timing = precision == "deadline" ? "预计最晚 " + absolute + " 前，距预估截止约 " + Countdown(remaining) + "。" :
                    precision == "estimated" ? "推测重置时间为 " + absolute + "，约 " + Countdown(remaining) + " 后。" :
                    precision == "date" ? "公布了 " + new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMilliseconds(start.Value).ToLocalTime().ToString("MM月dd日") + " 的重置预告，具体时刻未定。" :
                    remaining <= 0 ? "预计窗口已开始，具体到账时间待确认。" :
                    "距" + (precision == "exact" ? "计划时间" : "预计窗口开始") + "约 " + Countdown(remaining) + "。";
                return new ResetNotice { Kind = changed ? "schedule_changed" : "forecast", Title = changed ? "重置预告已更新" : "发现新的重置排期", Message =
                    "Did Codex Reset：" + timing + (Json.Text(upcoming, "reset_type") == "banked" ? "本次是储备重置次数发放。" : "") +
                    "这是公共排期，个人额度以实际同步为准。" };
            }
            if (precision != "date" && remaining > 0 && remaining <= 600000 && !Seen(keySoon))
            {
                Mark(keySoon, now);
                return new ResetNotice { Kind = "soon", Title = precision == "deadline" ? "预估截止时间将近" : "重置排期即将开始", Message =
                    (precision == "deadline" ? "预计最晚 " + absolute + " 前，距预估截止约 " :
                        precision == "estimated" ? "距网站推测时间约 " :
                        "距公共" + (precision == "exact" ? "计划时间" : "预计窗口") + "约 ") +
                    Countdown(remaining) + "。来源：Did Codex Reset；网站预估可能更新。" };
            }
            if (Saved.Count > 128)
                foreach (string key in Saved.Where(p => p.Key != "baseline" && p.Key != "active-schedule" && p.Key != "active-schedule-id")
                    .OrderBy(p => Convert.ToDouble(p.Value, CultureInfo.InvariantCulture)).Take(Saved.Count - 96).Select(p => p.Key).ToList())
                { Saved.Remove(key); Changed = true; }
            return null;
        }

        internal static string Countdown(double ms)
        {
            double minutes = Math.Ceiling(Math.Max(0, ms) / 60000);
            if (minutes >= 1440) return Math.Floor(minutes / 1440).ToString("0") + " 天 " + Math.Floor(minutes % 1440 / 60).ToString("0") + " 小时";
            if (minutes >= 60) return Math.Floor(minutes / 60).ToString("0") + " 小时 " + (minutes % 60).ToString("0") + " 分钟";
            return minutes.ToString("0") + " 分钟";
        }
    }

    internal sealed partial class Hud
    {
        private ResetRadarDisplay resetRadar;
        private ResetNotificationTracker resetNotifications;
        private DateTime nextResetNotice = DateTime.MinValue;
        private string lastResetNoticeTitle = "", lastResetNoticeKind = "";
        private double lastResetNoticeAt;

        private void UpdateResetRadar()
        {
            resetRadar = ResetRadarDisplay.Read(data, EpochMs());
            radarMotion.Update(resetRadar, MotionMs);
            if (!configuration.CornerMode || !configuration.ResetAlerts || DateTime.UtcNow < nextResetNotice) return;
            if (resetNotifications == null) resetNotifications = new ResetNotificationTracker(Json.Read(Path.Combine(runtime, "reset-notifications.json")));
            var notice = resetNotifications.Read(data, EpochMs());
            if (resetNotifications.Changed)
                try { Json.Write(Path.Combine(runtime, "reset-notifications.json"), resetNotifications.Saved); } catch (IOException) { }
            if (notice == null) return;
            radarMotion.Announce(notice, MotionMs);
            nextResetNotice = DateTime.UtcNow.AddSeconds(20);
            tray.ShowBalloonTip(12000, notice.Title, notice.Message, ToolTipIcon.Info);
            lastResetNoticeTitle = notice.Title; lastResetNoticeKind = notice.Kind; lastResetNoticeAt = EpochMs();
            if (notice.Kind == "completed") RequestRefresh();
        }
    }
}

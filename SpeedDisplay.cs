using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexTokenHud
{
    internal sealed class SpeedDisplay
    {
        internal string State = "unavailable", Value = "—", Mode = "等待采集", Phase = "待机";
        internal double? Rate, LiveRate, Recent, Peak, Cache, LastRate;
        internal double Share, LastResponseAt, ConfirmedOutput;
        internal bool Estimated, Ready;
        internal readonly List<double> History = new List<double>();
        internal readonly List<bool> Estimates = new List<bool>();
        internal readonly List<double> HistoryTimes = new List<double>();
        internal static bool Valid(double? value)
        { return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value >= 0; }
        private static double? Numeric(Dictionary<string, object> data, string key)
        {
            var value = Json.Get(data, key);
            if (value is bool || value is string) return null;
            var result = Json.Number(data, key);
            return Valid(result) ? result : null;
        }
        internal static string Format(double? value)
        {
            if (!Valid(value)) return "—";
            return value.Value >= 10000 ? (value.Value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" :
                value.Value.ToString(value.Value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
        }
        internal static string Tokens(double value)
        { return value >= 1000000 ? (value / 1000000).ToString("0.##", CultureInfo.InvariantCulture) + "M" :
            value >= 1000 ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : value.ToString("0", CultureInfo.InvariantCulture); }

        internal static SpeedDisplay Read(Dictionary<string, object> data, string selectedTitle, double now)
        {
            var result = new SpeedDisplay();
            double? heartbeat = Numeric(data, "updated_at_ms");
            if (Json.Text(data, "binding") != "ok" || Json.Text(data, "title") != selectedTitle ||
                !Valid(heartbeat) || heartbeat.Value > now + 30000 || now - heartbeat.Value > 6000 ||
                Json.Text(Json.Object(data, "source"), "state") != "ok") return result;
            var metrics = Json.Object(data, "metrics");
            var range = Json.Object(data, "range");
            if (range != null && Json.Text(range, "thread_id") == Json.Text(data, "thread_id"))
            {
                result.State = "range"; result.Mode = "区间均速";
                result.Rate = Numeric(range, "rate"); result.Recent = result.Rate;
                result.Cache = Numeric(range, "cache_percent"); result.ConfirmedOutput = Numeric(range, "output_tokens") ?? 0;
                result.Ready = Valid(result.Rate); result.Value = Format(result.Rate);
                return result;
            }
            var frame = Json.Object(metrics, "live_speed");
            if (frame == null)
            {
                // Compatibility with old collectors: qualify the historical number explicitly.
                var last = Json.Object(metrics, "last");
                var at = Numeric(last, "measured_at_ms");
                if (Valid(at) && now - at.Value >= -30000 && now - at.Value <= 900000 &&
                    (Json.Text(last, "model") == Json.Text(metrics, "model") || Json.Text(last, "model") == ""))
                {
                    result.Rate = Numeric(last, "rate"); result.Mode = "最近实测"; result.State = "last";
                    result.Ready = Valid(result.Rate); result.Value = Format(result.Rate);
                    result.LastRate = result.Rate; result.LastResponseAt = at.Value;
                    result.Phase = Json.Text(metrics, "stage") == "generating" ? "生成中" :
                        Json.Text(metrics, "stage") == "tools" ? "工具中" : "待机";
                }
                return result;
            }
            var updated = Numeric(frame, "updated_at_ms");
            if (!Valid(updated) || now - updated.Value > 3000 || updated.Value > now + 30000) return result;
            result.State = Json.Text(frame, "state");
            result.LiveRate = Numeric(frame, "rate");
            result.Recent = Numeric(frame, "steady_rate") ?? Numeric(frame, "recent_rate");
            result.Peak = Numeric(frame, "peak_rate"); result.LastRate = Numeric(frame, "last_response_rate");
            result.LastResponseAt = Numeric(frame, "last_response_at_ms") ?? 0;
            result.Cache = Numeric(frame, "cache_percent");
            result.ConfirmedOutput = Numeric(frame, "confirmed_turn_output") ?? 0;
            result.Share = Math.Max(0, Math.Min(1, Numeric(frame, "estimated_share") ?? 0));
            object rawRate = Json.Get(frame, "rate");
            if (rawRate != null && !Valid(result.LiveRate)) return new SpeedDisplay();
            bool usable = result.State != "stale" && result.State != "unknown" && result.State != "warming";
            string stage = Json.Text(metrics, "stage");
            object active = Json.Get(metrics, "turn_active");
            result.Phase = active is bool && !(bool)active ? "待机" :
                stage == "generating" ? "生成中" : stage == "tools" ? "工具中" : "待机";
            if (usable && result.Phase == "生成中" && Valid(result.LiveRate) && result.LiveRate.Value > 0 &&
                (result.State == "measured" || result.State == "estimating"))
            {
                result.Rate = result.LiveRate;
                result.Estimated = result.Share > .0001;
                result.Mode = result.Estimated ? "实时估算" : "实测窗口";
                result.Ready = true;
            }
            else if (usable && result.Phase != "生成中" && Valid(result.LastRate))
            {
                // Keep the last real measurement through tool/idle pauses, qualified
                // explicitly. A drain of the activity window is not a new speed of zero.
                result.Rate = result.LastRate;
                result.Mode = result.Phase == "工具中" ? "工具中·保留" : "上次实测";
                result.Ready = true;
            }
            else result.Mode = result.State == "warming" ? "建立基线" : result.State == "tools" ? "工具执行中" :
                result.State == "idle" ? "等待下一轮" : "等待计时";
            result.Value = result.Ready ? Format(result.Rate) : "—";
            foreach (var point in Json.List(frame, "history").TakeLastCompat(60))
            {
                var measured = Numeric(point, "measured"); var estimated = Numeric(point, "estimated");
                result.History.Add((Valid(measured) ? measured.Value : 0) + (Valid(estimated) ? estimated.Value : 0));
                result.Estimates.Add(Valid(estimated) && estimated.Value > 0);
                result.HistoryTimes.Add(Numeric(point, "at_ms") ?? 0);
            }
            return result;
        }
    }
}

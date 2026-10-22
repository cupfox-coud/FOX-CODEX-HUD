using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodexTokenHud
{
    // Activity is deliberately independent of token samples and confirmed statistic ranges.
    internal sealed class ActivityDisplay
    {
        internal string State = "unknown";
        internal string Title = "等待活动";
        internal string Detail = "等待本地日志";
        internal string ElapsedText = "--:--";
        internal int Severity;
        internal bool Active;
        internal double? EventAgeMs;
        internal double WaitLevel;
        internal string AverageText = "--:--";
        internal string AverageSampleText = "完成后更新";
        internal int CompletedCount;
        internal double? AverageMs;
        internal readonly List<double> ResponseMs = new List<double>();
        internal double LastResponseAt;
        internal string SessionId = "";

        private static bool ValidTime(double? value, double nowMs)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
                && value.Value > 0 && value.Value <= nowMs + 30000;
        }

        internal static string Elapsed(double ageMs)
        {
            if (double.IsNaN(ageMs) || double.IsInfinity(ageMs)) return "--:--";
            long seconds = (long)Math.Min(long.MaxValue / 1000.0, Math.Max(0, ageMs / 1000));
            if (seconds < 3600)
                return (seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                    (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
            return (seconds / 3600).ToString("00", CultureInfo.InvariantCulture) + ":" +
                ((seconds / 60) % 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        internal static ActivityDisplay Read(Dictionary<string, object> data, string selectedTitle, double nowMs)
        {
            var result = new ActivityDisplay();
            bool bound = Json.Text(data, "binding") == "ok" && Json.Text(data, "title") == selectedTitle;
            if (!bound)
            {
                result.Title = "等待绑定";
                result.Detail = "尚未识别活动对话";
                return result;
            }

            double? heartbeat = Json.Number(data, "updated_at_ms");
            if (!ValidTime(heartbeat, nowMs) || nowMs - heartbeat.Value > 6000)
            {
                result.Title = "采集暂停";
                result.Detail = "等待采集器重新连接";
                return result;
            }

            string source = Json.Text(Json.Object(data, "source"), "state");
            if (source != "ok")
            {
                result.Title = source == "catching_up" ? "同步活动中" : source == "unavailable" ? "日志暂不可读" : "等待活动";
                result.Detail = "活动状态待确认";
                return result;
            }

            var metrics = Json.Object(data, "metrics");
            var processing = Json.Object(metrics, "response_duration");
            double? mean = Json.Number(processing, "mean_ms");
            double? count = Json.Number(processing, "count");
            if (mean.HasValue && !double.IsNaN(mean.Value) && !double.IsInfinity(mean.Value) && mean.Value > 0 &&
                count.HasValue && count.Value >= 1 && count.Value <= 20 && count.Value == Math.Floor(count.Value))
            {
                result.AverageMs = mean;
                result.CompletedCount = (int)count.Value;
                result.AverageText = mean.Value < 3600000 ? (mean.Value / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s" : Elapsed(mean.Value);
                result.AverageSampleText = count.Value == 1 ? "仅 1 次 · 待积累" : "本会话 · " + count.Value.ToString("0") + " 次";
                result.SessionId = Json.Text(data, "thread_id");
                // Draw exactly the same completed responses as the numeric mean.
                // Older collectors may have a mean but no history: never invent a line.
                var samples = new List<Dictionary<string, object>>(Json.List(processing, "samples"));
                bool valid = samples.Count == result.CompletedCount;
                double sum = 0, previous = 0;
                foreach (var sample in samples)
                {
                    double? ms = Json.Number(sample, "duration_ms");
                    double? at = Json.Number(sample, "completed_at_ms");
                    if (!ms.HasValue || double.IsNaN(ms.Value) || double.IsInfinity(ms.Value) || ms.Value <= 0 ||
                        !ValidTime(at, nowMs) || at.Value < previous || ms.Value > at.Value) { valid = false; break; }
                    sum += ms.Value; previous = at.Value;
                    result.ResponseMs.Add(ms.Value);
                }
                if (!valid || Math.Abs(sum / result.CompletedCount - mean.Value) > Math.Max(1, mean.Value * 0.000001))
                    result.ResponseMs.Clear();
                else result.LastResponseAt = previous;
            }
            double? eventAt = Json.Number(metrics, "last_event_at_ms");
            if (ValidTime(eventAt, nowMs))
            {
                result.EventAgeMs = Math.Max(0, nowMs - eventAt.Value);
                result.ElapsedText = Elapsed(result.EventAgeMs.Value);
            }

            object activeValue = Json.Get(metrics, "turn_active");
            bool? active = activeValue is bool ? (bool?)activeValue : null;
            if (active == false)
            {
                result.State = "idle";
                result.Title = "已完成";
                result.Detail = result.EventAgeMs.HasValue ? "最近活动 · " + MetricDisplay.Age(result.EventAgeMs.Value) : "等待下一轮任务";
                return result;
            }
            if (active != true || !result.EventAgeMs.HasValue || result.EventAgeMs.Value >= 1800000)
            {
                result.Title = "任务状态待确认";
                result.Detail = active == true && result.EventAgeMs.HasValue ? "较久未见新日志，无法确认仍在运行" : "等待明确的任务活动";
                return result;
            }

            result.Active = true;
            bool tools = Json.Text(metrics, "stage") == "tools";
            result.State = tools ? "tools" : "generating";
            double seconds = result.EventAgeMs.Value / 1000;
            double first = tools ? 120 : 30;
            double second = tools ? 300 : 60;
            double third = tools ? 600 : 120;
            result.WaitLevel = seconds <= first ? seconds / first : seconds <= second ?
                1 + (seconds - first) / (second - first) : Math.Min(3, 2 + (seconds - second) / (third - second));
            result.Severity = seconds >= third ? 3 : seconds >= second ? 2 : seconds >= first ? 1 : 0;
            result.Title = tools ? (result.Severity == 0 ? "工具执行中" : "工具仍在等待") :
                result.Severity == 0 ? "正在生成" : result.Severity == 1 ? "等待更新" :
                result.Severity == 2 ? "等待较久" : "较久无新活动";
            result.Detail = tools ? "工具任务可能需要较长时间" : result.Severity == 0 ? "本轮进行中" : "暂无新的日志活动";

            // A node probe is evidence about that route, not proof of the cause of a model delay.
            var network = Json.Object(data, "network_latency");
            foreach (var node in Json.List(network, "nodes"))
            {
                if (Json.Text(node, "id") != "ai") continue;
                double? measuredAt = Json.Number(node, "measured_at_ms");
                if (!ValidTime(measuredAt, nowMs) || nowMs - measuredAt.Value > 75000) break;
                string state = Json.Text(node, "state");
                double? delay = Json.Number(node, "delay_ms");
                bool validDelay = delay.HasValue && !double.IsNaN(delay.Value) && !double.IsInfinity(delay.Value) && delay.Value > 0;
                if (result.Severity >= 1 && state == "timeout")
                    result.Detail = "AI 节点探测超时，可能影响响应";
                else if (result.Severity >= 1 && state == "ok" && validDelay && delay.Value >= 800)
                    result.Detail = "AI 节点偏慢，可能影响响应";
                else if (state == "ok" && validDelay && delay.Value < 800)
                    result.Detail = tools ? "节点探测正常 · 工具仍在执行" : result.Severity == 0 ? "节点探测正常 · 本轮进行中" : "节点探测正常 · 持续等待";
                break;
            }
            return result;
        }
    }
}

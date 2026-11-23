using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace GravityBox.App.Core;

public sealed record ParsedQuotaBar(
    string Title,
    double Percentage,
    string RemainingText,
    string ResetText,
    bool Stale);

public static class QuotaFormatting
{
    public static string FormatCountdown(string? resetTimeOrText)
    {
        if (string.IsNullOrWhiteSpace(resetTimeOrText))
        {
            return string.Empty;
        }

        string trimmed = resetTimeOrText.Trim();
        if ((trimmed.Contains('-') || trimmed.Contains('T'))
            && DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            TimeSpan diff = dto - DateTimeOffset.UtcNow;
            if (diff <= TimeSpan.Zero)
            {
                return "重置时间已过";
            }
            if (diff.TotalSeconds <= 60)
            {
                return "即将重置";
            }

            int totalMin = (int)Math.Floor(diff.TotalMinutes);
            int days = totalMin / (24 * 60);
            int hours = (totalMin % (24 * 60)) / 60;
            int mins = totalMin % 60;
            if (days > 0) return $"{days}天 {hours}小时后重置";
            if (hours > 0) return $"{hours}小时 {mins}分后重置";
            return $"{mins}分钟后重置";
        }

        return trimmed;
    }

    public static string FormatUpdatedAt(long unixTimestamp)
    {
        if (unixTimestamp <= 0)
        {
            return string.Empty;
        }
        long ms = unixTimestamp < 100_000_000_000L ? unixTimestamp * 1000L : unixTimestamp;
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var diff = DateTimeOffset.Now - dt;
        if (diff.TotalSeconds < 60 && diff.TotalSeconds >= 0)
        {
            return "刚刚";
        }
        return dt.Date == DateTime.Today
            ? dt.ToString("HH:mm", CultureInfo.InvariantCulture)
            : dt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes >= 1_000_000_000L) return $"{bytes / 1_000_000_000.0:0.00} GB";
        if (bytes >= 1_000_000L) return $"{bytes / 1_000_000.0:0.0} MB";
        if (bytes >= 1_000L) return $"{Math.Round(bytes / 1_000.0):0} KB";
        return $"{bytes} B";
    }

    public static string FormatCompactCount(long value)
    {
        if (value >= 100_000_000L) return $"{value / 100_000_000.0:0.00} 亿";
        if (value >= 10_000L) return $"{value / 10_000.0:0.0} 万";
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static int? ParseHourBucket(JsonObject obj)
    {
        var hourNode = obj["hour"];
        if (hourNode == null) return null;

        long numeric = hourNode.AsInt64(-1);
        if (numeric is >= 0 and <= 23)
        {
            return (int)numeric;
        }

        string? raw = hourNode.AsString()?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int direct) && direct is >= 0 and <= 23)
        {
            return direct;
        }

        // Matches "2026-11-23 14:00" or "2026-11-23T14:00" or "14:00"
        int spaceOrT = Math.Max(raw.LastIndexOf(' '), raw.LastIndexOf('T'));
        string timePart = spaceOrT >= 0 ? raw[(spaceOrT + 1)..] : raw;
        int colon = timePart.IndexOf(':');
        string hrPart = colon > 0 ? timePart[..colon] : timePart;
        if (int.TryParse(hrPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 0 and <= 23)
        {
            return parsed;
        }
        return null;
    }

    public static List<GravityBox.App.Controls.TrendPoint> BuildHourlyTrendPoints(
        JsonArray? hourlyArray,
        bool padFull24Hours = true,
        bool useRequestMetric = false)
    {
        var pts = new List<GravityBox.App.Controls.TrendPoint>();
        if (hourlyArray == null || hourlyArray.Count == 0)
        {
            return pts;
        }

        var byHour = new Dictionary<int, (double Val, long Reqs, long Tokens)>();
        bool allParsedHours = true;

        foreach (var hNode in hourlyArray)
        {
            var obj = hNode?.AsObject();
            if (obj == null) continue;
            long tok = obj["total_tokens"].AsInt64(obj["totalTokens"].AsInt64(obj["tokens"].AsInt64(0)));
            long reqs = obj["request_count"].AsInt64(obj["requestCount"].AsInt64(obj["requests"].AsInt64(0)));
            double val = useRequestMetric ? reqs : tok;
            int? hr = ParseHourBucket(obj);
            if (hr.HasValue)
            {
                if (byHour.TryGetValue(hr.Value, out var existing))
                {
                    byHour[hr.Value] = (existing.Val + val, existing.Reqs + reqs, existing.Tokens + tok);
                }
                else
                {
                    byHour[hr.Value] = (val, reqs, tok);
                }
            }
            else
            {
                allParsedHours = false;
                string label = obj["label"].AsString() ?? obj["hour"].AsString() ?? string.Empty;
                string tip = useRequestMetric
                    ? $"{label} · {reqs} 次请求"
                    : $"{label} · {FormatCompactCount(tok)} Token ({reqs} 次)";
                pts.Add(new GravityBox.App.Controls.TrendPoint(label, val, tip));
            }
        }

        if (allParsedHours && byHour.Count > 0)
        {
            pts.Clear();
            int maxHour = Math.Max(DateTime.Now.Hour, byHour.Keys.Max());
            int endHour = padFull24Hours ? 23 : maxHour;
            int startHour = 0;
            for (int h = startHour; h <= endHour; h++)
            {
                string label = $"{h:00}时";
                byHour.TryGetValue(h, out var bucket);
                string tip = useRequestMetric
                    ? $"{label} · {bucket.Reqs} 次请求"
                    : $"{label} · {FormatCompactCount(bucket.Tokens)} Token ({bucket.Reqs} 次)";
                pts.Add(new GravityBox.App.Controls.TrendPoint(label, bucket.Val, tip));
            }
        }

        return pts;
    }

    public static List<ParsedQuotaBar> ExtractQuotaBars(JsonObject? source)
    {
        var bars = new List<ParsedQuotaBar>();
        if (source == null)
        {
            return bars;
        }

        // 1. Real IPC contract: familyWindows (gemini / claude / argon / carbon)
        var fw = source["familyWindows"]?.AsObject()
            ?? source["quota"]?["familyWindows"]?.AsObject();
        if (fw != null)
        {
            AppendFamilyWindow(bars, fw["gemini"]?.AsObject(), "Gemini");
            AppendFamilyWindow(bars, fw["claude"]?.AsObject(), "Claude/GPT");
            AppendFamilyWindow(bars, fw["argon"]?.AsObject(), "Gemini 4 Argon");
            AppendFamilyWindow(bars, fw["carbon"]?.AsObject(), "Carbon");
        }

        // 2. Real IPC standaloneModels (only add non-duplicate entries)
        var standalone = source["standaloneModels"]?.AsArray();
        if (standalone != null)
        {
            foreach (var sNode in standalone)
            {
                var s = sNode?.AsObject();
                if (s == null) continue;
                string avail = s["availability"].AsString() ?? string.Empty;
                if (string.Equals(avail, "disabled", StringComparison.OrdinalIgnoreCase)) continue;
                double? rem = s["remaining"].AsNullableDouble();
                if (!rem.HasValue) continue;

                string name = s["displayName"].AsString() ?? s["key"].AsString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (bars.Exists(b => b.Title.StartsWith(name, StringComparison.OrdinalIgnoreCase))) continue;

                double pct = NormalizeRemainingToPercent(rem.Value);
                string period = FormatWindowPeriodLabel(s["period"].AsString());
                string title = string.IsNullOrEmpty(period) ? name : $"{name} · {period}";
                bool stale = string.Equals(s["quotaState"].AsString(), "stale", StringComparison.OrdinalIgnoreCase);
                string reset = s["resetCountdown"].AsString()
                    ?? FormatCountdown(s["resetTime"].AsString());
                bars.Add(new ParsedQuotaBar(title, pct, $"{pct:0}%", stale ? "数据已过期" : reset, stale));
            }
        }

        // 3. Fallback: quotaGroups or legacy/test quota.groups (normalized buckets)
        if (bars.Count == 0)
        {
            var groups = source["quotaGroups"]?.AsArray()
                ?? source["quota"]?["groups"]?.AsArray()
                ?? source["groups"]?.AsArray();
            if (groups != null)
            {
                foreach (var gNode in groups)
                {
                    var g = gNode?.AsObject();
                    if (g == null) continue;
                    string gName = g["displayName"].AsString()
                        ?? g["display_name"].AsString()
                        ?? g["title"].AsString()
                        ?? "Quota";
                    var buckets = g["buckets"]?.AsArray();
                    if (buckets != null && buckets.Count > 0)
                    {
                        foreach (var bNode in buckets)
                        {
                            var b = bNode?.AsObject();
                            if (b == null) continue;
                            double? frac = b["remainingFraction"].AsNullableDouble()
                                ?? b["remaining_fraction"].AsNullableDouble();
                            double? pctRaw = b["percentage"].AsNullableDouble();
                            if (!frac.HasValue && !pctRaw.HasValue) continue;
                            double pct = frac.HasValue
                                ? NormalizeRemainingToPercent(frac.Value)
                                : Math.Clamp(pctRaw!.Value, 0.0, 100.0);
                            string win = FormatWindowPeriodLabel(b["window"].AsString());
                            string title = string.IsNullOrEmpty(win) ? gName : $"{gName} ({win})";
                            bool stale = b["stale"].AsBool();
                            string reset = b["resetCountdown"].AsString()
                                ?? FormatCountdown(b["resetTime"].AsString() ?? b["reset_time"].AsString());
                            bars.Add(new ParsedQuotaBar(title, pct, $"{pct:0}%", stale ? "数据已过期" : reset, stale));
                        }
                    }
                    else
                    {
                        double? rem = g["remaining"].AsNullableDouble() ?? g["percentage"].AsNullableDouble();
                        if (!rem.HasValue) continue;
                        double pct = NormalizeRemainingToPercent(rem.Value);
                        bool stale = g["stale"].AsBool();
                        string reset = g["resetCountdown"].AsString() ?? FormatCountdown(g["resetTime"].AsString());
                        bars.Add(new ParsedQuotaBar(gName, pct, $"{pct:0}%", stale ? "数据已过期" : reset, stale));
                    }
                }
            }
        }

        // 4. Last-resort summary fallback if nothing detailed is present yet
        if (bars.Count == 0)
        {
            double? fiveHour = source["fiveHourRemaining"].AsNullableDouble()
                ?? source["five_hour_quota"]?["remaining_percentage"].AsNullableDouble();
            double? weekly = source["weeklyRemaining"].AsNullableDouble()
                ?? source["weekly_quota"]?["remaining_percentage"].AsNullableDouble();
            string resetText = source["resetCountdown"].AsString()
                ?? source["five_hour_quota"]?["reset_text"].AsString()
                ?? string.Empty;

            if (fiveHour.HasValue)
            {
                double pct = NormalizeRemainingToPercent(fiveHour.Value);
                bars.Add(new ParsedQuotaBar("5 小时配额", pct, $"{pct:0}%", FormatCountdown(resetText), false));
            }
            if (weekly.HasValue)
            {
                double pct = NormalizeRemainingToPercent(weekly.Value);
                bars.Add(new ParsedQuotaBar("每周配额", pct, $"{pct:0}%", string.Empty, false));
            }
        }

        return bars;
    }

    public static double NormalizeRemainingToPercent(double raw)
    {
        double pct = raw <= 1.0 ? raw * 100.0 : raw;
        return Math.Clamp(pct, 0.0, 100.0);
    }

    private static void AppendFamilyWindow(List<ParsedQuotaBar> target, JsonObject? familyNode, string familyTitle)
    {
        if (familyNode == null) return;
        AppendSingleWindow(target, familyNode["fiveHour"]?.AsObject(), $"{familyTitle} · 5 小时");
        AppendSingleWindow(target, familyNode["weekly"]?.AsObject(), $"{familyTitle} · 每周");
    }

    private static void AppendSingleWindow(List<ParsedQuotaBar> target, JsonObject? win, string title)
    {
        if (win == null) return;
        bool known = win["known"].AsBool();
        double? rem = win["remaining"].AsNullableDouble();
        if (!known || !rem.HasValue) return;

        double pct = NormalizeRemainingToPercent(rem.Value);
        bool stale = win["stale"].AsBool();
        string reset = stale
            ? "数据已过期"
            : FormatCountdown(win["resetTime"].AsString());
        target.Add(new ParsedQuotaBar(title, pct, $"{pct:0}%", reset, stale));
    }

    private static string FormatWindowPeriodLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string trimmed = raw.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "5h" => "5 小时",
            "weekly" => "每周",
            "daily" => "每日",
            "unspecified" => string.Empty,
            _ => trimmed,
        };
    }
}

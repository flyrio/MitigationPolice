// 本文件负责将单次受伤事件格式化为可复制/可发送的小队文本。
using System;
using System.Collections.Generic;
using System.Linq;
using MitigationPolice.Models;

namespace MitigationPolice.Chat;

public static class ShareFormatter {
    public const string SeparatorLine = "------------------------------------";

    public static bool IsSeparatorLine(string? line) {
        return line != null && string.Equals(line, SeparatorLine, StringComparison.Ordinal);
    }

    public static string BuildCopyText(DamageEventRecord record) {
        return Build(record, overwrites: null, maxBytes: int.MaxValue);
    }

    public static string BuildPartyMessage(DamageEventRecord record, int maxBytes) {
        var text = Build(record, overwrites: null, maxBytes);
        return text;
    }

    public static string BuildCopyText(DamageEventRecord record, IReadOnlyList<MitigationOverwrite> overwrites) {
        return Build(record, overwrites, maxBytes: int.MaxValue);
    }

    public static string BuildPartyMessage(DamageEventRecord record, IReadOnlyList<MitigationOverwrite> overwrites, int maxBytes) {
        var text = Build(record, overwrites, maxBytes);
        return text;
    }

    public static IReadOnlyList<string> BuildCopyLines(DamageEventRecord record, IReadOnlyList<MitigationOverwrite>? overwrites) {
        return BuildShareLines(record, overwrites, maxBytesPerLine: int.MaxValue);
    }

    public static string BuildCopyTextMultiline(DamageEventRecord record, IReadOnlyList<MitigationOverwrite>? overwrites) {
        return string.Join("\n", BuildCopyLines(record, overwrites));
    }

    public static IReadOnlyList<string> BuildPartyLines(DamageEventRecord record, IReadOnlyList<MitigationOverwrite>? overwrites, int maxBytesPerLine) {
        var maxBytes = Math.Clamp(maxBytesPerLine, 120, 480);
        var body = BuildShareLines(record, overwrites, maxBytes);

        var lines = new List<string>(body.Count + 2) {
            FitLine(SeparatorLine, maxBytes),
        };
        lines.AddRange(body);
        lines.Add(FitLine(SeparatorLine, maxBytes));
        return lines;
    }

    private static string Build(DamageEventRecord record, IReadOnlyList<MitigationOverwrite>? overwrites, int maxBytes) {
        var source = string.IsNullOrWhiteSpace(record.SourceName) ? "未知来源" : record.SourceName!;
        var action = string.IsNullOrWhiteSpace(record.ActionName)
            ? record.ActionId?.ToString() ?? "未知技能"
            : record.ActionName!;

        var fatalPrefix = record.IsFatal ? "致死 " : string.Empty;
        var header = $"{fatalPrefix}{source}:{action} -> {record.TargetName} 伤害{record.DamageAmount}";

        var missing = FormatMissing(record.MissingMitigations);
        var active = FormatActive(record.ActiveMitigations);
        var overwriteText = FormatOverwrites(overwrites);

        var full = overwriteText == "无"
            ? $"{header} | 已:{active} | 未:{missing}"
            : $"{header} | 已:{active} | 未:{missing} | 顶:{overwriteText}";
        if (Utf8Util.GetByteCount(full) <= maxBytes) {
            return full;
        }

        var compact = $"{header} | 未:{missing}";
        if (Utf8Util.GetByteCount(compact) <= maxBytes) {
            return compact;
        }

        var missingCompact = FormatMissing(record.MissingMitigations, limit: 2);
        compact = $"{header} | 未:{missingCompact}";
        if (Utf8Util.GetByteCount(compact) <= maxBytes) {
            return compact;
        }

        var minimal = $"{header} | 未:{record.MissingMitigations.Count}项";
        if (Utf8Util.GetByteCount(minimal) <= maxBytes) {
            return minimal;
        }

        return Utf8Util.Truncate(minimal, maxBytes);
    }

    private static IReadOnlyList<string> BuildShareLines(DamageEventRecord record, IReadOnlyList<MitigationOverwrite>? overwrites, int maxBytesPerLine) {
        var header = FitLine(BuildHeader(record), maxBytesPerLine);

        var lines = new List<string> { header };
        lines.AddRange(BuildActiveLines(record.ActiveMitigations, maxBytesPerLine));
        lines.AddRange(BuildMissingLines(record.MissingMitigations, maxBytesPerLine));
        lines.AddRange(BuildOverwriteLines(overwrites, maxBytesPerLine));
        return lines;
    }

    private static IReadOnlyList<string> BuildActiveLines(IReadOnlyList<MitigationContribution> list, int maxBytesPerLine) {
        if (list.Count == 0) {
            return new[] { "已:无（命中时无生效减伤）" };
        }

        var lines = new List<string>(list.Count);
        foreach (var x in list.OrderByDescending(m => m.RemainingSeconds)) {
            var remaining = FormatSeconds(x.RemainingSeconds);
            var line = $"已:{x.MitigationName}@{x.CasterName}(已交,命中时剩余{remaining})";
            lines.Add(FitLine(line, maxBytesPerLine));
        }

        return lines;
    }

    private static IReadOnlyList<string> BuildMissingLines(IReadOnlyList<MissingMitigation> list, int maxBytesPerLine) {
        if (list.Count == 0) {
            return new[] { "未:无" };
        }

        var lines = new List<string>(list.Count);
        foreach (var x in list.OrderByDescending(m => m.AvailableForSeconds)) {
            var line = $"未:{x.MitigationName}@{x.OwnerName}({FormatMissingAvailableText(x)})";
            lines.Add(FitLine(line, maxBytesPerLine));
        }

        return lines;
    }

    private static IReadOnlyList<string> BuildOverwriteLines(IReadOnlyList<MitigationOverwrite>? list, int maxBytesPerLine) {
        if (list == null || list.Count == 0) {
            return Array.Empty<string>();
        }

        var lines = new List<string>(list.Count);
        foreach (var item in list.OrderByDescending(x => x.TimestampUtc)) {
            lines.Add(FitLine($"顶:{FormatOverwriteItem(item)}", maxBytesPerLine));
        }

        return lines;
    }

    private static string FormatActive(IReadOnlyList<MitigationContribution> list, int limit = 3) {
        if (list.Count == 0) {
            return "无";
        }

        var parts = list
            .OrderByDescending(x => x.RemainingSeconds)
            .Take(limit)
            .Select(x => $"{x.MitigationName}@{x.CasterName}({FormatSeconds(x.RemainingSeconds)})")
            .ToList();

        if (list.Count > limit) {
            parts.Add($"…+{list.Count - limit}");
        }

        return string.Join(" ", parts);
    }

    private static string FormatMissing(IReadOnlyList<MissingMitigation> list, int limit = 4) {
        if (list.Count == 0) {
            return "无";
        }

        var parts = list
            .OrderByDescending(x => x.AvailableForSeconds)
            .Take(limit)
            .Select(FormatMissingItem)
            .ToList();

        if (list.Count > limit) {
            parts.Add($"…+{list.Count - limit}");
        }

        return string.Join(" ", parts);
    }

    private static string FormatOverwrites(IReadOnlyList<MitigationOverwrite>? list, int limit = 1) {
        if (list == null || list.Count == 0) {
            return "无";
        }

        var parts = list
            .OrderByDescending(x => x.TimestampUtc)
            .Take(limit)
            .Select(FormatOverwriteItem)
            .ToList();

        if (list.Count > limit) {
            parts.Add($"…+{list.Count - limit}");
        }

        return string.Join(" ", parts);
    }

    private static string FormatOverwriteItem(MitigationOverwrite item) {
        var remaining = FormatSeconds(item.OldRemainingSeconds);
        var isRefresh = item.OldCasterId == item.NewCasterId &&
                        string.Equals(item.OldMitigationId, item.NewMitigationId, StringComparison.OrdinalIgnoreCase);

        if (isRefresh) {
            return $"{item.NewMitigationName}@{item.NewCasterName}刷新(旧剩{remaining})";
        }

        return $"{item.NewMitigationName}@{item.NewCasterName}顶{item.OldMitigationName}@{item.OldCasterName}(旧剩{remaining})";
    }

    private static string FormatMissingAvailableText(MissingMitigation item) {
        var seconds = FormatSeconds(item.AvailableForSeconds);
        return item.NeverUsedSinceDutyStart
            ? $"开场{seconds}未交"
            : $"转好{seconds}未交";
    }

    private static string FormatMissingItem(MissingMitigation item) {
        return $"{item.MitigationName}@{item.OwnerName}({FormatMissingAvailableText(item)})";
    }

    private static string BuildHeader(DamageEventRecord record) {
        var source = string.IsNullOrWhiteSpace(record.SourceName) ? "未知来源" : record.SourceName!;
        var action = string.IsNullOrWhiteSpace(record.ActionName)
            ? record.ActionId?.ToString() ?? "未知技能"
            : record.ActionName!;

        var fatalPrefix = record.IsFatal ? "致死 " : string.Empty;
        return $"{fatalPrefix}{source}:{action} -> {record.TargetName} 伤害{record.DamageAmount}";
    }

    private static string BuildActiveLine(IReadOnlyList<MitigationContribution> list, int maxBytes) {
        if (list.Count == 0) {
            return "已:无";
        }

        for (var limit = 6; limit >= 1; limit = limit == 1 ? 0 : limit / 2) {
            var text = $"已:{FormatActive(list, limit)}";
            if (Utf8Util.GetByteCount(text) <= maxBytes) {
                return text;
            }
        }

        return $"已:{list.Count}项";
    }

    private static string BuildMissingLine(IReadOnlyList<MissingMitigation> list, int maxBytes) {
        if (list.Count == 0) {
            return "未:无";
        }

        for (var limit = 6; limit >= 1; limit = limit == 1 ? 0 : limit / 2) {
            var text = $"未:{FormatMissing(list, limit)}";
            if (Utf8Util.GetByteCount(text) <= maxBytes) {
                return text;
            }
        }

        return $"未:{list.Count}项";
    }

    private static string BuildOverwriteLine(IReadOnlyList<MitigationOverwrite>? list, int maxBytes) {
        if (list == null || list.Count == 0) {
            return string.Empty;
        }

        for (var limit = 2; limit >= 1; limit--) {
            var text = $"顶:{FormatOverwrites(list, limit)}";
            if (Utf8Util.GetByteCount(text) <= maxBytes) {
                return text;
            }
        }

        return $"顶:{list.Count}次";
    }

    private static string FitLine(string text, int maxBytes) {
        if (Utf8Util.GetByteCount(text) <= maxBytes) {
            return text;
        }

        return Utf8Util.Truncate(text, maxBytes);
    }

    private static string FormatSeconds(float seconds) {
        if (seconds < 0) {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalMinutes >= 1) {
            return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
        }

        return $"{(int)Math.Round(ts.TotalSeconds)}s";
    }
}

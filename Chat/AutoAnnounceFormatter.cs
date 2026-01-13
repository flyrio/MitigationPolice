// 本文件负责“自动通报”消息的格式化（顶减伤等）。
using System;
using System.Collections.Generic;
using System.Linq;
using MitigationPolice.Models;

namespace MitigationPolice.Chat;

public static class AutoAnnounceFormatter {
    public static string BuildOverwriteAnnouncementMessage(IReadOnlyList<MitigationOverwrite> newlyDetectedOverwrites) {
        if (newlyDetectedOverwrites.Count == 0) {
            return string.Empty;
        }

        var grouped = newlyDetectedOverwrites
            .Where(o => !IsRefreshOverwrite(o))
            .GroupBy(o => new OverwriteAnnounceKey(o.ConflictGroupId, o.OldMitigationId, o.OldCasterId, o.NewMitigationId, o.NewCasterId))
            .Select(g => new {
                Representative = g.OrderByDescending(x => x.OldRemainingSeconds).First(),
                Count = g.Select(x => x.AppliedActorId).Distinct().Count(),
            })
            .OrderByDescending(x => x.Representative.OldRemainingSeconds)
            .ThenByDescending(x => x.Count)
            .ToList();

        if (grouped.Count == 0) {
            return string.Empty;
        }

        const int maxParts = 2;
        var parts = grouped
            .Take(maxParts)
            .Select(x => FormatOverwriteAnnouncementPart(x.Representative, x.Count))
            .ToList();

        var extra = grouped.Count - parts.Count;
        var message = extra > 0
            ? $"顶减伤：{string.Join(" ", parts)} …+{extra}"
            : $"顶减伤：{string.Join(" ", parts)}";

        return Utf8Util.Truncate(message, 480);
    }

    private static bool IsRefreshOverwrite(MitigationOverwrite overwrite) {
        return overwrite.OldCasterId == overwrite.NewCasterId &&
               string.Equals(overwrite.OldMitigationId, overwrite.NewMitigationId, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatOverwriteAnnouncementPart(MitigationOverwrite overwrite, int affectedTargetCount) {
        var newCaster = string.IsNullOrWhiteSpace(overwrite.NewCasterName) ? "?" : overwrite.NewCasterName;
        var oldCaster = string.IsNullOrWhiteSpace(overwrite.OldCasterName) ? "?" : overwrite.OldCasterName;
        var remaining = FormatSeconds(overwrite.OldRemainingSeconds);

        var part = $"{overwrite.NewMitigationName}@{newCaster}顶{overwrite.OldMitigationName}@{oldCaster}(旧剩{remaining})";
        if (affectedTargetCount > 1) {
            part += $"x{affectedTargetCount}";
        }

        return part;
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

    private readonly record struct OverwriteAnnounceKey(string ConflictGroupId, string OldMitigationId, uint OldCasterId, string NewMitigationId, uint NewCasterId);
}


// 本文件定义互斥减伤的“顶掉/覆盖”记录，用于在 UI 与分享文本中展示。
using System;

namespace MitigationPolice.Models;

public sealed class MitigationOverwrite {
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public uint AppliedActorId { get; init; }
    public string AppliedActorName { get; init; } = string.Empty;

    public string ConflictGroupId { get; init; } = string.Empty;

    public string OldMitigationId { get; init; } = string.Empty;
    public string OldMitigationName { get; init; } = string.Empty;
    public uint OldCasterId { get; init; }
    public string OldCasterName { get; init; } = string.Empty;
    public float OldRemainingSeconds { get; init; }

    public string NewMitigationId { get; init; } = string.Empty;
    public string NewMitigationName { get; init; } = string.Empty;
    public uint NewCasterId { get; init; }
    public string NewCasterName { get; init; } = string.Empty;
    public float NewDurationSeconds { get; init; }
}


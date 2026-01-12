// 本文件定义一次受伤事件的持久化记录，用于复盘、统计与分享。
using System;
using System.Collections.Generic;

namespace MitigationPolice.Models;

public sealed class DamageEventRecord {
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public uint? ContentId { get; init; }
    public string? ContentName { get; init; }

    public uint TargetId { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public JobIds TargetJob { get; init; } = JobIds.OTHER;

    public uint? SourceId { get; init; }
    public string? SourceName { get; init; }

    public uint? ActionId { get; init; }
    public string? ActionName { get; init; }

    public uint DamageAmount { get; init; }
    public string? DamageType { get; init; }

    public bool IsFatal { get; set; }

    public List<MitigationContribution> ActiveMitigations { get; init; } = new();
    public List<MissingMitigation> MissingMitigations { get; init; } = new();
}

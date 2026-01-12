// 本文件定义一次“战斗会话”的持久化记录：按进战/脱战分段，用于历史复盘与筛选。
using System;
using System.Collections.Generic;

namespace MitigationPolice.Models;

public sealed class CombatSessionRecord {
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset StartUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndUtc { get; set; }

    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public uint? ContentId { get; init; }
    public string? ContentName { get; init; }

    public List<DamageEventRecord> Events { get; init; } = new();
}


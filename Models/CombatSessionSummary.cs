// 本文件定义战斗会话的轻量摘要，用于 UI 列表展示与选择。
using System;

namespace MitigationPolice.Models;

public readonly record struct CombatSessionSummary(
    Guid Id,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    uint TerritoryId,
    string TerritoryName,
    uint? ContentId,
    string? ContentName,
    int EventCount,
    int FatalCount
);


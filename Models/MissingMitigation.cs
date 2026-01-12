// 本文件定义一次命中中“可用未交”的减伤责任候选信息。
namespace MitigationPolice.Models;

public sealed class MissingMitigation {
    public string MitigationId { get; init; } = string.Empty;
    public string MitigationName { get; init; } = string.Empty;
    public uint IconActionId { get; init; }

    public uint OwnerId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public JobIds OwnerJob { get; init; } = JobIds.OTHER;

    public bool NeverUsedSinceDutyStart { get; init; }
    public float AvailableForSeconds { get; init; }
}


// 本文件定义一次命中中“已生效”的减伤贡献明细。
namespace MitigationPolice.Models;

public sealed class MitigationContribution {
    public string MitigationId { get; init; } = string.Empty;
    public string MitigationName { get; init; } = string.Empty;
    public uint IconActionId { get; init; }

    public uint CasterId { get; init; }
    public string CasterName { get; init; } = string.Empty;

    public float RemainingSeconds { get; init; }
}


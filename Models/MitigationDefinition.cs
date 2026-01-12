// 本文件定义减伤技能的数据模型，支持多触发器（同 CD 的升级/变体技能）。
using System.Collections.Generic;

namespace MitigationPolice.Models;

public sealed class MitigationDefinition {
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public uint IconActionId { get; set; }
    public List<uint> TriggerActionIds { get; set; } = new();

    public float DurationSeconds { get; set; }
    public float CooldownSeconds { get; set; }

    public Dictionary<uint, float> DurationSecondsByActionId { get; set; } = new();
    public Dictionary<uint, float> CooldownSecondsByActionId { get; set; } = new();

    public MitigationCategory Category { get; set; } = MitigationCategory.Party;
    public MitigationApplyTo ApplyTo { get; set; } = MitigationApplyTo.Target;

    public List<JobIds> Jobs { get; set; } = new();

    public bool Enabled { get; set; } = true;
}

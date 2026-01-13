// 本文件定义职业枚举（与 ClassJob RowId 对齐），用于减伤技能归属判定与展示。
namespace MitigationPolice.Models;

public enum JobIds : uint {
    OTHER = 0,

    PLD = 19,
    MNK = 20,
    WAR = 21,
    DRG = 22,
    BRD = 23,
    WHM = 24,
    BLM = 25,
    SMN = 27,
    SCH = 28,
    NIN = 30,
    MCH = 31,
    DRK = 32,
    AST = 33,
    SAM = 34,
    RDM = 35,
    BLU = 36,
    GNB = 37,
    DNC = 38,
    RPR = 39,
    SGE = 40,
    VPR = 41,
    PCT = 42,
}

public static class JobIdsExtensions {
    public static string ToCnName(this JobIds job) {
        return job switch {
            JobIds.PLD => "骑士",
            JobIds.MNK => "武僧",
            JobIds.WAR => "战士",
            JobIds.DRG => "龙骑士",
            JobIds.BRD => "吟游诗人",
            JobIds.WHM => "白魔法师",
            JobIds.BLM => "黑魔法师",
            JobIds.SMN => "召唤师",
            JobIds.SCH => "学者",
            JobIds.NIN => "忍者",
            JobIds.MCH => "机工士",
            JobIds.DRK => "暗黑骑士",
            JobIds.AST => "占星术士",
            JobIds.SAM => "武士",
            JobIds.RDM => "赤魔法师",
            JobIds.BLU => "青魔法师",
            JobIds.GNB => "绝枪战士",
            JobIds.DNC => "舞者",
            JobIds.RPR => "钐镰客",
            JobIds.SGE => "贤者",
            JobIds.VPR => "蝰蛇剑士",
            JobIds.PCT => "绘灵法师",
            _ => "其他",
        };
    }
}

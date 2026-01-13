// 本文件提供“减伤百分比”计算所需的减伤强度映射与叠乘逻辑。
using System;
using System.Collections.Generic;
using MitigationPolice.Models;

namespace MitigationPolice.Mitigations;

public static class MitigationReductionLibrary {
    private enum DamageKind {
        Unknown = 0,
        Physical = 1,
        Magical = 2,
    }

    private readonly record struct ReductionProfile(float PhysicalPercent, float MagicalPercent);

    private static readonly IReadOnlyDictionary<string, ReductionProfile> ProfilesByMitigationId =
        new Dictionary<string, ReductionProfile>(StringComparer.OrdinalIgnoreCase) {
            // 坦克/通用
            ["rampart"] = new ReductionProfile(0.20f, 0.20f),
            ["reprisal"] = new ReductionProfile(0.10f, 0.10f),

            // 目标减伤（敌方减益）
            ["addle"] = new ReductionProfile(0.05f, 0.10f),
            ["feint"] = new ReductionProfile(0.10f, 0.05f),
            ["dismantle"] = new ReductionProfile(0.10f, 0.10f),

            // 远程物理团减
            ["troubadour"] = new ReductionProfile(0.10f, 0.10f),
            ["tactician"] = new ReductionProfile(0.10f, 0.10f),
            ["shield_samba"] = new ReductionProfile(0.10f, 0.10f),

            // 治疗
            ["temperance"] = new ReductionProfile(0.10f, 0.10f),
            ["aquaveil"] = new ReductionProfile(0.15f, 0.15f),
            ["sacred_soil"] = new ReductionProfile(0.10f, 0.10f),
            ["fey_illumination"] = new ReductionProfile(0.00f, 0.05f),
            ["expedient"] = new ReductionProfile(0.10f, 0.10f),
            ["collective_unconscious"] = new ReductionProfile(0.10f, 0.10f),
            ["exaltation"] = new ReductionProfile(0.10f, 0.10f),
            ["kerachole"] = new ReductionProfile(0.10f, 0.10f),
            ["taurochole"] = new ReductionProfile(0.10f, 0.10f),
            ["holos"] = new ReductionProfile(0.10f, 0.10f),

            // 坦克团队魔法减伤
            ["dark_missionary"] = new ReductionProfile(0.00f, 0.10f),
            ["heart_of_light"] = new ReductionProfile(0.00f, 0.10f),

            // 坦克个人减伤
            ["bloodwhetting"] = new ReductionProfile(0.10f, 0.10f),
            ["passage_of_arms"] = new ReductionProfile(0.15f, 0.15f),
        };

    public static float ComputeDamageReductionPercent(IReadOnlyList<MitigationContribution> activeMitigations, string? damageType) {
        if (activeMitigations.Count == 0) {
            return 0;
        }

        var kind = ClassifyDamageKind(damageType);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var multiplier = 1.0;
        foreach (var x in activeMitigations) {
            if (string.IsNullOrWhiteSpace(x.MitigationId) || !unique.Add(x.MitigationId)) {
                continue;
            }

            if (!TryGetReduction(x.MitigationId, kind, out var reduction)) {
                continue;
            }

            if (reduction <= 0) {
                continue;
            }

            reduction = Math.Clamp(reduction, 0f, 1f);
            multiplier *= 1.0 - reduction;
        }

        var combined = 1.0 - multiplier;
        if (combined < 0) {
            return 0;
        }
        if (combined > 1) {
            return 1;
        }

        return (float)combined;
    }

    private static bool TryGetReduction(string mitigationId, DamageKind damageKind, out float reduction) {
        reduction = 0;
        if (!ProfilesByMitigationId.TryGetValue(mitigationId, out var profile)) {
            return false;
        }

        reduction = damageKind switch {
            DamageKind.Magical => profile.MagicalPercent,
            DamageKind.Physical => profile.PhysicalPercent,
            _ => Math.Min(profile.PhysicalPercent, profile.MagicalPercent),
        };

        return true;
    }

    private static DamageKind ClassifyDamageKind(string? damageType) {
        if (string.IsNullOrWhiteSpace(damageType)) {
            return DamageKind.Unknown;
        }

        return damageType switch {
            "魔法" => DamageKind.Magical,
            "吐息" => DamageKind.Magical,
            "斩击" => DamageKind.Physical,
            "穿刺" => DamageKind.Physical,
            "打击" => DamageKind.Physical,
            "射击" => DamageKind.Physical,
            "物理" => DamageKind.Physical,
            _ => DamageKind.Unknown,
        };
    }
}


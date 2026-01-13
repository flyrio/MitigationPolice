// 本文件定义插件配置，并负责默认减伤清单的初始化与持久化。
using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using MitigationPolice.Mitigations;
using MitigationPolice.Models;

namespace MitigationPolice;

[Serializable]
public sealed class Configuration : IPluginConfiguration {
    [NonSerialized]
    private IDalamudPluginInterface pluginInterface = null!;

    public int Version { get; set; } = 9;

    public bool TrackOnlyInInstances { get; set; } = true;

    public bool IncludePersonalMitigations { get; set; } = true;
    public bool IncludePartyMitigations { get; set; } = true;
    public bool IncludeEnemyMitigations { get; set; } = true;

    public bool AssumeReadyAtDutyStart { get; set; } = true;

    public int MinDamageToAnalyzeMissing { get; set; } = 0;

    public int MaxStoredEvents { get; set; } = 20000;
    public int SaveDebounceMilliseconds { get; set; } = 1500;

    public bool AllowSendingToPartyChat { get; set; } = false;
    public bool AutoAnnounceOverwritesToPartyChat { get; set; } = false;
    public bool AutoAnnounceDeathsToPartyChat { get; set; } = false;

    public List<MitigationDefinition> Mitigations { get; set; } = new();

    public static Configuration Get(IDalamudPluginInterface pluginInterface) {
        var config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.pluginInterface = pluginInterface;

        if (config.Mitigations.Count == 0) {
            config.Mitigations = DefaultMitigationLibrary.Build();
            config.Save();
        }

        if (config.Version < 2) {
            config.MigrateDefaultMitigationNamesToCn();
            config.Version = 2;
            config.Save();
        }

        if (config.Version < 3) {
            config.MigrateDefaultMitigationTimingsFromMcp();
            config.Version = 3;
            config.Save();
        }

        if (config.Version < 4) {
            config.MigratePhysRangedPartyMitigationCooldownTo90();
            config.Version = 4;
            config.Save();
        }

        if (config.Version < 5) {
            config.Version = 5;
            config.Save();
        }

        if (config.Version < 6) {
            config.IncludePersonalMitigations = true;
            config.Version = 6;
            config.Save();
        }

        if (config.Version < 7) {
            config.AutoAnnounceOverwritesToPartyChat = false;
            config.Version = 7;
            config.Save();
        }

        if (config.Version < 8) {
            config.AutoAnnounceDeathsToPartyChat = false;
            config.Version = 8;
            config.Save();
        }

        if (config.Version < 9) {
            config.AllowSendingToPartyChat = false;
            config.TryAddDismantleMitigation();
            config.Version = 9;
            config.Save();
        }

        return config;
    }

    private void MigrateDefaultMitigationNamesToCn() {
        var idToOldName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["rampart"] = "Rampart",
            ["reprisal"] = "Reprisal",
            ["addle"] = "Addle",
            ["feint"] = "Feint",
            ["dismantle"] = "Dismantle",
            ["troubadour"] = "Troubadour",
            ["tactician"] = "Tactician",
            ["shield_samba"] = "Shield Samba",
            ["hallowed_ground"] = "Hallowed Ground",
            ["divine_veil"] = "Divine Veil",
            ["passage_of_arms"] = "Passage of Arms",
            ["holmgang"] = "Holmgang",
            ["shake_it_off"] = "Shake It Off",
            ["bloodwhetting"] = "Bloodwhetting / Raw Intuition",
            ["living_dead"] = "Living Dead",
            ["dark_missionary"] = "Dark Missionary",
            ["the_blackest_night"] = "The Blackest Night",
            ["superbolide"] = "Superbolide",
            ["heart_of_light"] = "Heart of Light",
        };

        var idToCnName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["rampart"] = "铁壁",
            ["reprisal"] = "雪仇",
            ["addle"] = "昏乱",
            ["feint"] = "牵制",
            ["dismantle"] = "武装解除",
            ["troubadour"] = "行吟",
            ["tactician"] = "策动",
            ["shield_samba"] = "防守之桑巴",
            ["hallowed_ground"] = "神圣领域",
            ["divine_veil"] = "圣光幕帘",
            ["passage_of_arms"] = "武装戍卫",
            ["holmgang"] = "死斗",
            ["shake_it_off"] = "摆脱",
            ["bloodwhetting"] = "原初的血气/原初的直觉/原初的勇猛",
            ["living_dead"] = "行尸走肉",
            ["dark_missionary"] = "暗黑布道",
            ["the_blackest_night"] = "至黑之夜",
            ["superbolide"] = "超火流星",
            ["heart_of_light"] = "光之心",
        };

        foreach (var def in Mitigations) {
            if (!idToCnName.TryGetValue(def.Id, out var cnName)) {
                continue;
            }

            var hasOld = idToOldName.TryGetValue(def.Id, out var oldName);
            if (string.IsNullOrWhiteSpace(def.Name) ||
                (hasOld && string.Equals(def.Name, oldName, StringComparison.OrdinalIgnoreCase))) {
                def.Name = cnName;
            }
        }
    }

    private void TryAddDismantleMitigation() {
        if (Mitigations.Exists(m => string.Equals(m.Id, "dismantle", StringComparison.OrdinalIgnoreCase))) {
            return;
        }

        if (Mitigations.Exists(m => m.TriggerActionIds != null && m.TriggerActionIds.Contains(2887))) {
            return;
        }

        Mitigations.Add(new MitigationDefinition {
            Id = "dismantle",
            Name = "武装解除",
            IconActionId = 2887,
            TriggerActionIds = new List<uint> { 2887 },
            DurationSeconds = 10,
            CooldownSeconds = 120,
            Category = MitigationCategory.EnemyDebuff,
            ApplyTo = MitigationApplyTo.Source,
            Jobs = new List<JobIds> { JobIds.MCH },
            Enabled = true,
        });
    }

    private void MigrateDefaultMitigationTimingsFromMcp() {
        foreach (var def in Mitigations) {
            switch (def.Id.ToLowerInvariant()) {
                case "troubadour":
                    TryUpdateCooldown(def, new[] { 7405u }, oldCdSeconds: 90, newCdSeconds: 120);
                    break;
                case "tactician":
                    TryUpdateCooldown(def, new[] { 16889u }, oldCdSeconds: 90, newCdSeconds: 120);
                    break;
                case "shield_samba":
                    TryUpdateCooldown(def, new[] { 16012u }, oldCdSeconds: 90, newCdSeconds: 120);
                    break;
                case "shake_it_off":
                    TryUpdateDuration(def, new[] { 7388u }, oldDurationSeconds: 15, newDurationSeconds: 30);
                    break;
                case "bloodwhetting":
                    TryAddBloodwhettingDurations(def);
                    break;
            }
        }
    }

    private void MigratePhysRangedPartyMitigationCooldownTo90() {
        foreach (var def in Mitigations) {
            switch (def.Id.ToLowerInvariant()) {
                case "troubadour":
                    TryUpdateCooldown(def, new[] { 7405u }, oldCdSeconds: 120, newCdSeconds: 90);
                    break;
                case "tactician":
                    TryUpdateCooldown(def, new[] { 16889u }, oldCdSeconds: 120, newCdSeconds: 90);
                    break;
                case "shield_samba":
                    TryUpdateCooldown(def, new[] { 16012u }, oldCdSeconds: 120, newCdSeconds: 90);
                    break;
            }
        }
    }

    private static void TryUpdateCooldown(MitigationDefinition def, uint[] expectedTriggers, float oldCdSeconds, float newCdSeconds) {
        if (!HasSameTriggers(def.TriggerActionIds, expectedTriggers)) {
            return;
        }

        if (Math.Abs(def.CooldownSeconds - oldCdSeconds) > 0.01f) {
            return;
        }

        def.CooldownSeconds = newCdSeconds;
    }

    private static void TryUpdateDuration(MitigationDefinition def, uint[] expectedTriggers, float oldDurationSeconds, float newDurationSeconds) {
        if (!HasSameTriggers(def.TriggerActionIds, expectedTriggers)) {
            return;
        }

        if (Math.Abs(def.DurationSeconds - oldDurationSeconds) > 0.01f) {
            return;
        }

        def.DurationSeconds = newDurationSeconds;
    }

    private static void TryAddBloodwhettingDurations(MitigationDefinition def) {
        var expected = new[] { 16464u, 3551u, 25751u };
        if (!HasSameTriggers(def.TriggerActionIds, expected)) {
            return;
        }

        if (def.DurationSecondsByActionId != null && def.DurationSecondsByActionId.Count > 0) {
            return;
        }

        if (Math.Abs(def.DurationSeconds - 6) > 0.01f) {
            return;
        }

        def.DurationSeconds = 8;
        def.DurationSecondsByActionId = new Dictionary<uint, float> {
            [3551] = 6,
            [16464] = 8,
            [25751] = 8,
        };
    }

    private static bool HasSameTriggers(List<uint> actual, uint[] expected) {
        if (actual.Count != expected.Length) {
            return false;
        }

        for (var i = 0; i < expected.Length; i++) {
            if (!actual.Contains(expected[i])) {
                return false;
            }
        }

        return true;
    }

    public void Save() {
        pluginInterface.SavePluginConfig(this);
    }
}

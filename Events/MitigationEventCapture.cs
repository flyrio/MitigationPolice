// 本文件负责从网络包中捕获受伤事件与队员减伤施放，并写入事件存储供 UI 复盘使用。
using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using MitigationPolice.Mitigations;
using MitigationPolice.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace MitigationPolice.Events;

public unsafe sealed class MitigationEventCapture : IDisposable {
    private unsafe delegate void ProcessPacketActionEffectDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    private readonly MitigationPolicePlugin plugin;
    private readonly Hook<ProcessPacketActionEffectDelegate> processPacketActionEffectHook;

    private delegate void ProcessPacketActorControlDelegate(
        uint entityId,
        uint category,
        uint param1,
        uint param2,
        uint param3,
        uint param4,
        uint param5,
        uint param6,
        uint param7,
        uint param8,
        ulong targetId,
        byte param9);

    private readonly Hook<ProcessPacketActorControlDelegate> processPacketActorControlHook;
    private readonly object deathGate = new();
    private readonly Dictionary<uint, DateTime> lastDeathByTarget = new();
    private readonly TimeSpan deathDedupWindow = TimeSpan.FromSeconds(2);
    private readonly TimeSpan fatalLookbackWindow = TimeSpan.FromSeconds(3);

    public MitigationEventCapture(MitigationPolicePlugin plugin) {
        this.plugin = plugin;

        processPacketActionEffectHook =
            Service.GameInteropProvider.HookFromSignature<ProcessPacketActionEffectDelegate>(
                ActionEffectHandler.Addresses.Receive.String,
                ProcessPacketActionEffectDetour);
        processPacketActionEffectHook.Enable();

        processPacketActorControlHook =
            Service.GameInteropProvider.HookFromSignature<ProcessPacketActorControlDelegate>(
                "E8 ?? ?? ?? ?? 0F B7 0B 83 E9 64",
                ProcessPacketActorControlDetour);
        processPacketActorControlHook.Enable();

        Service.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose() {
        Service.Framework.Update -= OnFrameworkUpdate;
        processPacketActionEffectHook.Dispose();
        processPacketActorControlHook.Dispose();
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework) {
        var nowUtc = DateTime.UtcNow;
        plugin.MitigationState.Tick(nowUtc);
    }

    private unsafe void ProcessPacketActionEffectDetour(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* effectHeader,
        ActionEffectHandler.TargetEffects* effectArray,
        GameObjectId* targetEntityIds
    ) {
        processPacketActionEffectHook.Original(casterEntityId, casterPtr, targetPos, effectHeader, effectArray, targetEntityIds);

        try {
            if (!plugin.MitigationState.ShouldCapturePackets) {
                return;
            }

            if (effectHeader->NumTargets == 0) {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var inCombat = Service.Condition[ConditionFlag.InCombat];
            var actionId = effectHeader->SpellId;
            if (actionId == 0) {
                return;
            }

            var sourceId = casterEntityId == 0 ? null : (uint?)casterEntityId;
            var sourceName = ResolveSourceName(casterPtr, sourceId);

            var targets = stackalloc uint[(int)effectHeader->NumTargets];
            for (var i = 0; i < effectHeader->NumTargets; i++) {
                targets[i] = (uint)(targetEntityIds[i] & uint.MaxValue);
            }

            if (sourceId.HasValue && plugin.MitigationState.IsTrackedActor(sourceId.Value)) {
                plugin.MitigationState.RecordActionUsage(sourceId.Value, sourceName ?? string.Empty, actionId, new ReadOnlySpan<uint>(targets, (int)effectHeader->NumTargets), nowUtc);
            }

            for (var i = 0; i < effectHeader->NumTargets; i++) {
                var actionTargetId = targets[i];
                if (actionTargetId == 0) {
                    continue;
                }

                if (!plugin.MitigationState.IsTrackedActor(actionTargetId)) {
                    continue;
                }

                for (var j = 0; j < 8; j++) {
                    ref var actionEffect = ref effectArray[i].Effects[j];
                    if (!IsDamageEffect(actionEffect.Type)) {
                        continue;
                    }

                    var amount = ResolveDamageAmount(actionEffect);
                    var damageType = ResolveDamageTypeName((byte)(actionEffect.Param1 & 0xF));

                    var duty = plugin.MitigationState.ResolveDutyContext();
                    var targetActor = Service.ObjectTable.SearchByEntityId(actionTargetId);
                    var targetName = targetActor?.Name.ToString() ?? string.Empty;
                    var targetJob = ResolveJob(targetActor);

                    var actionName = ResolveActionName(actionId);

                    if (!inCombat) {
                        continue;
                    }

                    plugin.MitigationState.EnsureCombatStart(nowUtc);
                    plugin.EventStore.BeginCombatSession(duty, nowUtc);

                    var (active, missing) = plugin.MitigationState.AnalyzeHit(nowUtc, actionTargetId, sourceId, actionId, amount);
                    if (missing.Count > 0) {
                        missing.Sort((a, b) => b.AvailableForSeconds.CompareTo(a.AvailableForSeconds));
                    }

                    plugin.EventStore.AddToActiveSession(new DamageEventRecord {
                        TimestampUtc = new DateTimeOffset(nowUtc, TimeSpan.Zero),
                        TerritoryId = duty.TerritoryId,
                        TerritoryName = duty.TerritoryName,
                        ContentId = duty.ContentId,
                        ContentName = duty.ContentName,
                        TargetId = actionTargetId,
                        TargetName = targetName,
                        TargetJob = targetJob,
                        SourceId = sourceId,
                        SourceName = sourceName,
                        ActionId = actionId,
                        ActionName = actionName,
                        DamageAmount = amount,
                        DamageType = damageType,
                        ActiveMitigations = active,
                        MissingMitigations = missing,
                    });
                }
            }
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to process action effect");
        }
    }

    private void ProcessPacketActorControlDetour(
        uint entityId,
        uint category,
        uint param1,
        uint param2,
        uint param3,
        uint param4,
        uint param5,
        uint param6,
        uint param7,
        uint param8,
        ulong targetId,
        byte param9
    ) {
        processPacketActorControlHook.Original(entityId, category, param1, param2, param3, param4, param5, param6, param7, param8, targetId, param9);

        try {
            if (!plugin.MitigationState.ShouldCapturePackets) {
                return;
            }

            if (category != (uint)ActorControlCategory.Death) {
                return;
            }

            if (!plugin.MitigationState.IsTrackedActor(entityId)) {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            lock (deathGate) {
                if (lastDeathByTarget.TryGetValue(entityId, out var lastDeath) &&
                    nowUtc - lastDeath <= deathDedupWindow) {
                    return;
                }
                lastDeathByTarget[entityId] = nowUtc;
            }

            plugin.EventStore.TryMarkLatestFatal(entityId, nowUtc, fatalLookbackWindow);
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to process actor control death");
        }
    }

    private static uint ResolveDamageAmount(ActionEffectHandler.Effect actionEffect) {
        uint amount = actionEffect.Value;
        if ((actionEffect.Param4 & 0x40) == 0x40) {
            amount += (uint)actionEffect.Param3 << 16;
        }

        return amount;
    }

    private static JobIds ResolveJob(IGameObject? actor) {
        if (actor is IPlayerCharacter pc) {
            var rowId = pc.ClassJob.RowId;
            if (Enum.IsDefined(typeof(JobIds), rowId)) {
                return (JobIds)rowId;
            }
        }

        return JobIds.OTHER;
    }

    private static bool IsDamageEffect(byte effectType) {
        return effectType == (byte)ActionEffectType.Damage ||
               effectType == (byte)ActionEffectType.BlockedDamage ||
               effectType == (byte)ActionEffectType.ParriedDamage;
    }

    private static string ResolveDamageTypeName(byte damageType) {
        return damageType switch {
            1 => "斩击",
            2 => "穿刺",
            3 => "打击",
            4 => "射击",
            5 => "魔法",
            6 => "吐息",
            7 => "物理",
            8 => "极限技",
            _ => "未知",
        };
    }

    private static string? ResolveActionName(uint actionId) {
        var sheet = Service.DataManager.GetExcelSheet<ActionSheet>();
        var row = sheet?.GetRow(actionId);
        return row?.Name.ToString();
    }

    private static unsafe string? ResolveSourceName(Character* casterPtr, uint? sourceId) {
        if (casterPtr != null) {
            var name = casterPtr->NameString;
            if (!string.IsNullOrWhiteSpace(name)) {
                return name;
            }
        }

        return ResolveObjectName(sourceId);
    }

    private static string? ResolveObjectName(uint? objectId) {
        if (!objectId.HasValue || objectId.Value == 0) {
            return null;
        }

        var match = Service.ObjectTable.SearchByEntityId(objectId.Value);
        return match?.Name.ToString();
    }

    private enum ActionEffectType : byte {
        Damage = 3,
        BlockedDamage = 5,
        ParriedDamage = 6,
    }

    private enum ActorControlCategory : uint {
        Death = 0x06,
    }
}

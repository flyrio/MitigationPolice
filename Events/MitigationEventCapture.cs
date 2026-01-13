// 本文件负责从网络包中捕获受伤事件与队员减伤施放，并写入事件存储供 UI 复盘使用。
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using MitigationPolice.Chat;
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

    private readonly object deathAnnounceGate = new();
    private readonly Queue<DeathAnnounceRequest> pendingDeathAnnounces = new();
    private const int MaxPendingDeathAnnounces = 20;
    private readonly TimeSpan deathAnnounceInterval = TimeSpan.FromSeconds(1);
    private DateTime lastDeathAnnounceUtc = DateTime.MinValue;

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
        FlushDeathAnnouncements(nowUtc);
    }

    private unsafe void ProcessPacketActionEffectDetour(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* effectHeader,
        ActionEffectHandler.TargetEffects* effectArray,
        GameObjectId* targetEntityIds
    ) {
        var targetCount = (int)effectHeader->NumTargets;
        var actionId = effectHeader->SpellId;

        var targets = stackalloc uint[targetCount];
        for (var i = 0; i < targetCount; i++) {
            targets[i] = (uint)(targetEntityIds[i] & uint.MaxValue);
        }

        Dictionary<uint, PreHitVitals>? preVitalsByTarget = null;
        if (plugin.MitigationState.ShouldCapturePackets && targetCount > 0 && actionId != 0) {
            try {
                preVitalsByTarget = CapturePreHitVitals(new ReadOnlySpan<uint>(targets, targetCount));
            } catch (Exception ex) {
                Service.PluginLog.Warning(ex, "Failed to capture pre-hit vitals; will continue without death details");
            }
        }

        processPacketActionEffectHook.Original(casterEntityId, casterPtr, targetPos, effectHeader, effectArray, targetEntityIds);

        try {
            if (!plugin.MitigationState.ShouldCapturePackets) {
                return;
            }

            if (targetCount == 0) {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var inCombat = Service.Condition[ConditionFlag.InCombat];
            if (actionId == 0) {
                return;
            }

            var sourceId = casterEntityId == 0 ? null : (uint?)casterEntityId;
            var sourceName = ResolveSourceName(casterPtr, sourceId);

            if (sourceId.HasValue && plugin.MitigationState.IsTrackedActor(sourceId.Value)) {
                plugin.MitigationState.RecordActionUsage(sourceId.Value, sourceName ?? string.Empty, actionId, new ReadOnlySpan<uint>(targets, targetCount), nowUtc);
            }

            for (var i = 0; i < targetCount; i++) {
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
                        TargetHpBefore = preVitalsByTarget != null && preVitalsByTarget.TryGetValue(actionTargetId, out var vitals) ? vitals.HpBefore : 0,
                        TargetShieldBefore = preVitalsByTarget != null && preVitalsByTarget.TryGetValue(actionTargetId, out vitals) ? vitals.ShieldBefore : 0,
                        ActiveMitigations = active,
                        MissingMitigations = missing,
                    });
                }
            }
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to process action effect");
        }
    }

    private Dictionary<uint, PreHitVitals> CapturePreHitVitals(ReadOnlySpan<uint> targets) {
        var result = new Dictionary<uint, PreHitVitals>();

        for (var i = 0; i < targets.Length; i++) {
            var targetId = targets[i];
            if (targetId == 0) {
                continue;
            }

            if (!plugin.MitigationState.IsTrackedActor(targetId)) {
                continue;
            }

            if (!TryResolvePreHitVitals(targetId, out var vitals)) {
                continue;
            }

            result[targetId] = vitals;
        }

        return result;
    }

    private static bool TryResolvePreHitVitals(uint targetId, out PreHitVitals vitals) {
        vitals = default;

        var actor = Service.ObjectTable.SearchByEntityId(targetId);
        if (actor is not ICharacter c) {
            return false;
        }

        var hp = (uint)c.CurrentHp;
        var maxHp = (uint)c.MaxHp;

        var shieldPercent = Math.Clamp((int)c.ShieldPercentage, 0, 100);
        var shield = maxHp > 0 && shieldPercent > 0
            ? (uint)Math.Round(maxHp * (shieldPercent / 100f))
            : 0u;

        vitals = new PreHitVitals(hp, shield);
        return true;
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
            EnqueueDeathAnnouncement(entityId, nowUtc);
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to process actor control death");
        }
    }

    private void EnqueueDeathAnnouncement(uint targetId, DateTime nowUtc) {
        if (!plugin.Configuration.AutoAnnounceDeathsToPartyChat) {
            return;
        }

        lock (deathAnnounceGate) {
            while (pendingDeathAnnounces.Count >= MaxPendingDeathAnnounces) {
                pendingDeathAnnounces.Dequeue();
            }

            pendingDeathAnnounces.Enqueue(new DeathAnnounceRequest(targetId, nowUtc));
        }
    }

    private void FlushDeathAnnouncements(DateTime nowUtc) {
        if (!plugin.Configuration.AutoAnnounceDeathsToPartyChat || !plugin.ChatSender.CanSend) {
            lock (deathAnnounceGate) {
                pendingDeathAnnounces.Clear();
            }
            return;
        }

        if (nowUtc - lastDeathAnnounceUtc < deathAnnounceInterval) {
            return;
        }

        DeathAnnounceRequest? request = null;
        lock (deathAnnounceGate) {
            if (pendingDeathAnnounces.Count > 0) {
                request = pendingDeathAnnounces.Dequeue();
            }
        }

        if (!request.HasValue) {
            return;
        }

        var lines = BuildDeathAnnouncementLines(request.Value.TargetId, request.Value.WhenUtc);
        lastDeathAnnounceUtc = nowUtc;

        if (lines.Count == 0) {
            return;
        }

        if (!plugin.ChatSender.TrySendEchoMessages(lines, out var echoError)) {
            Service.PluginLog.Warning(string.IsNullOrWhiteSpace(echoError)
                ? "自动通报发送失败：未知错误"
                : $"自动通报发送失败：{echoError}");
        }

        if (plugin.Configuration.AllowSendingToPartyChat && HasPartyMembersBesidesSelf()) {
            if (!plugin.ChatSender.TrySendPartyMessages(lines, out var partyError)) {
                Service.PluginLog.Warning(string.IsNullOrWhiteSpace(partyError)
                    ? "自动通报发送到小队失败：未知错误"
                    : $"自动通报发送到小队失败：{partyError}");
            }
        }
    }

    private List<string> BuildDeathAnnouncementLines(uint targetId, DateTime nowUtc) {
        var sessionId = plugin.EventStore.ActiveSessionId;
        var nowOffset = new DateTimeOffset(nowUtc, TimeSpan.Zero);

        DamageEventRecord? record = null;
        if (sessionId.HasValue) {
            var events = plugin.EventStore.GetSessionEventsSnapshot(sessionId.Value);
            for (var i = events.Count - 1; i >= 0; i--) {
                var e = events[i];
                if (nowOffset - e.TimestampUtc > fatalLookbackWindow) {
                    break;
                }

                if (e.TargetId == targetId) {
                    record = e;
                    break;
                }
            }
        }

        if (record == null) {
            var name = ResolveObjectName(targetId) ?? targetId.ToString();
            var text = $"减伤警察：{name} 死亡（未找到对应受伤事件）";
            return new List<string> {
                ShareFormatter.SeparatorLine,
                Utf8Util.Truncate(text, 480),
                ShareFormatter.SeparatorLine,
            };
        }

        record.IsFatal = true;
        var overwrites = plugin.MitigationState.GetOverwritesForEvent(record);

        var prefix = string.Empty;
        if (sessionId.HasValue) {
            var summary = plugin.EventStore.TryGetSummary(sessionId.Value);
            if (summary.HasValue) {
                var seconds = (record.TimestampUtc - summary.Value.StartUtc).TotalSeconds;
                prefix = $"[T+{FormatShareOffsetSeconds(seconds)}] ";
            }
        }

        var raw = ShareFormatter.BuildPartyLines(record, overwrites, 320);
        var linesOut = new List<string>(raw.Count);
        var prefixApplied = false;
        for (var i = 0; i < raw.Count; i++) {
            var line = raw[i];
            if (!prefixApplied && !ShareFormatter.IsSeparatorLine(line) && !string.IsNullOrWhiteSpace(prefix)) {
                line = prefix + line;
                prefixApplied = true;
            } else if (!prefixApplied && !ShareFormatter.IsSeparatorLine(line) && string.IsNullOrWhiteSpace(prefix)) {
                prefixApplied = true;
            }

            linesOut.Add(Utf8Util.Truncate(line, 480));
        }

        return linesOut;
    }

    private static bool HasPartyMembersBesidesSelf() {
        var local = Service.ObjectTable.LocalPlayer;
        if (local == null) {
            return false;
        }

        for (var i = 0; i < Service.PartyList.Length; i++) {
            var member = Service.PartyList[i];
            if (member?.EntityId is not { } id || id == 0) {
                continue;
            }

            if (id != local.EntityId) {
                return true;
            }
        }

        return false;
    }

    private static string FormatShareOffsetSeconds(double seconds) {
        if (seconds < 0) {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalMinutes >= 1) {
            return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
        }

        return $"{Math.Max(0, (int)Math.Round(ts.TotalSeconds))}s";
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

    private readonly record struct DeathAnnounceRequest(uint TargetId, DateTime WhenUtc);

    private readonly record struct PreHitVitals(uint HpBefore, uint ShieldBefore);
}

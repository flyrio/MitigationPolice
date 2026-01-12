// 本文件维护副本会话、减伤施放记录与冷却推算，并为每次伤害生成“已交/未交”归因结果。
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;
using MitigationPolice.Chat;
using MitigationPolice.Models;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace MitigationPolice.Mitigations;

public sealed class MitigationState {
    private readonly MitigationPolicePlugin plugin;

    private DateTime combatStartUtc = DateTime.MinValue;
    private uint lastTerritoryId;
    private bool lastInInstance;
    private bool lastInCombat;

    private DateTime lastPartyScanUtc = DateTime.MinValue;
    private readonly TimeSpan partyScanInterval = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<uint, PartyMemberSnapshot> partyMembers = new();
    private HashSet<uint> trackedActorIds = new();

    private readonly object gate = new();
    private readonly Dictionary<uint, List<MitigationDefinition>> definitionsByTriggerAction = new();
    private readonly Dictionary<ActiveGroupKey, ActiveEffect> activeByGroupKey = new();
    private readonly Dictionary<OwnerKey, OwnerLastUse> lastUseByOwner = new();
    private readonly List<MitigationOverwrite> overwrites = new();
    private readonly Queue<string> pendingOverwriteAnnouncements = new();
    private DateTime lastAutoAnnounceUtc = DateTime.MinValue;

    private const int MaxOverwriteRecords = 5000;
    private readonly TimeSpan overwriteRetention = TimeSpan.FromMinutes(20);
    private readonly TimeSpan overwriteLookback = TimeSpan.FromSeconds(45);
    private readonly TimeSpan autoAnnounceInterval = TimeSpan.FromSeconds(1);
    private const int MaxPendingOverwriteAnnouncements = 50;

    private const string ConflictGroupPhysRangedPartyMitigation = "phys_ranged_party_mitigation";

    private static readonly HashSet<uint> PhysRangedPartyMitigationActionIds = new() {
        7405,  // 行吟（吟游诗人）
        16889, // 策动（机工士）
        16012, // 防守之桑巴（舞者）
    };

    private static readonly HashSet<uint> EnemyDebuffDurationUpgradeActionIds = new() {
        7560, // 昏乱
        7549, // 牵制
        7535, // 雪仇
    };

    public MitigationState(MitigationPolicePlugin plugin) {
        this.plugin = plugin;
        ReloadFromConfig();
    }

    public bool ShouldCapturePackets {
        get {
            if (!Service.ClientState.IsLoggedIn || Service.ObjectTable.LocalPlayer == null) {
                return false;
            }

            if (plugin.Configuration.TrackOnlyInInstances && !IsInInstance()) {
                return false;
            }

            return true;
        }
    }

    public DateTime CombatStartUtc => combatStartUtc;

    public bool IsInCombat => lastInCombat && combatStartUtc != DateTime.MinValue;

    public void Tick(DateTime nowUtc) {
        if (!Service.ClientState.IsLoggedIn || Service.ObjectTable.LocalPlayer == null) {
            plugin.EventStore.EndCombatSession(nowUtc);
            ResetAllState();
            lastTerritoryId = 0;
            lastInInstance = false;
            lastInCombat = false;
            return;
        }

        var territoryId = Service.ClientState.TerritoryType;
        var inInstance = IsInInstance();
        var inCombat = Service.Condition[ConditionFlag.InCombat];

        if (territoryId != lastTerritoryId || inInstance != lastInInstance) {
            plugin.EventStore.EndCombatSession(nowUtc);
            ResetAllState();
            lastTerritoryId = territoryId;
            lastInInstance = inInstance;
            lastInCombat = false;
            inCombat = false;
        }

        if (!ShouldCapturePackets) {
            if (lastInCombat) {
                plugin.EventStore.EndCombatSession(nowUtc);
                lastInCombat = false;
                combatStartUtc = DateTime.MinValue;
            }
        } else if (inCombat != lastInCombat) {
            lastInCombat = inCombat;
            if (inCombat) {
                combatStartUtc = nowUtc;
                plugin.EventStore.BeginCombatSession(ResolveDutyContext(), nowUtc);
            } else {
                plugin.EventStore.EndCombatSession(nowUtc);
                combatStartUtc = DateTime.MinValue;
            }
        }

        if (nowUtc - lastPartyScanUtc >= partyScanInterval) {
            lastPartyScanUtc = nowUtc;
            RefreshPartyMembers();
        }

        PruneExpired(nowUtc);
        FlushOverwriteAnnouncements(nowUtc);
    }

    public void ReloadFromConfig() {
        lock (gate) {
            definitionsByTriggerAction.Clear();

            foreach (var def in plugin.Configuration.Mitigations.Where(m => m.Enabled)) {
                foreach (var actionId in def.TriggerActionIds.Distinct()) {
                    if (!definitionsByTriggerAction.TryGetValue(actionId, out var list)) {
                        list = new List<MitigationDefinition>();
                        definitionsByTriggerAction[actionId] = list;
                    }

                    list.Add(def);
                }
            }
        }
    }

    public void EnsureCombatStart(DateTime nowUtc) {
        if (combatStartUtc == DateTime.MinValue) {
            combatStartUtc = nowUtc;
        }
    }

    public void ResetCombatStart(DateTime nowUtc) {
        combatStartUtc = nowUtc;
        lock (gate) {
            lastUseByOwner.Clear();
        }
    }

    public IReadOnlyDictionary<uint, PartyMemberSnapshot> GetPartyMembersSnapshot() {
        lock (gate) {
            return new Dictionary<uint, PartyMemberSnapshot>(partyMembers);
        }
    }

    public bool IsTrackedActor(uint entityId) {
        lock (gate) {
            return trackedActorIds.Contains(entityId);
        }
    }

    public DutyContext ResolveDutyContext() {
        var territoryId = Service.ClientState.TerritoryType;
        var territoryName = string.Empty;
        uint? contentId = null;
        string? contentName = null;

        var territorySheet = Service.DataManager.GetExcelSheet<TerritoryType>();
        var territory = territorySheet?.GetRow(territoryId);
        if (territory != null) {
            if (territory.Value.PlaceName.RowId != 0) {
                territoryName = territory.Value.PlaceName.Value.Name.ToString();
            }

            if (territory.Value.ContentFinderCondition.RowId != 0) {
                var content = territory.Value.ContentFinderCondition.Value;
                contentId = content.RowId;
                contentName = content.Name.ToString();
            }
        }

        return new DutyContext(territoryId, territoryName, contentId, contentName);
    }

    public void RecordActionUsage(uint casterId, string casterName, uint actionId, ReadOnlySpan<uint> targets, DateTime nowUtc) {
        List<MitigationDefinition>? defs;
        lock (gate) {
            if (!definitionsByTriggerAction.TryGetValue(actionId, out defs)) {
                return;
            }
        }

        var casterLevel = ResolvePartyMemberLevel(casterId);
        var nowOffset = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        var shouldAutoAnnounce = plugin.Configuration.AllowSendingToPartyChat &&
                                 plugin.Configuration.AutoAnnounceOverwritesToPartyChat;
        List<MitigationOverwrite>? newlyDetectedOverwrites = null;

        foreach (var def in defs) {
            if (!IsEligibleJob(casterId, def)) {
                continue;
            }

            lock (gate) {
                lastUseByOwner[new OwnerKey(casterId, def.Id)] = new OwnerLastUse(nowUtc, actionId, casterLevel);
            }

            var durationSeconds = ResolveDurationSeconds(def, actionId, casterLevel);
            if (durationSeconds <= 0) {
                continue;
            }

            var expiresUtc = nowUtc.AddSeconds(durationSeconds);
            var mitigationName = ResolveMitigationName(def);
            var conflictGroupId = ResolveConflictGroupId(def);
            var iconActionId = def.IconActionId != 0 ? def.IconActionId : actionId;

            if (targets.Length == 0) {
                continue;
            }

            for (var i = 0; i < targets.Length; i++) {
                var appliedActorId = targets[i];
                if (appliedActorId == 0) {
                    continue;
                }

                lock (gate) {
                    var key = new ActiveGroupKey(conflictGroupId, appliedActorId);

                    if (activeByGroupKey.TryGetValue(key, out var existing) &&
                        existing.ExpiresUtc > nowUtc) {
                        var overwrite = new MitigationOverwrite {
                            TimestampUtc = nowOffset,
                            AppliedActorId = appliedActorId,
                            AppliedActorName = ResolvePartyMemberNameLocked(appliedActorId),
                            ConflictGroupId = conflictGroupId,
                            OldMitigationId = existing.MitigationId,
                            OldMitigationName = existing.MitigationName,
                            OldCasterId = existing.CasterId,
                            OldCasterName = existing.CasterName,
                            OldRemainingSeconds = (float)(existing.ExpiresUtc - nowUtc).TotalSeconds,
                            NewMitigationId = def.Id,
                            NewMitigationName = mitigationName,
                            NewCasterId = casterId,
                            NewCasterName = casterName,
                            NewDurationSeconds = durationSeconds,
                        };
                        overwrites.Add(overwrite);
                        PruneOverwritesLocked(nowUtc);

                        if (shouldAutoAnnounce && !IsRefreshOverwrite(overwrite)) {
                            newlyDetectedOverwrites ??= new List<MitigationOverwrite>();
                            newlyDetectedOverwrites.Add(overwrite);
                        }
                    }

                    activeByGroupKey[key] = new ActiveEffect(def.Id, mitigationName, iconActionId, casterId, casterName, expiresUtc);
                }
            }
        }

        if (newlyDetectedOverwrites != null && newlyDetectedOverwrites.Count > 0) {
            EnqueueOverwriteAnnouncement(newlyDetectedOverwrites);
        }
    }

    public List<MitigationOverwrite> GetOverwritesForEvent(DamageEventRecord record) {
        var eventUtc = record.TimestampUtc.UtcDateTime;
        var fromUtc = eventUtc - overwriteLookback;
        var results = new List<MitigationOverwrite>();

        lock (gate) {
            for (var i = overwrites.Count - 1; i >= 0; i--) {
                var o = overwrites[i];
                var t = o.TimestampUtc.UtcDateTime;
                if (t > eventUtc) {
                    continue;
                }

                if (t < fromUtc) {
                    break;
                }

                if (o.AppliedActorId == record.TargetId ||
                    (record.SourceId.HasValue && o.AppliedActorId == record.SourceId.Value)) {
                    results.Add(o);
                    if (results.Count >= 30) {
                        break;
                    }
                }
            }
        }

        results.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
        return results;
    }

    public (List<MitigationContribution> Active, List<MissingMitigation> Missing) AnalyzeHit(
        DateTime nowUtc,
        uint targetId,
        uint? sourceId,
        uint? actionId,
        uint damageAmount) {
        var active = new List<MitigationContribution>();
        var missing = new List<MissingMitigation>();

        if (plugin.Configuration.MinDamageToAnalyzeMissing > 0 &&
            damageAmount < plugin.Configuration.MinDamageToAnalyzeMissing) {
            return (active, missing);
        }

        var defs = plugin.Configuration.Mitigations.Where(d => d.Enabled).ToList();
        var groups = new Dictionary<ActiveGroupKey, List<MitigationDefinition>>();

        foreach (var def in defs) {
            if (!ShouldAnalyze(def)) {
                continue;
            }

            if (def.ApplyTo == MitigationApplyTo.Source && !sourceId.HasValue) {
                continue;
            }

            var appliedActorId = def.ApplyTo switch {
                MitigationApplyTo.Target => (uint?)targetId,
                MitigationApplyTo.Source => sourceId,
                _ => null,
            };

            if (!appliedActorId.HasValue) {
                continue;
            }

            var groupId = ResolveConflictGroupId(def);
            var key = new ActiveGroupKey(groupId, appliedActorId.Value);
            if (!groups.TryGetValue(key, out var list)) {
                list = new List<MitigationDefinition>();
                groups[key] = list;
            }

            list.Add(def);
        }

        foreach (var (key, groupDefs) in groups) {
            if (TryGetActiveEffect(key, nowUtc, out var effect)) {
                active.Add(new MitigationContribution {
                    MitigationId = effect.MitigationId,
                    MitigationName = effect.MitigationName,
                    IconActionId = effect.IconActionId,
                    CasterId = effect.CasterId,
                    CasterName = effect.CasterName,
                    RemainingSeconds = (float)(effect.ExpiresUtc - nowUtc).TotalSeconds,
                });
                continue;
            }

            foreach (var def in groupDefs) {
                var owners = def.Category == MitigationCategory.Personal
                    ? GetEligiblePersonalOwners(def, targetId)
                    : GetEligibleOwners(def);

                foreach (var owner in owners) {
                    var availability = GetAvailability(def, owner, nowUtc);
                    if (!availability.IsAvailable) {
                        continue;
                    }

                    missing.Add(new MissingMitigation {
                        MitigationId = def.Id,
                        MitigationName = ResolveMitigationName(def),
                        IconActionId = def.IconActionId,
                        OwnerId = owner.EntityId,
                        OwnerName = owner.Name,
                        OwnerJob = owner.Job,
                        NeverUsedSinceDutyStart = availability.NeverUsedSinceDutyStart,
                        AvailableForSeconds = availability.AvailableForSeconds,
                    });
                }
            }
        }

        return (active, missing);
    }

    private List<PartyMemberSnapshot> GetEligiblePersonalOwners(MitigationDefinition def, uint targetId) {
        lock (gate) {
            if (!partyMembers.TryGetValue(targetId, out var member)) {
                return new List<PartyMemberSnapshot>();
            }

            if (def.Jobs.Count != 0 && !def.Jobs.Contains(member.Job)) {
                return new List<PartyMemberSnapshot>();
            }

            return new List<PartyMemberSnapshot> { member };
        }
    }

    private void RefreshPartyMembers() {
        var newTracked = new HashSet<uint>();
        var newMembers = new Dictionary<uint, PartyMemberSnapshot>();

        var local = Service.ObjectTable.LocalPlayer;
        if (local != null) {
            var job = ResolveJob(local);
            var level = ResolveLevel(local);
            newTracked.Add(local.EntityId);
            newMembers[local.EntityId] = new PartyMemberSnapshot(local.EntityId, local.Name.ToString(), job, level);
        }

        for (var i = 0; i < Service.PartyList.Length; i++) {
            var member = Service.PartyList[i];
            if (member?.EntityId is not { } id || id == 0) {
                continue;
            }

            var actor = Service.ObjectTable.SearchByEntityId(id);
            var name = actor?.Name.ToString() ?? string.Empty;
            var job = actor != null ? ResolveJob(actor) : JobIds.OTHER;
            var level = actor != null ? ResolveLevel(actor) : 0;

            newTracked.Add(id);
            newMembers[id] = new PartyMemberSnapshot(id, name, job, level);
        }

        lock (gate) {
            trackedActorIds = newTracked;
            partyMembers.Clear();
            foreach (var kv in newMembers) {
                partyMembers[kv.Key] = kv.Value;
            }
        }
    }

    private static JobIds ResolveJob(IGameObject actor) {
        if (actor is IPlayerCharacter pc) {
            var rowId = pc.ClassJob.RowId;
            if (Enum.IsDefined(typeof(JobIds), rowId)) {
                return (JobIds)rowId;
            }
        }

        return JobIds.OTHER;
    }

    private static int ResolveLevel(IGameObject actor) {
        if (actor is IPlayerCharacter pc) {
            return pc.Level;
        }

        return 0;
    }

    private bool ShouldAnalyze(MitigationDefinition def) {
        return def.Category switch {
            MitigationCategory.Personal => plugin.Configuration.IncludePersonalMitigations,
            MitigationCategory.Party => plugin.Configuration.IncludePartyMitigations,
            MitigationCategory.EnemyDebuff => plugin.Configuration.IncludeEnemyMitigations,
            _ => true,
        };
    }

    private string ResolveMitigationName(MitigationDefinition def) {
        if (!string.IsNullOrWhiteSpace(def.Name)) {
            return def.Name;
        }

        var sheet = Service.DataManager.GetExcelSheet<ActionSheet>();
        return sheet?.GetRow(def.IconActionId).Name.ToString() ?? def.Id;
    }

    private static string ResolveConflictGroupId(MitigationDefinition def) {
        if (def.IconActionId != 0 && PhysRangedPartyMitigationActionIds.Contains(def.IconActionId)) {
            return ConflictGroupPhysRangedPartyMitigation;
        }

        if (def.TriggerActionIds != null && def.TriggerActionIds.Any(id => PhysRangedPartyMitigationActionIds.Contains(id))) {
            return ConflictGroupPhysRangedPartyMitigation;
        }

        return def.Id;
    }

    private bool TryGetActiveEffect(ActiveGroupKey key, DateTime nowUtc, out ActiveEffect effect) {
        lock (gate) {
            if (activeByGroupKey.TryGetValue(key, out effect) && effect.ExpiresUtc > nowUtc) {
                return true;
            }
        }

        effect = default;
        return false;
    }

    private string ResolvePartyMemberNameLocked(uint entityId) {
        if (partyMembers.TryGetValue(entityId, out var member)) {
            return member.Name;
        }

        var actor = Service.ObjectTable.SearchByEntityId(entityId);
        return actor?.Name.ToString() ?? string.Empty;
    }

    private int ResolvePartyMemberLevel(uint entityId) {
        lock (gate) {
            if (partyMembers.TryGetValue(entityId, out var member) && member.Level > 0) {
                return member.Level;
            }
        }

        var actor = Service.ObjectTable.SearchByEntityId(entityId);
        return actor != null ? ResolveLevel(actor) : 0;
    }

    private bool IsEligibleJob(uint casterId, MitigationDefinition def) {
        var job = JobIds.OTHER;
        lock (gate) {
            if (partyMembers.TryGetValue(casterId, out var member)) {
                job = member.Job;
            }
        }

        return def.Jobs.Count == 0 || def.Jobs.Contains(job);
    }

    private List<PartyMemberSnapshot> GetEligibleOwners(MitigationDefinition def) {
        lock (gate) {
            return partyMembers.Values
                .Where(member => def.Jobs.Count == 0 || def.Jobs.Contains(member.Job))
                .ToList();
        }
    }

    private Availability GetAvailability(MitigationDefinition def, PartyMemberSnapshot owner, DateTime nowUtc) {
        var key = new OwnerKey(owner.EntityId, def.Id);
        OwnerLastUse lastUse;

        lock (gate) {
            if (!lastUseByOwner.TryGetValue(key, out lastUse)) {
                if (!plugin.Configuration.AssumeReadyAtDutyStart || combatStartUtc == DateTime.MinValue) {
                    return Availability.Unavailable();
                }

                return Availability.Available(true, (float)(nowUtc - combatStartUtc).TotalSeconds);
            }
        }

        var level = lastUse.LevelAtUse > 0 ? lastUse.LevelAtUse : owner.Level;
        var availableSince = lastUse.LastUsedUtc.AddSeconds(ResolveCooldownSeconds(def, lastUse.ActionId, level));
        if (nowUtc < availableSince) {
            return Availability.Unavailable();
        }

        if (combatStartUtc != DateTime.MinValue && availableSince <= combatStartUtc) {
            return Availability.Available(true, (float)(nowUtc - combatStartUtc).TotalSeconds);
        }

        return Availability.Available(false, (float)(nowUtc - availableSince).TotalSeconds);
    }

    private void PruneExpired(DateTime nowUtc) {
        lock (gate) {
            if (activeByGroupKey.Count > 0) {
                var expired = new List<ActiveGroupKey>();
                foreach (var (key, effect) in activeByGroupKey) {
                    if (effect.ExpiresUtc <= nowUtc) {
                        expired.Add(key);
                    }
                }

                foreach (var key in expired) {
                    activeByGroupKey.Remove(key);
                }
            }

            PruneOverwritesLocked(nowUtc);
        }
    }

    private void PruneOverwritesLocked(DateTime nowUtc) {
        if (overwrites.Count == 0) {
            return;
        }

        var cutoffUtc = nowUtc - overwriteRetention;
        var removeCount = 0;
        for (var i = 0; i < overwrites.Count; i++) {
            if (overwrites[i].TimestampUtc.UtcDateTime < cutoffUtc) {
                removeCount++;
            } else {
                break;
            }
        }

        if (removeCount > 0) {
            overwrites.RemoveRange(0, removeCount);
        }

        if (overwrites.Count > MaxOverwriteRecords) {
            overwrites.RemoveRange(0, overwrites.Count - MaxOverwriteRecords);
        }
    }

    private void ResetAllState() {
        combatStartUtc = DateTime.MinValue;
        lastInCombat = false;
        lock (gate) {
            partyMembers.Clear();
            trackedActorIds = new HashSet<uint>();
            activeByGroupKey.Clear();
            lastUseByOwner.Clear();
            overwrites.Clear();
            pendingOverwriteAnnouncements.Clear();
        }
        lastAutoAnnounceUtc = DateTime.MinValue;
    }

    private void EnqueueOverwriteAnnouncement(IReadOnlyList<MitigationOverwrite> newlyDetectedOverwrites) {
        if (!plugin.Configuration.AllowSendingToPartyChat ||
            !plugin.Configuration.AutoAnnounceOverwritesToPartyChat) {
            return;
        }

        var message = BuildOverwriteAnnouncementMessage(newlyDetectedOverwrites);
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        lock (gate) {
            while (pendingOverwriteAnnouncements.Count >= MaxPendingOverwriteAnnouncements) {
                pendingOverwriteAnnouncements.Dequeue();
            }

            pendingOverwriteAnnouncements.Enqueue(message);
        }
    }

    private void FlushOverwriteAnnouncements(DateTime nowUtc) {
        if (!plugin.Configuration.AllowSendingToPartyChat ||
            !plugin.Configuration.AutoAnnounceOverwritesToPartyChat ||
            !HasPartyMembersBesidesSelf()) {
            lock (gate) {
                pendingOverwriteAnnouncements.Clear();
            }
            return;
        }

        if (nowUtc - lastAutoAnnounceUtc < autoAnnounceInterval) {
            return;
        }

        string? message = null;
        lock (gate) {
            if (pendingOverwriteAnnouncements.Count > 0) {
                message = pendingOverwriteAnnouncements.Dequeue();
            }
        }

        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        if (plugin.ChatSender.TrySendPartyMessage(message, out var error)) {
            lastAutoAnnounceUtc = nowUtc;
            return;
        }

        lastAutoAnnounceUtc = nowUtc;
        Service.PluginLog.Warning(string.IsNullOrWhiteSpace(error)
            ? "自动通报发送失败：未知错误"
            : $"自动通报发送失败：{error}");
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

    private static string BuildOverwriteAnnouncementMessage(IReadOnlyList<MitigationOverwrite> newlyDetectedOverwrites) {
        var grouped = newlyDetectedOverwrites
            .Where(o => !IsRefreshOverwrite(o))
            .GroupBy(o => new OverwriteAnnounceKey(o.ConflictGroupId, o.OldMitigationId, o.OldCasterId, o.NewMitigationId, o.NewCasterId))
            .Select(g => new {
                Representative = g.OrderByDescending(x => x.OldRemainingSeconds).First(),
                Count = g.Select(x => x.AppliedActorId).Distinct().Count(),
            })
            .OrderByDescending(x => x.Representative.OldRemainingSeconds)
            .ThenByDescending(x => x.Count)
            .ToList();

        if (grouped.Count == 0) {
            return string.Empty;
        }

        const int maxParts = 2;
        var parts = grouped
            .Take(maxParts)
            .Select(x => FormatOverwriteAnnouncementPart(x.Representative, x.Count))
            .ToList();

        var extra = grouped.Count - parts.Count;
        var message = extra > 0
            ? $"顶减伤：{string.Join(" ", parts)} …+{extra}"
            : $"顶减伤：{string.Join(" ", parts)}";

        return Utf8Util.Truncate(message, 480);
    }

    private static bool IsRefreshOverwrite(MitigationOverwrite overwrite) {
        return overwrite.OldCasterId == overwrite.NewCasterId &&
               string.Equals(overwrite.OldMitigationId, overwrite.NewMitigationId, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatOverwriteAnnouncementPart(MitigationOverwrite overwrite, int affectedTargetCount) {
        var newCaster = string.IsNullOrWhiteSpace(overwrite.NewCasterName) ? "?" : overwrite.NewCasterName;
        var oldCaster = string.IsNullOrWhiteSpace(overwrite.OldCasterName) ? "?" : overwrite.OldCasterName;
        var remaining = FormatSeconds(overwrite.OldRemainingSeconds);

        var part = $"{overwrite.NewMitigationName}@{newCaster}顶{overwrite.OldMitigationName}@{oldCaster}(旧剩{remaining})";
        if (affectedTargetCount > 1) {
            part += $"x{affectedTargetCount}";
        }

        return part;
    }

    private static string FormatSeconds(float seconds) {
        if (seconds < 0) {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalMinutes >= 1) {
            return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
        }

        return $"{(int)Math.Round(ts.TotalSeconds)}s";
    }

    private static float ResolveDurationSeconds(MitigationDefinition def, uint actionId, int casterLevel) {
        if (def.DurationSecondsByActionId != null && def.DurationSecondsByActionId.TryGetValue(actionId, out var value)) {
            return value;
        }

        if (casterLevel > 0 && EnemyDebuffDurationUpgradeActionIds.Contains(actionId)) {
            return casterLevel >= 98 ? 15 : 10;
        }

        return def.DurationSeconds;
    }

    private static float ResolveCooldownSeconds(MitigationDefinition def, uint actionId, int casterLevel) {
        if (def.CooldownSecondsByActionId != null && def.CooldownSecondsByActionId.TryGetValue(actionId, out var value)) {
            return value;
        }

        if (casterLevel > 0 && PhysRangedPartyMitigationActionIds.Contains(actionId)) {
            return casterLevel >= 88 ? 90 : 120;
        }

        return def.CooldownSeconds;
    }

    private static bool IsInInstance() {
        return IsConditionActive("BoundByDuty") ||
               IsConditionActive("BoundByDuty56") ||
               IsConditionActive("BoundByDuty95");
    }

    private static bool IsConditionActive(string flagName) {
        return Enum.TryParse(flagName, out ConditionFlag flag) && Service.Condition[flag];
    }

    private readonly record struct ActiveGroupKey(string ConflictGroupId, uint AppliedActorId);

    private readonly record struct OwnerKey(uint OwnerId, string MitigationId);

    private readonly record struct OwnerLastUse(DateTime LastUsedUtc, uint ActionId, int LevelAtUse);

    private readonly record struct ActiveEffect(string MitigationId, string MitigationName, uint IconActionId, uint CasterId, string CasterName, DateTime ExpiresUtc);

    private readonly record struct OverwriteAnnounceKey(string ConflictGroupId, string OldMitigationId, uint OldCasterId, string NewMitigationId, uint NewCasterId);

    public readonly record struct DutyContext(uint TerritoryId, string TerritoryName, uint? ContentId, string? ContentName);

    public readonly record struct PartyMemberSnapshot(uint EntityId, string Name, JobIds Job, int Level);

    private readonly record struct Availability(bool IsAvailable, bool NeverUsedSinceDutyStart, float AvailableForSeconds) {
        public static Availability Unavailable() => new(false, false, 0);
        public static Availability Available(bool neverUsed, float availableForSeconds) => new(true, neverUsed, availableForSeconds);
    }
}

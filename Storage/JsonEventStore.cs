// 本文件负责伤害事件的本地持久化（JSON），并提供快照读取给 UI 使用。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using MitigationPolice.Mitigations;
using MitigationPolice.Models;

namespace MitigationPolice.Storage;

public sealed class JsonEventStore : IDisposable {
    private const int CurrentSchemaVersion = 2;

    private readonly string filePath;
    private readonly object gate = new();
    private readonly List<CombatSessionRecord> sessions = new();
    private readonly JsonSerializerOptions options = new() {
        WriteIndented = true,
    };

    private readonly Func<int> maxRecordsProvider;
    private readonly Func<int> debounceMsProvider;
    private DateTime lastSaveUtc = DateTime.MinValue;
    private bool dirty;

    public JsonEventStore(IDalamudPluginInterface pluginInterface, Func<int> maxRecordsProvider, Func<int> debounceMsProvider) {
        this.maxRecordsProvider = maxRecordsProvider;
        this.debounceMsProvider = debounceMsProvider;

        var directory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "mitigation-police.json");
    }

    public int TotalSessions {
        get {
            lock (gate) {
                return sessions.Count;
            }
        }
    }

    public int TotalEvents {
        get {
            lock (gate) {
                return sessions.Sum(s => s.Events.Count);
            }
        }
    }

    public bool HasActiveSession {
        get {
            lock (gate) {
                return sessions.Any(s => s.EndUtc == null);
            }
        }
    }

    public Guid? ActiveSessionId {
        get {
            lock (gate) {
                return sessions.LastOrDefault(s => s.EndUtc == null)?.Id;
            }
        }
    }

    public List<CombatSessionSummary> GetSessionSummaries() {
        lock (gate) {
            return sessions
                .OrderByDescending(s => s.StartUtc)
                .Select(s => new CombatSessionSummary(
                    s.Id,
                    s.StartUtc,
                    s.EndUtc,
                    s.TerritoryId,
                    s.TerritoryName,
                    s.ContentId,
                    s.ContentName,
                    s.Events.Count,
                    s.Events.Count(e => e.IsFatal)))
                .ToList();
        }
    }

    public CombatSessionSummary? TryGetSummary(Guid sessionId) {
        lock (gate) {
            var s = sessions.FirstOrDefault(x => x.Id == sessionId);
            if (s == null) {
                return null;
            }

            return new CombatSessionSummary(
                s.Id,
                s.StartUtc,
                s.EndUtc,
                s.TerritoryId,
                s.TerritoryName,
                s.ContentId,
                s.ContentName,
                s.Events.Count,
                s.Events.Count(e => e.IsFatal));
        }
    }

    public List<DamageEventRecord> GetSessionEventsSnapshot(Guid sessionId) {
        lock (gate) {
            var s = sessions.FirstOrDefault(x => x.Id == sessionId);
            return s == null ? new List<DamageEventRecord>() : new List<DamageEventRecord>(s.Events);
        }
    }

    public Guid? GetMostRecentSessionId() {
        lock (gate) {
            return sessions
                .OrderByDescending(s => s.StartUtc)
                .FirstOrDefault()
                ?.Id;
        }
    }

    public void BeginCombatSession(MitigationState.DutyContext duty, DateTime nowUtc) {
        var nowOffset = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        lock (gate) {
            var existingActive = sessions.LastOrDefault(s => s.EndUtc == null);
            if (existingActive != null) {
                return;
            }

            sessions.Add(new CombatSessionRecord {
                StartUtc = nowOffset,
                TerritoryId = duty.TerritoryId,
                TerritoryName = duty.TerritoryName,
                ContentId = duty.ContentId,
                ContentName = duty.ContentName,
            });

            dirty = true;
        }
    }

    public void EndCombatSession(DateTime nowUtc) {
        var nowOffset = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        var shouldSave = false;

        lock (gate) {
            var active = sessions.LastOrDefault(s => s.EndUtc == null);
            if (active == null) {
                return;
            }

            active.EndUtc = nowOffset;
            if (active.Events.Count == 0) {
                sessions.Remove(active);
            } else {
                TrimToMaxLocked();
            }

            dirty = true;
            shouldSave = true;
        }

        if (shouldSave) {
            Save();
        }
    }

    public void AddToActiveSession(DamageEventRecord record) {
        lock (gate) {
            var active = sessions.LastOrDefault(s => s.EndUtc == null);
            if (active == null) {
                sessions.Add(new CombatSessionRecord {
                    StartUtc = record.TimestampUtc,
                    TerritoryId = record.TerritoryId,
                    TerritoryName = record.TerritoryName,
                    ContentId = record.ContentId,
                    ContentName = record.ContentName,
                    Events = new List<DamageEventRecord> { record },
                });
            } else {
                active.Events.Add(record);
            }

            TrimToMaxLocked();
            dirty = true;
        }
    }

    public bool TryMarkLatestFatal(uint targetId, DateTime nowUtc, TimeSpan lookback) {
        var nowOffset = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        lock (gate) {
            var session = sessions.LastOrDefault(s => s.EndUtc == null) ?? sessions.LastOrDefault();
            if (session == null || session.Events.Count == 0) {
                return false;
            }

            for (var i = session.Events.Count - 1; i >= 0; i--) {
                var e = session.Events[i];
                if (e.TargetId != targetId) {
                    continue;
                }

                if (e.IsFatal) {
                    return false;
                }

                if (nowOffset - e.TimestampUtc > lookback) {
                    return false;
                }

                e.IsFatal = true;
                dirty = true;
                return true;
            }

            return false;
        }
    }

    public void ClearAll() {
        lock (gate) {
            sessions.Clear();
            dirty = true;
        }
    }

    public void Load() {
        if (!File.Exists(filePath)) {
            return;
        }

        try {
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) {
                return;
            }

            using var document = JsonDocument.Parse(json);
            List<CombatSessionRecord>? loadedSessions = null;

            if (document.RootElement.ValueKind == JsonValueKind.Array) {
                var legacyRecords = JsonSerializer.Deserialize<List<DamageEventRecord>>(json, options);
                loadedSessions = MigrateLegacyRecords(legacyRecords);
            } else if (document.RootElement.ValueKind == JsonValueKind.Object) {
                if (document.RootElement.TryGetProperty("Sessions", out _)) {
                    var envelope = JsonSerializer.Deserialize<EventStoreEnvelope>(json, options);
                    if (envelope != null) {
                        loadedSessions = envelope.Sessions;
                        if (envelope.SchemaVersion != CurrentSchemaVersion) {
                            Service.PluginLog.Warning($"Event store schema version {envelope.SchemaVersion} loaded; current is {CurrentSchemaVersion}.");
                        }
                    }
                } else if (document.RootElement.TryGetProperty("Records", out _)) {
                    var legacyEnvelope = JsonSerializer.Deserialize<LegacyEventStoreEnvelope>(json, options);
                    loadedSessions = MigrateLegacyRecords(legacyEnvelope?.Records);
                }
            }

            if (loadedSessions == null) {
                return;
            }

            lock (gate) {
                sessions.Clear();
                sessions.AddRange(loadedSessions);
                TrimToMaxLocked();
                dirty = false;
            }
        } catch (JsonException ex) {
            BackupCorruptFile(ex);
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to load event store");
        }
    }

    public void Tick() {
        if (!dirty) {
            return;
        }

        var debounceMs = Math.Clamp(debounceMsProvider(), 0, 60_000);
        var nowUtc = DateTime.UtcNow;
        if (debounceMs > 0 && nowUtc - lastSaveUtc < TimeSpan.FromMilliseconds(debounceMs)) {
            return;
        }

        Save();
    }

    public void Save() {
        try {
            List<CombatSessionRecord> snapshot;
            lock (gate) {
                snapshot = CloneSessionsLocked();
                dirty = false;
                lastSaveUtc = DateTime.UtcNow;
            }

            var envelope = new EventStoreEnvelope {
                SchemaVersion = CurrentSchemaVersion,
                Sessions = snapshot,
            };

            var json = JsonSerializer.Serialize(envelope, options);
            var tempPath = $"{filePath}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, true);
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to save event store");
            lock (gate) {
                dirty = true;
            }
        }
    }

    public void Dispose() {
        try {
            EndCombatSession(DateTime.UtcNow);
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to finalize active combat session on dispose");
        }

        if (dirty) {
            Save();
        }
    }

    private void TrimToMaxLocked() {
        var max = Math.Clamp(maxRecordsProvider(), 100, 200_000);
        var total = sessions.Sum(s => s.Events.Count);
        if (total <= max) {
            return;
        }

        while (sessions.Count > 1 && total > max) {
            total -= sessions[0].Events.Count;
            sessions.RemoveAt(0);
        }

        if (sessions.Count == 0) {
            return;
        }

        var only = sessions[0];
        if (only.Events.Count <= max) {
            return;
        }

        var removeCount = only.Events.Count - max;
        only.Events.RemoveRange(0, removeCount);
    }

    private void BackupCorruptFile(Exception ex) {
        Service.PluginLog.Error(ex, "Failed to load event store");
        try {
            var backupPath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
            File.Copy(filePath, backupPath, true);
            Service.PluginLog.Warning($"Backed up corrupt event store to {backupPath}");
        } catch (Exception backupEx) {
            Service.PluginLog.Error(backupEx, "Failed to backup corrupt event store");
        }
    }

    private sealed class EventStoreEnvelope {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<CombatSessionRecord> Sessions { get; set; } = new();
    }

    private sealed class LegacyEventStoreEnvelope {
        public int SchemaVersion { get; set; } = 1;
        public List<DamageEventRecord> Records { get; set; } = new();
    }

    private List<CombatSessionRecord> CloneSessionsLocked() {
        var list = new List<CombatSessionRecord>(sessions.Count);
        foreach (var s in sessions) {
            list.Add(new CombatSessionRecord {
                Id = s.Id,
                StartUtc = s.StartUtc,
                EndUtc = s.EndUtc,
                TerritoryId = s.TerritoryId,
                TerritoryName = s.TerritoryName,
                ContentId = s.ContentId,
                ContentName = s.ContentName,
                Events = new List<DamageEventRecord>(s.Events),
            });
        }
        return list;
    }

    private static List<CombatSessionRecord> MigrateLegacyRecords(List<DamageEventRecord>? legacy) {
        if (legacy == null || legacy.Count == 0) {
            return new List<CombatSessionRecord>();
        }

        var ordered = legacy.OrderBy(r => r.TimestampUtc).ToList();
        var first = ordered[0];
        var start = ordered.First().TimestampUtc;
        var end = ordered.Last().TimestampUtc;

        return new List<CombatSessionRecord> {
            new() {
                StartUtc = start,
                EndUtc = end,
                TerritoryId = first.TerritoryId,
                TerritoryName = first.TerritoryName,
                ContentId = first.ContentId,
                ContentName = first.ContentName,
                Events = ordered,
            },
        };
    }
}

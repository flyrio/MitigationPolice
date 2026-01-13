// 本文件实现主界面：按机制聚合、事件列表与单次详情（含复制/发小队）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MitigationPolice.Chat;
using MitigationPolice.Models;

namespace MitigationPolice.UI;

public sealed class MainWindow : Window {
    private readonly MitigationPolicePlugin plugin;

    private PoliceMode policeMode = PoliceMode.Team;

    private bool onlyShowMissing;
    private bool onlyShowFatal;
    private string mechanicSearch = string.Empty;

    private MechanicKey? selectedMechanic;
    private Guid? selectedEventId;

    private Guid? selectedSessionId;

    private float focusTimeSeconds;
    private bool timeFilterEnabled;
    private float timeFilterWindowSeconds = 5f;
    private bool scrollToSelectedEvent;

    private string lastCopyMessage = string.Empty;
    private string lastSendMessage = string.Empty;

    private enum PoliceMode {
        Team,
        Personal,
    }

    public MainWindow(MitigationPolicePlugin plugin) : base("减伤警察") {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(900, 500),
        };
    }

    public override void Draw() {
        var summaries = plugin.EventStore.GetSessionSummaries();
        if (selectedSessionId == null || summaries.All(s => s.Id != selectedSessionId.Value)) {
            selectedSessionId = summaries.FirstOrDefault().Id;
            selectedMechanic = null;
            selectedEventId = null;
        }

        var selectedSummary = selectedSessionId.HasValue
            ? plugin.EventStore.TryGetSummary(selectedSessionId.Value)
            : null;

        DrawHeader(summaries, selectedSummary);

        var sessionStartUtc = selectedSummary?.StartUtc ?? DateTimeOffset.UtcNow;
        var allRecords = selectedSessionId.HasValue
            ? plugin.EventStore.GetSessionEventsSnapshot(selectedSessionId.Value)
            : new List<DamageEventRecord>();

        if (allRecords.Count == 0) {
            ImGui.Separator();
            ImGui.Text("暂无记录（仅记录战斗开始后的队伍受伤事件；脱战后会存为一场战斗）");
            return;
        }

        ImGui.Separator();

        var records = ApplyFilters(allRecords, sessionStartUtc).ToList();
        if (records.Count == 0) {
            ImGui.Text("当前筛选条件下无事件");
            return;
        }

        if (ImGui.BeginTable("##mp_layout", 3, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV)) {
            ImGui.TableSetupColumn("机制", ImGuiTableColumnFlags.WidthStretch, 0.33f);
            ImGui.TableSetupColumn("事件", ImGuiTableColumnFlags.WidthStretch, 0.34f);
            ImGui.TableSetupColumn("详情", ImGuiTableColumnFlags.WidthStretch, 0.33f);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawMechanicList(records);

            ImGui.TableNextColumn();
            DrawEventList(records, allRecords, sessionStartUtc);

            ImGui.TableNextColumn();
            DrawEventDetail(allRecords, sessionStartUtc);

            ImGui.EndTable();
        }
    }

    private void DrawHeader(IReadOnlyList<CombatSessionSummary> sessions, CombatSessionSummary? selectedSummary) {
        var dutyName = selectedSummary != null
            ? FormatDutyName(selectedSummary.Value.TerritoryId, selectedSummary.Value.TerritoryName, selectedSummary.Value.ContentName)
            : FormatDutyNameFromCurrent();

        ImGui.Text($"副本: {dutyName}");

        if (selectedSummary != null) {
            ImGui.SameLine();
            var duration = FormatDuration(selectedSummary.Value.StartUtc, selectedSummary.Value.EndUtc);
            ImGui.Text($"| 战斗: {selectedSummary.Value.StartUtc.ToLocalTime():HH:mm:ss} {duration}");

            ImGui.SameLine();
            ImGui.Text($"| 事件: {selectedSummary.Value.EventCount}");

            ImGui.SameLine();
            if (selectedSummary.Value.FatalCount > 0) {
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"| 致死: {selectedSummary.Value.FatalCount}");
            } else {
                ImGui.Text("| 致死: 0");
            }
        }

        ImGui.SameLine();
        var allow = plugin.MitigationState.ShouldCapturePackets ? "启用" : "未启用";
        ImGui.Text($"| 捕获: {allow}");

        ImGui.SameLine();
        ImGui.Text($"| 历史战斗: {plugin.EventStore.TotalSessions}");

        ImGui.Separator();

        var currentSessionLabel = selectedSummary != null
            ? FormatSessionLabel(selectedSummary.Value)
            : "无";
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("战斗记录", currentSessionLabel)) {
            foreach (var s in sessions.OrderByDescending(x => x.StartUtc)) {
                ImGui.PushID(s.Id.ToString());
                var label = FormatSessionLabel(s);
                var selected = selectedSessionId.HasValue && selectedSessionId.Value == s.Id;
                if (ImGui.Selectable(label, selected)) {
                    selectedSessionId = s.Id;
                    selectedMechanic = null;
                    selectedEventId = null;
                    scrollToSelectedEvent = false;
                }
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.Checkbox("仅缺失", ref onlyShowMissing);

        ImGui.SameLine();
        ImGui.Checkbox("仅致死", ref onlyShowFatal);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        var modeLabel = policeMode == PoliceMode.Team ? "团队减伤" : "个人减伤";
        if (ImGui.BeginCombo("警察模式", modeLabel)) {
            if (ImGui.Selectable("团队减伤", policeMode == PoliceMode.Team)) {
                policeMode = PoliceMode.Team;
                selectedMechanic = null;
                selectedEventId = null;
                scrollToSelectedEvent = false;
            }

            if (ImGui.Selectable("个人减伤", policeMode == PoliceMode.Personal)) {
                policeMode = PoliceMode.Personal;
                selectedMechanic = null;
                selectedEventId = null;
                scrollToSelectedEvent = false;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputText("机制搜索", ref mechanicSearch, 64);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.InputFloat("T+(秒)", ref focusTimeSeconds, 0.5f, 5f, "%.1f");

        ImGui.SameLine();
        if (ImGui.Button("定位")) {
            focusTimeSeconds = Math.Max(0, focusTimeSeconds);
            timeFilterEnabled = true;
            scrollToSelectedEvent = true;
            selectedMechanic = null;
            selectedEventId = null;
        }

        ImGui.SameLine();
        ImGui.Checkbox("按时间筛选(±)", ref timeFilterEnabled);

        if (timeFilterEnabled) {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            ImGui.InputFloat("窗口(秒)", ref timeFilterWindowSeconds, 0.5f, 5f, "%.1f");
        }

        ImGui.SameLine();
        if (ImGui.Button("设置")) {
            plugin.ConfigWindow.Toggle();
        }

        ImGui.SameLine();
        if (ImGui.Button("清空记录")) {
            plugin.EventStore.ClearAll();
            selectedSessionId = null;
            selectedMechanic = null;
            selectedEventId = null;
            lastCopyMessage = string.Empty;
            lastSendMessage = "已清空记录";
        }

        if (!string.IsNullOrWhiteSpace(lastSendMessage)) {
            ImGui.Text(lastSendMessage);
        }
    }

    private void DrawMechanicList(IReadOnlyList<DamageEventRecord> records) {
        ImGui.Text("机制列表");
        ImGui.Separator();

        var query = mechanicSearch.Trim();

        var mechanics = records
            .GroupBy(r => new MechanicKey(
                r.SourceName ?? string.Empty,
                r.ActionId ?? 0,
                r.ActionName ?? string.Empty))
            .Select(g => new MechanicSummary {
                Key = g.Key,
                Hits = g.Count(),
                TotalDamage = g.Sum(x => (long)x.DamageAmount),
                MaxHit = g.Max(x => (long)x.DamageAmount),
                MissingHits = g.Count(x => GetMissingCountForMode(x) > 0),
                FatalHits = g.Count(x => x.IsFatal),
                LastUtc = g.Max(x => x.TimestampUtc),
            })
            .Where(s => !onlyShowMissing || s.MissingHits > 0)
            .Where(s => string.IsNullOrWhiteSpace(query) || s.Key.ToDisplayName().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.TotalDamage)
            .Take(300)
            .ToList();

        if (mechanics.Count == 0) {
            ImGui.Text("无匹配机制");
            return;
        }

        if (ImGui.BeginTable("##mp_mechanics", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders)) {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("机制");
            ImGui.TableSetupColumn("次数", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("总伤害", ImGuiTableColumnFlags.WidthFixed, 90);
            var suffix = policeMode == PoliceMode.Team ? "(团)" : "(个)";
            ImGui.TableSetupColumn($"缺失{suffix}", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("致死", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selectedAll = selectedMechanic == null;
            if (ImGui.Selectable("（全部机制）", selectedAll, ImGuiSelectableFlags.SpanAllColumns)) {
                selectedMechanic = null;
                selectedEventId = null;
            }
            ImGui.TableNextColumn();
            ImGui.Text(records.Count.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(records.Sum(x => (long)x.DamageAmount).ToString());
            ImGui.TableNextColumn();
            var missingAll = records.Count(x => GetMissingCountForMode(x) > 0);
            if (missingAll > 0) {
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), missingAll.ToString());
            } else {
                ImGui.Text("0");
            }
            ImGui.TableNextColumn();
            var fatalAll = records.Count(x => x.IsFatal);
            if (fatalAll > 0) {
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), fatalAll.ToString());
            } else {
                ImGui.Text("0");
            }

            foreach (var item in mechanics) {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                var selected = selectedMechanic.HasValue && selectedMechanic.Value.Equals(item.Key);
                if (ImGui.Selectable(item.Key.ToDisplayName(), selected, ImGuiSelectableFlags.SpanAllColumns)) {
                    selectedMechanic = item.Key;
                    selectedEventId = null;
                }

                ImGui.TableNextColumn();
                ImGui.Text(item.Hits.ToString());

                ImGui.TableNextColumn();
                ImGui.Text(item.TotalDamage.ToString());

                ImGui.TableNextColumn();
                if (item.MissingHits > 0) {
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), item.MissingHits.ToString());
                } else {
                    ImGui.Text("0");
                }

                ImGui.TableNextColumn();
                if (item.FatalHits > 0) {
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), item.FatalHits.ToString());
                } else {
                    ImGui.Text("0");
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawEventList(IReadOnlyList<DamageEventRecord> records, IReadOnlyList<DamageEventRecord> allRecords, DateTimeOffset sessionStartUtc) {
        ImGui.Text("事件列表");
        ImGui.Separator();

        List<DamageEventRecord> events;
        var showAll = selectedMechanic is null;

        if (selectedMechanic is { } selectedKey) {
            events = records
                .Where(r => (r.SourceName ?? string.Empty) == selectedKey.SourceName &&
                            (r.ActionId ?? 0) == selectedKey.ActionId)
                .OrderBy(r => r.TimestampUtc)
                .Take(600)
                .ToList();
        } else {
            events = records
                .OrderBy(r => r.TimestampUtc)
                .Take(800)
                .ToList();
        }

        if (events.Count == 0) {
            ImGui.Text("无事件");
            return;
        }

        if (scrollToSelectedEvent && selectedEventId == null) {
            selectedEventId = FindNearestEventId(allRecords, sessionStartUtc, focusTimeSeconds);
        }

        if (selectedEventId == null || events.All(e => e.Id != selectedEventId.Value)) {
            selectedEventId = events[0].Id;
        }

        var columnCount = showAll ? 6 : 5;
        if (ImGui.BeginTable("##mp_events", columnCount, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders)) {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("T+", ImGuiTableColumnFlags.WidthFixed, 62);
            if (showAll) {
                ImGui.TableSetupColumn("机制");
            }
            ImGui.TableSetupColumn("目标");
            ImGui.TableSetupColumn("伤害", ImGuiTableColumnFlags.WidthFixed, 70);
            var suffix = policeMode == PoliceMode.Team ? "(团)" : "(个)";
            ImGui.TableSetupColumn($"已交{suffix}", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn($"缺失{suffix}", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableHeadersRow();

            for (var index = 0; index < events.Count; index++) {
                var e = events[index];
                ImGui.PushID(e.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                var selected = selectedEventId.HasValue && selectedEventId.Value == e.Id;
                var timeText = FormatOffsetSeconds((e.TimestampUtc - sessionStartUtc).TotalSeconds);
                if (e.IsFatal) {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f));
                }
                if (ImGui.Selectable(timeText, selected, ImGuiSelectableFlags.SpanAllColumns)) {
                    selectedEventId = e.Id;
                    lastCopyMessage = string.Empty;
                    lastSendMessage = string.Empty;
                }
                if (e.IsFatal) {
                    ImGui.PopStyleColor();
                }
                if (selected && ImGui.IsItemHovered()) {
                    ImGui.SetTooltip(e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"));
                }

                if (scrollToSelectedEvent && selected) {
                    ImGui.SetScrollHereY(0.5f);
                    scrollToSelectedEvent = false;
                }

                if (showAll) {
                    ImGui.TableNextColumn();
                    var key = new MechanicKey(e.SourceName ?? string.Empty, e.ActionId ?? 0, e.ActionName ?? string.Empty);
                    ImGui.Text(key.ToDisplayName());
                }

                ImGui.TableNextColumn();
                ImGui.Text($"{e.TargetName} ({e.TargetJob})");

                ImGui.TableNextColumn();
                ImGui.Text(e.DamageAmount.ToString());

                ImGui.TableNextColumn();
                ImGui.Text(GetActiveCountForMode(e).ToString());

                ImGui.TableNextColumn();
                var missingCount = GetMissingCountForMode(e);
                if (missingCount > 0) {
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), missingCount.ToString());
                } else {
                    ImGui.Text("0");
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawEventDetail(IReadOnlyList<DamageEventRecord> allRecords, DateTimeOffset sessionStartUtc) {
        ImGui.Text("事件详情");
        ImGui.Separator();

        if (selectedEventId == null) {
            ImGui.Text("请先选择事件");
            return;
        }

        var record = allRecords.FirstOrDefault(r => r.Id == selectedEventId.Value);
        if (record == null) {
            ImGui.Text("事件已不存在");
            return;
        }

        var offsetSeconds = (record.TimestampUtc - sessionStartUtc).TotalSeconds;
        var timeLabel = $"T+{FormatOffsetSeconds(offsetSeconds)} | {record.TimestampUtc.ToLocalTime():HH:mm:ss}";
        if (record.IsFatal) {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"致死 | {timeLabel}");
        } else {
            ImGui.Text($"时间: {timeLabel}");
        }

        ImGui.Text($"{record.SourceName}:{record.ActionName} -> {record.TargetName}");
        ImGui.Text($"伤害: {record.DamageAmount} | 类型: {record.DamageType}");
        if (record.IsFatal) {
            if (record.TargetHpBefore == 0 && record.TargetShieldBefore == 0) {
                ImGui.Text("死亡详情: 生前血量/盾量未知");
            } else {
                var total = (ulong)record.TargetHpBefore + (ulong)record.TargetShieldBefore;
                var overkill = (ulong)record.DamageAmount > total ? (ulong)record.DamageAmount - total : 0UL;
                ImGui.Text($"死亡详情: 生前血{record.TargetHpBefore} 生前盾{record.TargetShieldBefore} 致死伤害{record.DamageAmount} 过量{overkill}");
            }
        }
        ImGui.Separator();

        ImGui.Text("已交减伤（命中时仍在持续）");
        if (record.ActiveMitigations.Count == 0) {
            ImGui.TextDisabled("无");
        } else {
            foreach (var m in record.ActiveMitigations.OrderByDescending(x => x.RemainingSeconds)) {
                ImGui.Text($"{m.MitigationName} 贡献者:{m.CasterName} 剩余:{Math.Max(0, (int)Math.Round(m.RemainingSeconds))}s");
            }
        }

        ImGui.Separator();
        ImGui.Text("缺失减伤（可用未交）");
        var missingPersonal = record.MissingMitigations.Where(m => IsPersonalMitigation(m.MitigationId)).OrderByDescending(x => x.AvailableForSeconds).ToList();
        var missingOther = record.MissingMitigations.Where(m => !IsPersonalMitigation(m.MitigationId)).OrderByDescending(x => x.AvailableForSeconds).ToList();

        if (ImGui.BeginTable("##mp_missing_split", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable)) {
            ImGui.TableSetupColumn("缺失团队/敌方");
            ImGui.TableSetupColumn("缺失个人");
            ImGui.TableHeadersRow();

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (missingOther.Count == 0) {
                ImGui.TextDisabled("无");
            } else {
                foreach (var m in missingOther) {
                    var label = m.NeverUsedSinceDutyStart ? "开场" : "转好";
                    ImGui.Text($"{m.MitigationName} 责任:{m.OwnerName}({m.OwnerJob}) {label}:{(int)Math.Round(m.AvailableForSeconds)}s");
                }
            }

            ImGui.TableNextColumn();
            if (missingPersonal.Count == 0) {
                ImGui.TextDisabled("无");
            } else {
                foreach (var m in missingPersonal) {
                    var label = m.NeverUsedSinceDutyStart ? "开场" : "转好";
                    ImGui.Text($"{m.MitigationName} 责任:{m.OwnerName}({m.OwnerJob}) {label}:{(int)Math.Round(m.AvailableForSeconds)}s");
                }
            }

            ImGui.EndTable();
        } else {
            if (record.MissingMitigations.Count == 0) {
                ImGui.TextDisabled("无");
            } else {
                foreach (var m in record.MissingMitigations.OrderByDescending(x => x.AvailableForSeconds)) {
                    var label = m.NeverUsedSinceDutyStart ? "开场" : "转好";
                    ImGui.Text($"{m.MitigationName} 责任:{m.OwnerName}({m.OwnerJob}) {label}:{(int)Math.Round(m.AvailableForSeconds)}s");
                }
            }
        }

        ImGui.Separator();
        DrawOverwrites(record);

        ImGui.Separator();
        DrawShareControls(record, sessionStartUtc);
    }

    private void DrawOverwrites(DamageEventRecord record) {
        ImGui.Text("冲突减伤（顶掉/覆盖）");

        var list = plugin.MitigationState.GetOverwritesForEvent(record);
        if (list.Count == 0) {
            ImGui.TextDisabled("无");
            return;
        }

        foreach (var o in list) {
            var time = o.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

            var applied = o.AppliedActorName;
            if (string.IsNullOrWhiteSpace(applied)) {
                if (o.AppliedActorId == record.TargetId) {
                    applied = record.TargetName;
                } else if (record.SourceId.HasValue && o.AppliedActorId == record.SourceId.Value) {
                    applied = record.SourceName ?? "来源";
                } else {
                    applied = o.AppliedActorId.ToString();
                }
            }

            var remaining = Math.Max(0, (int)Math.Round(o.OldRemainingSeconds));
            var isRefresh = o.OldCasterId == o.NewCasterId &&
                            string.Equals(o.OldMitigationId, o.NewMitigationId, StringComparison.OrdinalIgnoreCase);

            if (isRefresh) {
                ImGui.Text($"{time} [{applied}] {o.NewMitigationName}@{o.NewCasterName} 刷新 (旧剩{remaining}s)");
            } else {
                ImGui.Text($"{time} [{applied}] {o.NewMitigationName}@{o.NewCasterName} 顶 {o.OldMitigationName}@{o.OldCasterName} (旧剩{remaining}s)");
            }
        }
    }

    private void DrawShareControls(DamageEventRecord record, DateTimeOffset sessionStartUtc) {
        ImGui.Text("分享");

        var overwrites = plugin.MitigationState.GetOverwritesForEvent(record);
        var prefix = $"[T+{FormatShareOffsetSeconds((record.TimestampUtc - sessionStartUtc).TotalSeconds)}] ";

        var copyLines = ShareFormatter.BuildCopyLines(record, overwrites);
        var copyText = copyLines.Count == 0
            ? prefix + ShareFormatter.BuildCopyText(record, overwrites)
            : prefix + string.Join("\n", new[] { copyLines[0] }.Concat(copyLines.Skip(1)));

        var rawPartyLines = ShareFormatter.BuildPartyLines(record, overwrites, 320);
        var partyLines = new List<string>(rawPartyLines.Count);
        var prefixApplied = false;
        for (var i = 0; i < rawPartyLines.Count; i++) {
            var line = rawPartyLines[i];
            if (!prefixApplied && !ShareFormatter.IsSeparatorLine(line)) {
                line = prefix + line;
                prefixApplied = true;
            }
            partyLines.Add(Utf8Util.Truncate(line, 480));
        }

        if (ImGui.Button("复制详情")) {
            ImGui.SetClipboardText(copyText);
            lastCopyMessage = "已复制到剪贴板";
        }

        ImGui.SameLine();
        var canSend = plugin.ChatSender.CanSend;
        if (!canSend) {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("播报（默语）")) {
            if (plugin.ChatSender.TrySendEchoMessages(partyLines, out var err)) {
                lastSendMessage = $"已发送到默语（{partyLines.Count} 行）";

                if (plugin.Configuration.AllowSendingToPartyChat && HasPartyMembersBesidesSelf()) {
                    if (plugin.ChatSender.TrySendPartyMessages(partyLines, out var partyErr)) {
                        lastSendMessage += "，并已发送到小队频道";
                    } else {
                        lastSendMessage += $"，发送到小队失败：{partyErr ?? "未知错误"}";
                    }
                }
            } else {
                lastSendMessage = err ?? "发送失败";
            }
        }
        if (!canSend) {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip(plugin.ChatSender.LastError ?? "聊天发送不可用");
            }
        }

        if (!string.IsNullOrWhiteSpace(lastCopyMessage)) {
            ImGui.Text(lastCopyMessage);
        }

        ImGui.Separator();
        ImGui.TextDisabled("发送预览（逐行发送）：");
        ImGui.BeginChild("##mp_share_preview", new Vector2(0, 90), true, ImGuiWindowFlags.AlwaysVerticalScrollbar);
        foreach (var line in partyLines) {
            ImGui.TextUnformatted(line);
        }
        ImGui.EndChild();
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

    private readonly record struct MechanicKey(string SourceName, uint ActionId, string ActionName) {
        public string ToDisplayName() {
            var source = string.IsNullOrWhiteSpace(SourceName) ? "未知来源" : SourceName;
            var action = string.IsNullOrWhiteSpace(ActionName) ? (ActionId == 0 ? "未知技能" : ActionId.ToString()) : ActionName;
            return $"{source}:{action}";
        }
    }

    private sealed class MechanicSummary {
        public MechanicKey Key { get; init; }
        public int Hits { get; init; }
        public long TotalDamage { get; init; }
        public long MaxHit { get; init; }
        public int MissingHits { get; init; }
        public int FatalHits { get; init; }
        public DateTimeOffset LastUtc { get; init; }
    }

    private IEnumerable<DamageEventRecord> ApplyFilters(IReadOnlyList<DamageEventRecord> records, DateTimeOffset sessionStartUtc) {
        var query = mechanicSearch.Trim();

        foreach (var r in records) {
            if (onlyShowMissing && GetMissingCountForMode(r) == 0) {
                continue;
            }

            if (onlyShowFatal && !r.IsFatal) {
                continue;
            }

            if (timeFilterEnabled) {
                var t = (float)(r.TimestampUtc - sessionStartUtc).TotalSeconds;
                if (Math.Abs(t - focusTimeSeconds) > Math.Max(0.5f, timeFilterWindowSeconds)) {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(query)) {
                var key = new MechanicKey(r.SourceName ?? string.Empty, r.ActionId ?? 0, r.ActionName ?? string.Empty);
                if (!key.ToDisplayName().Contains(query, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
            }

            yield return r;
        }
    }

    private static Guid? FindNearestEventId(IReadOnlyList<DamageEventRecord> records, DateTimeOffset sessionStartUtc, float seconds) {
        if (records.Count == 0) {
            return null;
        }

        var target = Math.Max(0, seconds);
        var ordered = records.OrderBy(r => r.TimestampUtc).ToList();
        foreach (var r in ordered) {
            var t = (float)(r.TimestampUtc - sessionStartUtc).TotalSeconds;
            if (t >= target) {
                return r.Id;
            }
        }

        return ordered[^1].Id;
    }

    private static string FormatOffsetSeconds(double seconds) {
        if (seconds < 0) {
            seconds = 0;
        }

        if (seconds < 60) {
            return $"{seconds:0.0}s";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
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

    private static string FormatDuration(DateTimeOffset startUtc, DateTimeOffset? endUtc) {
        var end = endUtc ?? DateTimeOffset.UtcNow;
        var ts = end - startUtc;
        if (ts.TotalMinutes >= 1) {
            return $"({(int)ts.TotalMinutes}m{ts.Seconds:D2}s)";
        }

        return $"({Math.Max(0, (int)Math.Round(ts.TotalSeconds))}s)";
    }

    private static string FormatDutyName(uint territoryId, string territoryName, string? contentName) {
        var dutyName = !string.IsNullOrWhiteSpace(contentName)
            ? $"{contentName} ({territoryName})"
            : territoryName;

        if (string.IsNullOrWhiteSpace(dutyName)) {
            dutyName = $"区域 {territoryId}";
        }

        return dutyName;
    }

    private string FormatDutyNameFromCurrent() {
        var duty = plugin.MitigationState.ResolveDutyContext();
        return FormatDutyName(duty.TerritoryId, duty.TerritoryName, duty.ContentName);
    }

    private static string FormatSessionLabel(CombatSessionSummary s) {
        var duty = FormatDutyName(s.TerritoryId, s.TerritoryName, s.ContentName);
        var duration = FormatDuration(s.StartUtc, s.EndUtc);
        var fatal = s.FatalCount > 0 ? $" 致死{s.FatalCount}" : string.Empty;
        return $"{s.StartUtc.ToLocalTime():MM-dd HH:mm:ss} {duration} | {duty} | 事件{s.EventCount}{fatal}";
    }

    private int GetActiveCountForMode(DamageEventRecord record) {
        var count = 0;
        foreach (var m in record.ActiveMitigations) {
            var isPersonal = IsPersonalMitigation(m.MitigationId);
            if (policeMode == PoliceMode.Personal ? isPersonal : !isPersonal) {
                count++;
            }
        }

        return count;
    }

    private int GetMissingCountForMode(DamageEventRecord record) {
        var count = 0;
        foreach (var m in record.MissingMitigations) {
            var isPersonal = IsPersonalMitigation(m.MitigationId);
            if (policeMode == PoliceMode.Personal ? isPersonal : !isPersonal) {
                count++;
            }
        }

        return count;
    }

    private bool IsPersonalMitigation(string mitigationId) {
        foreach (var def in plugin.Configuration.Mitigations) {
            if (string.Equals(def.Id, mitigationId, StringComparison.OrdinalIgnoreCase)) {
                return def.Category == MitigationCategory.Personal;
            }
        }

        return false;
    }
}

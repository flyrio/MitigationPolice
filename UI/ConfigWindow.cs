// 本文件实现配置界面：捕获范围、追责规则、默认清单与自定义减伤增删。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MitigationPolice.Chat;
using MitigationPolice.Mitigations;
using ActionSheet = Lumina.Excel.Sheets.Action;
using MitigationPolice.Models;

namespace MitigationPolice.UI;

public sealed class ConfigWindow : Window {
    private const int MaxSearchResults = 200;

    private readonly MitigationPolicePlugin plugin;

    private string actionSearch = string.Empty;
    private readonly List<ActionSearchEntry> actionSearchResults = new();
    private string actionSearchMessage = string.Empty;

    private readonly List<JobIds> newMitigationJobs = new();

    private readonly Dictionary<string, string> triggerTextById = new();

    private bool autoAnnouncePreviewInitialized;
    private string deathAutoAnnouncePreview = string.Empty;
    private string overwriteAutoAnnouncePreview = string.Empty;
    private DateTime lastAutoAnnouncePreviewUtc = DateTime.MinValue;

    public ConfigWindow(MitigationPolicePlugin plugin) : base("减伤巡查 - 设置") {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(720, 500),
        };
    }

    public override void Draw() {
        ImGui.BeginChild("##mp_config_scroll", new Vector2(0, 0), false, ImGuiWindowFlags.AlwaysVerticalScrollbar);

        if (ImGui.CollapsingHeader("基础设置", ImGuiTreeNodeFlags.DefaultOpen)) {
            DrawGeneralSettings();
        }

        ImGui.Separator();

        if (ImGui.BeginTabBar("##mp_config_tabs")) {
            if (ImGui.BeginTabItem("减伤清单")) {
                DrawMitigationList();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("添加减伤")) {
                DrawAddMitigation();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.EndChild();
    }

    private void DrawGeneralSettings() {
        ImGui.Text("功能说明");
        ImGui.BulletText("复盘：按战斗会话记录队伍受伤事件");
        ImGui.BulletText("分析：命中生效减伤 / 缺失减伤 / 已放未覆盖 / 顶减伤");
        ImGui.BulletText("手动：主界面可复制与播报（默认仅默语，可选同步小队）");
        ImGui.BulletText("自动：死亡/顶减伤可自动通报（默认关闭）");
        ImGui.Separator();

        ImGui.Text("捕获范围");
        ImGui.TextDisabled("仅记录战斗中队伍成员的受伤事件；脱战后会保存为一场战斗记录。");
        var trackOnlyInInstances = plugin.Configuration.TrackOnlyInInstances;
        if (ImGui.Checkbox("仅副本内捕获", ref trackOnlyInInstances)) {
            plugin.Configuration.TrackOnlyInInstances = trackOnlyInInstances;
            SaveAndReload();
        }

        ImGui.Separator();
        ImGui.Text("追责范围");
        ImGui.TextDisabled("决定“缺失减伤”统计包含哪些类型（未到学会等级/等级同步不足会自动不追责）。");
        var includePersonal = plugin.Configuration.IncludePersonalMitigations;
        if (ImGui.Checkbox("追责：个人减伤", ref includePersonal)) {
            plugin.Configuration.IncludePersonalMitigations = includePersonal;
            SaveAndReload();
        }

        var includeParty = plugin.Configuration.IncludePartyMitigations;
        if (ImGui.Checkbox("追责：团队减伤", ref includeParty)) {
            plugin.Configuration.IncludePartyMitigations = includeParty;
            SaveAndReload();
        }

        var includeEnemy = plugin.Configuration.IncludeEnemyMitigations;
        if (ImGui.Checkbox("追责：敌方减伤（雪仇/昏乱/牵制/武装解除）", ref includeEnemy)) {
            plugin.Configuration.IncludeEnemyMitigations = includeEnemy;
            SaveAndReload();
        }

        ImGui.Separator();
        ImGui.Text("追责规则");
        ImGui.TextDisabled("“开场未交/转好未交/已放未覆盖”等判断会结合战斗开始时间与技能冷却推算。");
        var assumeReady = plugin.Configuration.AssumeReadyAtDutyStart;
        if (ImGui.Checkbox("默认视为开战时已转好（用于“开场未交”）", ref assumeReady)) {
            plugin.Configuration.AssumeReadyAtDutyStart = assumeReady;
            SaveAndReload();
        }

        var minDamage = plugin.Configuration.MinDamageToAnalyzeMissing;
        if (ImGui.InputInt("仅对伤害>=X 的事件追责(0=全部)", ref minDamage)) {
            plugin.Configuration.MinDamageToAnalyzeMissing = Math.Max(0, minDamage);
            SaveAndReload();
        }

        ImGui.Separator();
        ImGui.Text("数据存储");
        ImGui.TextDisabled("仅影响本地保存的战斗事件数量（越大占用越高）。");
        var maxEvents = plugin.Configuration.MaxStoredEvents;
        if (ImGui.InputInt("最大保存事件数（超出将移除最早战斗）", ref maxEvents)) {
            plugin.Configuration.MaxStoredEvents = Math.Clamp(maxEvents, 100, 200_000);
            plugin.Configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("通报与频道");
        ImGui.TextDisabled("自动通报默认关闭；开启前建议先看下方预览，避免刷屏。");
        var allowSend = plugin.Configuration.AllowSendingToPartyChat;
        if (ImGui.Checkbox("同时发送到小队频道（默认仅默语）", ref allowSend)) {
            plugin.Configuration.AllowSendingToPartyChat = allowSend;
            plugin.Configuration.Save();
        }

        ImGui.TextDisabled("提示：默认所有播报/通报只发默语(/e)；勾选上方开关后会额外发小队(/p)。");

        var autoAnnounce = plugin.Configuration.AutoAnnounceOverwritesToPartyChat;
        if (ImGui.Checkbox("自动通报：发生冲突减伤（顶掉/覆盖）时播报", ref autoAnnounce)) {
            plugin.Configuration.AutoAnnounceOverwritesToPartyChat = autoAnnounce;
            plugin.Configuration.Save();
        }

        var autoAnnounceDeath = plugin.Configuration.AutoAnnounceDeathsToPartyChat;
        if (ImGui.Checkbox("自动通报：有人死亡时播报减伤巡查信息", ref autoAnnounceDeath)) {
            plugin.Configuration.AutoAnnounceDeathsToPartyChat = autoAnnounceDeath;
            plugin.Configuration.Save();
        }

        if (autoAnnounce || autoAnnounceDeath) {
            if (!plugin.Configuration.AllowSendingToPartyChat) {
                ImGui.TextDisabled("提示：自动通报默认发送到默语；如需同步到小队，请开启“同时发送到小队频道”。");
            } else {
                ImGui.TextDisabled("提示：已开启同步到小队频道，注意可能刷屏。");
            }
        }

        DrawAutoAnnouncePreview();
    }

    private void DrawAutoAnnouncePreview() {
        ImGui.Separator();
        ImGui.Text("自动通报预览");
        ImGui.TextDisabled("预览会尝试读取最近一次记录（致死/顶减伤）；没有对应记录时会显示示例。");

        if (!autoAnnouncePreviewInitialized) {
            autoAnnouncePreviewInitialized = true;
            RefreshAutoAnnouncePreview();
        }

        if (ImGui.Button("刷新预览")) {
            RefreshAutoAnnouncePreview();
        }
        ImGui.SameLine();
        if (ImGui.Button("使用示例")) {
            SetSampleAutoAnnouncePreview();
        }

        if (lastAutoAnnouncePreviewUtc != DateTime.MinValue) {
            ImGui.SameLine();
            ImGui.TextDisabled($"更新时间：{lastAutoAnnouncePreviewUtc.ToLocalTime():HH:mm:ss}");
        }

        ImGui.TextDisabled("死亡自动通报：");
        ImGui.BeginChild("##mp_auto_death_preview", new Vector2(0, 65), true, ImGuiWindowFlags.AlwaysVerticalScrollbar | ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.TextUnformatted(deathAutoAnnouncePreview);
        ImGui.EndChild();

        ImGui.TextDisabled("顶减伤自动通报：");
        ImGui.BeginChild("##mp_auto_overwrite_preview", new Vector2(0, 65), true, ImGuiWindowFlags.AlwaysVerticalScrollbar | ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.TextUnformatted(overwriteAutoAnnouncePreview);
        ImGui.EndChild();
    }

    private void RefreshAutoAnnouncePreview() {
        SetSampleAutoAnnouncePreview();

        var sessionId = plugin.EventStore.ActiveSessionId ?? plugin.EventStore.GetMostRecentSessionId();
        if (!sessionId.HasValue) {
            lastAutoAnnouncePreviewUtc = DateTime.UtcNow;
            return;
        }

        var summary = plugin.EventStore.TryGetSummary(sessionId.Value);
        var sessionStartUtc = summary?.StartUtc ?? DateTimeOffset.UtcNow;

        var events = plugin.EventStore.GetSessionEventsSnapshot(sessionId.Value);
        if (events.Count == 0) {
            lastAutoAnnouncePreviewUtc = DateTime.UtcNow;
            return;
        }

        for (var i = events.Count - 1; i >= 0; i--) {
            var record = events[i];
            if (!record.IsFatal) {
                continue;
            }

            var prefix = $"[T+{FormatShareOffsetSeconds((record.TimestampUtc - sessionStartUtc).TotalSeconds)}] ";
            deathAutoAnnouncePreview = Utf8Util.Truncate(prefix + ShareFormatter.BuildDeathPoliceTipLine(record), 480);
            break;
        }

        for (var i = events.Count - 1; i >= 0; i--) {
            var list = plugin.MitigationState.GetOverwritesForEvent(events[i]);
            if (list.Count == 0) {
                continue;
            }

            var batch = TakeLatestOverwriteBatch(list);
            var message = AutoAnnounceFormatter.BuildOverwriteAnnouncementMessage(batch);
            if (!string.IsNullOrWhiteSpace(message)) {
                overwriteAutoAnnouncePreview = message;
                break;
            }
        }

        lastAutoAnnouncePreviewUtc = DateTime.UtcNow;
    }

    private void SetSampleAutoAnnouncePreview() {
        deathAutoAnnouncePreview = "占星 Yumoki被强风 2629368点魔法伤害做掉了！生前血量：110023，生前盾量：0，致死伤害：2629368，过量：2519345，减伤百分比：19%，状态：怒涛之计 命运之轮";
        overwriteAutoAnnouncePreview = "顶减伤：雪仇@骑士A顶雪仇@骑士B(旧剩10s)";
    }

    private static List<MitigationOverwrite> TakeLatestOverwriteBatch(List<MitigationOverwrite> list) {
        if (list.Count <= 1) {
            return list;
        }

        var latestUtc = list.Max(o => o.TimestampUtc.UtcDateTime);
        var cutoff = latestUtc.AddMilliseconds(-750);
        return list.Where(o => o.TimestampUtc.UtcDateTime >= cutoff).ToList();
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

    private void DrawMitigationList() {
        ImGui.Text("减伤清单");
        ImGui.TextDisabled("该清单用于：命中生效减伤/缺失减伤追责/已放未覆盖/顶掉与互斥判断。可按需禁用或自定义持续/冷却/触发。");

        var defs = plugin.Configuration.Mitigations;
        if (defs.Count == 0) {
            ImGui.TextDisabled("当前清单为空");
            return;
        }

        if (ImGui.Button("重置为默认清单")) {
            plugin.Configuration.Mitigations = DefaultMitigationLibrary.Build();
            SaveAndReload();
            triggerTextById.Clear();
            return;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("提示：触发(ActionId) 支持逗号分隔；职业为空会被视为“所有职业”");

        var style = ImGui.GetStyle();
        var numberWidth = ImGui.CalcTextSize("000000.0").X + style.FramePadding.X * 2 + 24f;

        if (ImGui.BeginTable("##mp_mitigations", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable)) {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("分类", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("生效", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("触发(ActionId)", ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn("持续(s)", ImGuiTableColumnFlags.WidthFixed, numberWidth);
            ImGui.TableSetupColumn("CD(s)", ImGuiTableColumnFlags.WidthFixed, numberWidth);
            ImGui.TableSetupColumn("职业", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            for (var i = 0; i < defs.Count; i++) {
                var def = defs[i];

                ImGui.PushID(def.Id);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var enabled = def.Enabled;
                if (ImGui.Checkbox("##enabled", ref enabled)) {
                    def.Enabled = enabled;
                    SaveAndReload();
                }

                ImGui.TableNextColumn();
                var name = def.Name ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##name", ref name, 128) && ImGui.IsItemDeactivatedAfterEdit()) {
                    def.Name = name.Trim();
                    SaveAndReload();
                }

                ImGui.TableNextColumn();
                if (DrawCategoryCombo(def)) {
                    plugin.Configuration.Save();
                }

                ImGui.TableNextColumn();
                if (DrawApplyToCombo(def)) {
                    plugin.Configuration.Save();
                }

                ImGui.TableNextColumn();
                var triggerText = GetOrCreateTriggerText(def);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##triggers", ref triggerText, 256) && ImGui.IsItemDeactivatedAfterEdit()) {
                    triggerTextById[def.Id] = triggerText;
                    var parsed = ParseActionIdList(triggerText);
                    if (parsed.Count > 0) {
                        def.TriggerActionIds = parsed;
                        if (def.IconActionId == 0) {
                            def.IconActionId = parsed[0];
                        }
                        SaveAndReload();
                    }
                }

                ImGui.TableNextColumn();
                var duration = def.DurationSeconds;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("##duration", ref duration, 0.1f, 1f, "%.1f")) {
                    def.DurationSeconds = Math.Max(0, duration);
                    plugin.Configuration.Save();
                }
                if (ImGui.IsItemHovered()) {
                    var tooltip = BuildTimingTooltip("持续时间", def.DurationSecondsByActionId, GetLevelDurationRuleHint(def));
                    if (!string.IsNullOrWhiteSpace(tooltip)) {
                        ImGui.SetTooltip(tooltip);
                    }
                }

                ImGui.TableNextColumn();
                var cd = def.CooldownSeconds;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("##cd", ref cd, 0.1f, 1f, "%.1f")) {
                    def.CooldownSeconds = Math.Max(0, cd);
                    plugin.Configuration.Save();
                }
                if (ImGui.IsItemHovered()) {
                    var tooltip = BuildTimingTooltip("CD", def.CooldownSecondsByActionId, GetLevelCooldownRuleHint(def));
                    if (!string.IsNullOrWhiteSpace(tooltip)) {
                        ImGui.SetTooltip(tooltip);
                    }
                }

                ImGui.TableNextColumn();
                if (DrawJobPicker(def.Jobs)) {
                    plugin.Configuration.Save();
                    plugin.MitigationState.ReloadFromConfig();
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("删除")) {
                    defs.RemoveAt(i);
                    triggerTextById.Remove(def.Id);
                    SaveAndReload();
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawAddMitigation() {
        ImGui.Text("添加减伤（按 Action 搜索）");
        ImGui.TextDisabled("建议：先在 GarlandTools/灰机上确认 ActionId 与 BuffId，再添加到清单并按需调整“分类/生效/持续/CD/职业”。");

        var changed = ImGui.InputText("名称/ID", ref actionSearch, 64);
        ImGui.SameLine();
        var run = ImGui.Button("搜索");

        if (changed || run) {
            UpdateActionSearchResults();
        }

        ImGui.Text("适用职业：");
        DrawJobPicker(newMitigationJobs, id: "##new_jobs");

        if (!string.IsNullOrWhiteSpace(actionSearchMessage)) {
            ImGui.Text(actionSearchMessage);
        }

        if (actionSearchResults.Count == 0) {
            return;
        }

        ImGui.BeginChild("##mp_action_search", new Vector2(0, 180), true);
        foreach (var entry in actionSearchResults) {
            ImGui.PushID((int)entry.Id);

            var canAdd = newMitigationJobs.Count > 0;
            if (!canAdd) {
                ImGui.BeginDisabled();
            }
            if (ImGui.SmallButton("添加")) {
                AddMitigationFromAction(entry);
            }
            if (!canAdd) {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip("请先选择适用职业");
                }
            }

            ImGui.SameLine();
            ImGui.Text($"{entry.Id} | {entry.Name}");

            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private void UpdateActionSearchResults() {
        actionSearchResults.Clear();
        actionSearchMessage = string.Empty;

        var query = actionSearch.Trim();
        if (string.IsNullOrWhiteSpace(query)) {
            return;
        }

        var sheet = Service.DataManager.GetExcelSheet<ActionSheet>();
        if (sheet == null) {
            actionSearchMessage = "无法读取 Action 表";
            return;
        }

        if (uint.TryParse(query, out var actionId)) {
            var row = sheet.GetRowOrDefault(actionId);
            if (row == null) {
                actionSearchMessage = "未找到该 ActionId";
                return;
            }

            var name = row.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) {
                name = "(无名称)";
            }

            actionSearchResults.Add(new ActionSearchEntry(actionId, name));
            return;
        }

        foreach (var row in sheet) {
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) {
                continue;
            }

            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            actionSearchResults.Add(new ActionSearchEntry(row.RowId, name));
            if (actionSearchResults.Count >= MaxSearchResults) {
                actionSearchMessage = $"结果过多，仅显示前 {MaxSearchResults} 条";
                break;
            }
        }

        if (actionSearchResults.Count == 0) {
            actionSearchMessage = "未找到匹配项";
        }
    }

    private void AddMitigationFromAction(ActionSearchEntry entry) {
        var id = $"custom_{entry.Id}_{Guid.NewGuid():N}"[..16];

        plugin.Configuration.Mitigations.Add(new MitigationDefinition {
            Id = id,
            Name = entry.Name,
            IconActionId = entry.Id,
            TriggerActionIds = new List<uint> { entry.Id },
            DurationSeconds = 15,
            CooldownSeconds = 90,
            Category = MitigationCategory.Party,
            ApplyTo = MitigationApplyTo.Target,
            Jobs = new List<JobIds>(newMitigationJobs),
            Enabled = true,
        });

        SaveAndReload();
    }

    private static List<uint> ParseActionIdList(string input) {
        var result = new List<uint>();
        var tokens = input.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens) {
            if (uint.TryParse(token.Trim(), out var id) && id != 0) {
                if (!result.Contains(id)) {
                    result.Add(id);
                }
            }
        }

        return result;
    }

    private string GetOrCreateTriggerText(MitigationDefinition def) {
        if (!triggerTextById.TryGetValue(def.Id, out var value)) {
            value = string.Join(",", def.TriggerActionIds);
            triggerTextById[def.Id] = value;
        }

        return value;
    }

    private static bool DrawCategoryCombo(MitigationDefinition def) {
        var labels = new[] { "个人", "团队", "敌方减伤" };
        var idx = def.Category switch {
            MitigationCategory.Personal => 0,
            MitigationCategory.Party => 1,
            MitigationCategory.EnemyDebuff => 2,
            _ => 1,
        };

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.Combo("##cat", ref idx, labels, labels.Length)) {
            return false;
        }

        def.Category = idx switch {
            0 => MitigationCategory.Personal,
            1 => MitigationCategory.Party,
            2 => MitigationCategory.EnemyDebuff,
            _ => def.Category,
        };
        return true;
    }

    private static bool DrawApplyToCombo(MitigationDefinition def) {
        var labels = new[] { "受伤者", "来源" };
        var idx = def.ApplyTo == MitigationApplyTo.Source ? 1 : 0;

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.Combo("##apply", ref idx, labels, labels.Length)) {
            return false;
        }

        def.ApplyTo = idx == 1 ? MitigationApplyTo.Source : MitigationApplyTo.Target;
        return true;
    }

    private static bool DrawJobPicker(List<JobIds> jobs, string id = "##jobs") {
        var changed = false;
        var preview = jobs.Count == 0 ? "所有职业" : $"{jobs.Count}项";

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo(id, preview, ImGuiComboFlags.HeightLarge)) {
            foreach (var value in (JobIds[])Enum.GetValues(typeof(JobIds))) {
                if (value == JobIds.OTHER) {
                    continue;
                }

                var selected = jobs.Contains(value);
                if (ImGui.Checkbox(value.ToCnName(), ref selected)) {
                    if (selected) {
                        jobs.Add(value);
                    } else {
                        jobs.Remove(value);
                    }

                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private void SaveAndReload() {
        plugin.Configuration.Save();
        plugin.MitigationState.ReloadFromConfig();
    }

    private static bool HasOverrides(Dictionary<uint, float>? map) {
        return map is { Count: > 0 };
    }

    private static string BuildOverridesTooltip(string label, Dictionary<uint, float> map) {
        var lines = map
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key} = {kv.Value:0.##}s")
            .ToList();

        return $"{label}按 ActionId 覆盖：\n{string.Join("\n", lines)}\n（上方输入框为默认值；命中覆盖时以覆盖为准）";
    }

    private static string BuildTimingTooltip(string label, Dictionary<uint, float>? actionIdOverrides, string? levelRuleHint) {
        var parts = new List<string>();

        if (HasOverrides(actionIdOverrides) && actionIdOverrides != null) {
            parts.Add(BuildOverridesTooltip(label, actionIdOverrides));
        }

        if (!string.IsNullOrWhiteSpace(levelRuleHint)) {
            parts.Add(levelRuleHint!);
        }

        return string.Join("\n\n", parts);
    }

    private static string? GetLevelDurationRuleHint(MitigationDefinition def) {
        if (HasAnyTrigger(def, 7560, 7549, 7535)) {
            return "自动等级差异：<98级=10s，>=98级=15s（昏乱/牵制/雪仇）";
        }

        return null;
    }

    private static string? GetLevelCooldownRuleHint(MitigationDefinition def) {
        if (HasAnyTrigger(def, 7405, 16889, 16012)) {
            return "自动等级差异：<88级=120s，>=88级=90s（行吟/策动/防守之桑巴）";
        }

        return null;
    }

    private static bool HasAnyTrigger(MitigationDefinition def, params uint[] actionIds) {
        if (def.IconActionId != 0 && actionIds.Contains(def.IconActionId)) {
            return true;
        }

        if (def.TriggerActionIds == null || def.TriggerActionIds.Count == 0) {
            return false;
        }

        foreach (var id in actionIds) {
            if (def.TriggerActionIds.Contains(id)) {
                return true;
            }
        }

        return false;
    }

    private sealed record ActionSearchEntry(uint Id, string Name);
}

// 本文件实现配置界面：捕获范围、追责规则、默认清单与自定义减伤增删。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
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

    public ConfigWindow(MitigationPolicePlugin plugin) : base("减伤警察 - 设置") {
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
        var trackOnlyInInstances = plugin.Configuration.TrackOnlyInInstances;
        if (ImGui.Checkbox("仅副本内捕获", ref trackOnlyInInstances)) {
            plugin.Configuration.TrackOnlyInInstances = trackOnlyInInstances;
            SaveAndReload();
        }

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

        var maxEvents = plugin.Configuration.MaxStoredEvents;
        if (ImGui.InputInt("最大保存事件数（超出将移除最早战斗）", ref maxEvents)) {
            plugin.Configuration.MaxStoredEvents = Math.Clamp(maxEvents, 100, 200_000);
            plugin.Configuration.Save();
        }

        var allowSend = plugin.Configuration.AllowSendingToPartyChat;
        if (ImGui.Checkbox("同时发送到小队频道（默认仅默语）", ref allowSend)) {
            plugin.Configuration.AllowSendingToPartyChat = allowSend;
            plugin.Configuration.Save();
        }

        var autoAnnounce = plugin.Configuration.AutoAnnounceOverwritesToPartyChat;
        if (ImGui.Checkbox("自动通报：发生冲突减伤（顶掉/覆盖）时播报", ref autoAnnounce)) {
            plugin.Configuration.AutoAnnounceOverwritesToPartyChat = autoAnnounce;
            plugin.Configuration.Save();
        }

        var autoAnnounceDeath = plugin.Configuration.AutoAnnounceDeathsToPartyChat;
        if (ImGui.Checkbox("自动通报：有人死亡时播报减伤警察信息", ref autoAnnounceDeath)) {
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
    }

    private void DrawMitigationList() {
        ImGui.Text("减伤清单");

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
                if (ImGui.Checkbox($"{value}", ref selected)) {
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

# 数据模型

本章节描述持久化与展示所使用的核心数据模型（位于 `Models/`）。

---

## CombatSessionRecord（战斗会话）

一次“战斗会话”的持久化记录（按进战/脱战分段）。

| 字段 | 类型 | 说明 |
|------|------|------|
| Id | Guid | 会话唯一 ID |
| StartUtc | DateTimeOffset | 会话开始时间（UTC） |
| EndUtc | DateTimeOffset? | 会话结束时间（UTC，可空） |
| TerritoryId | uint | 地区 ID |
| TerritoryName | string | 地区名 |
| ContentId | uint? | 副本/内容 ID（可空） |
| ContentName | string? | 副本/内容名（可空） |
| Events | List\<DamageEventRecord> | 本会话内的受伤事件列表 |

---

## DamageEventRecord（受伤事件）

一次受伤/命中事件的持久化记录，用于复盘、统计与分享。

| 字段 | 类型 | 说明 |
|------|------|------|
| Id | Guid | 事件唯一 ID |
| TimestampUtc | DateTimeOffset | 事件时间（UTC） |
| TerritoryId / TerritoryName | uint / string | 地区信息 |
| ContentId / ContentName | uint? / string? | 内容信息 |
| TargetId / TargetName | uint / string | 受击目标 |
| TargetJob | JobIds | 受击目标职业 |
| SourceId / SourceName | uint? / string? | 伤害来源（可空） |
| ActionId / ActionName | uint? / string? | 技能信息（可空） |
| DamageAmount | uint | 伤害数值 |
| DamageType | string? | 伤害类型（可空） |
| IsFatal | bool | 是否致死 |
| ActiveMitigations | List\<MitigationContribution> | 命中瞬间已生效的减伤贡献 |
| MissingMitigations | List\<MissingMitigation> | 命中瞬间可用但未交的减伤候选 |

---

## MitigationDefinition（减伤定义）

减伤技能/能力的定义（支持同 CD 的升级/变体技能）。

| 字段 | 类型 | 说明 |
|------|------|------|
| Id / Name | string / string | 内部 ID / 展示名 |
| IconActionId | uint | 用于显示的图标 ActionId |
| TriggerActionIds | List\<uint> | 触发该减伤的 ActionId 列表 |
| DurationSeconds / CooldownSeconds | float / float | 默认持续/冷却 |
| DurationSecondsByActionId | Dictionary\<uint,float> | 按 ActionId 覆盖持续时间 |
| CooldownSecondsByActionId | Dictionary\<uint,float> | 按 ActionId 覆盖冷却时间 |
| Category | MitigationCategory | 分类（例如 Party/Personal 等） |
| ApplyTo | MitigationApplyTo | 作用对象（例如 Target/Party 等） |
| Jobs | List\<JobIds> | 可用职业 |
| Enabled | bool | 是否启用 |

---

## MitigationContribution（已生效减伤贡献）

命中瞬间“已生效”的减伤贡献明细。

| 字段 | 类型 | 说明 |
|------|------|------|
| MitigationId / MitigationName | string / string | 减伤 ID / 名称 |
| IconActionId | uint | 图标 ActionId |
| CasterId / CasterName | uint / string | 施放者 |
| RemainingSeconds | float | 命中时剩余持续时间（秒） |

---

## MissingMitigation（可用未交候选）

命中瞬间“可用但未交”的减伤责任候选信息。

| 字段 | 类型 | 说明 |
|------|------|------|
| MitigationId / MitigationName | string / string | 减伤 ID / 名称 |
| IconActionId | uint | 图标 ActionId |
| OwnerId / OwnerName | uint / string | 减伤拥有者（责任候选） |
| OwnerJob | JobIds | 拥有者职业 |
| NeverUsedSinceDutyStart | bool | 从进本后是否从未使用过 |
| AvailableForSeconds | float | 转好已持续的时间（秒） |

---

## MitigationOverwrite（顶掉/覆盖）

互斥减伤的“顶掉/覆盖”记录，用于 UI 与分享文本展示。

| 字段 | 类型 | 说明 |
|------|------|------|
| TimestampUtc | DateTimeOffset | 覆盖发生时间（UTC） |
| AppliedActorId / AppliedActorName | uint / string | 被覆盖目标 |
| ConflictGroupId | string | 互斥组 ID |
| Old* | 多字段 | 旧减伤信息（名称/施放者/剩余时长等） |
| New* | 多字段 | 新减伤信息（名称/施放者/持续时长等） |

---

## 持久化文件
- 文件名: `mitigation-police.json`
- 位置: 插件配置目录（由 `IDalamudPluginInterface.GetPluginConfigDirectory()` 决定）

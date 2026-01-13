# 技术设计：学会等级过滤 + 致死详情 + 默语播报

## 1. 补全「武装解除」
- 数据来源：灰机 Wiki/技能提示数据（ActionId=2887，Lv62，持续10s，CD120s）。
- 在默认减伤库新增一条敌方减伤（EnemyDebuff），并通过配置迁移注入到已有用户配置中（避免只影响新装）。

## 2. “学会等级”过滤策略
- 目标：对“缺失减伤（可用未交）”的生成阶段加过滤，未到学会等级直接视为不可用。
- 实现：基于 `Lumina.Excel.Sheets.Action` 的 `ClassJobLevel` 动态计算每条减伤的最低学会等级（取 `TriggerActionIds`/`IconActionId` 中最小非零值）。
- 运行时取队员当前等级（等级同步后值即为同步等级），若 `owner.Level < requiredLevel` 则不产生 MissingMitigation 记录。

## 3. 致死详情采集与过量伤害
- 在 ActionEffect detour 调用 `Original` 之前，先从目标对象读取“生前血量/盾量”（HP/Shield%）。
- 将生前血量、生前盾量写入 `DamageEventRecord`（默认 0 表示未知）。
- 过量伤害：`max(0, 致死伤害 - (生前血量 + 生前盾量))`，并在分享文本与死亡自动播报中展示。

## 4. 播报通道调整
- `ChatSender` 新增 `/e`（默语）发送能力。
- 所有播报（手动发送/自动通报）默认走默语；若配置 `AllowSendingToPartyChat=true` 且在小队中，再额外向小队频道逐行发送。
- 将 `AllowSendingToPartyChat` 默认值调整为 false，并通过配置迁移将旧用户的该开关重置为 false（降低误发风险）。

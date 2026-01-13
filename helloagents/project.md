# 项目技术约定

## 技术栈
- **类型:** Dalamud 插件（FFXIV）
- **语言:** C#（Nullable/ImplicitUsings 开启）
- **SDK:** `Dalamud.CN.NET.Sdk/14.0.1`
- **DalamudApiLevel:** 14

## 开发与构建
- **本地构建:** `dotnet build -c Release`
  - 依赖本机已安装的 Dalamud（例如 XIVLauncherCN 环境），用于解析/生成构建所需的 Dalamud 依赖。
- **CI 构建:** GitHub Actions 在 Windows 环境下构建，并设置 `DALAMUD_HOME=vendor/dalamud` 以保证无本机 Dalamud 环境也可编译。

## 用户入口
- **命令:** `/mp`（打开/关闭主窗口）
- **UI:** 主窗口（复盘/分享）+ 配置窗口（开关/阈值/发送通道）

## 播报与追责规则
- **播报通道:** 默认使用默语（`/e`）发送；仅在勾选“同时发送到小队频道”后，才会额外发送到小队频道（`/p`）。
- **学会等级过滤:** “缺失减伤（可用未交）”仅对已学会技能追责；等级同步导致未学会时同样不追责。
- **已放未覆盖标注:** 若检测到某减伤在持续窗口内已释放，但本次命中目标未吃到该减伤，则在“缺失减伤”中标注为“已放未覆盖”。

## 数据与存储
- **本地持久化:** 使用 JSON 文件保存战斗会话与受伤事件记录。
  - 文件名: `mitigation-police.json`
  - 位置: Dalamud 插件配置目录（由 `IDalamudPluginInterface.GetPluginConfigDirectory()` 决定）

## 发布与下载
- **稳定下载链接（始终不变）:**
  - `https://github.com/flyrio/MitigationPolice/releases/download/latest/MitigationPolice.zip`
- **发布机制:** 每次 push 到 `main` 后，Actions 会构建并更新 Release（tag: `latest`），覆盖上传 `MitigationPolice.zip`。
  - 工作流: `.github/workflows/publish-latest-zip.yml`

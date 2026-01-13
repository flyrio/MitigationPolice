# MitigationPolice（减伤巡查）

> 本文件包含项目级别的核心信息。详细的模块文档见 `modules/` 目录。

---

## 1. 项目概述

### 目标与背景
在副本内按“每场战斗会话”记录队伍成员受到的伤害事件，归因命中瞬间生效的减伤，并推算减伤转好未交的责任人（含转好时长/从开场未交）。支持按战斗回看、按开场时间定位、致死标注与一键复制/发送小队频道。

### 范围
- **范围内:** 受伤事件记录、减伤贡献/缺失推算、顶减伤（覆盖）检测、复盘 UI、文本分享/小队通报。
- **范围外:** 任何形式的外部数据上传、跨客户端同步、对外服务/API。

### 干系人
- **负责人:** flyrio

---

## 2. 模块索引

| 模块名称 | 职责 | 状态 | 文档 |
|---------|------|------|------|
| Core | 插件入口、服务定位、配置管理 | ✅稳定 | [modules/core.md](modules/core.md) |
| Events | 捕获受伤/减伤/死亡等事件并入库 | ✅稳定 | [modules/events.md](modules/events.md) |
| Mitigations | 减伤库、状态跟踪、缺失/覆盖判定 | ✅稳定 | [modules/mitigations.md](modules/mitigations.md) |
| UI | ImGui 窗口、复盘展示、分享入口 | ✅稳定 | [modules/ui.md](modules/ui.md) |
| Chat | 分享文本生成与小队频道发送 | ✅稳定 | [modules/chat.md](modules/chat.md) |
| Storage | 会话与事件的 JSON 持久化 | ✅稳定 | [modules/storage.md](modules/storage.md) |
| Models | 数据模型定义（持久化与显示） | ✅稳定 | [modules/models.md](modules/models.md) |

---

## 3. 快速链接
- [技术约定](../project.md)
- [架构设计](arch.md)
- [用户入口与命令](api.md)
- [数据模型](data.md)
- [变更历史](../history/index.md)

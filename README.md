# EagleShareSystem

把 **Eagle 素材库**里打了「发布」标签的图片，自动生成可浏览的静态图站，并同步到 **GitHub Pages** 与自建站点（如 [eagle.mantoublog.top](https://eagle.mantoublog.top/)）。

适合库很大（例如十万级）、但真正要公开的图不多的场景：完整库可放在 Dropbox，CI **只跟增量变更**，不会每次扫描整库。

## 能做什么

- 扫描 Dropbox 下多个 `*.library`
- 只收录带指定标签（默认 `发布`）的图片
- 生成静态站点：分类树、搜索、标签筛选、面包屑、灯箱预览
- 列表默认用 **缩略图**，打开大图再加载原图
- 删除图 / 去掉「发布」/ Eagle 回收站后，下次同步会从站点移除
- 同时部署到 GitHub Pages 与服务器（SSH + rsync 增量）

## 仓库结构

```text
EagleStaticSiteTool/     # .NET 9 生成工具（本地扫描 / Dropbox 增量）
.eagle-sync/state.json   # 同步游标与已发布清单（Action 会回写）
.github/workflows/       # 构建、部署 Pages、rsync 到服务器
EagleDemo*.library/      # 本地演示库（可选）
```

## 工作原理（简要）

1. **Cursor**：Dropbox 的增量书签，记在 `.eagle-sync/state.json`  
   之后只处理「书签之后」有变更的文件，不扫十万张全库。
2. **已发布清单**：同一 state 里的 `Images` / `Libraries`  
   每次按这份清单重新生成站点；变更只负责更新清单。
3. **标签过滤**：只有带「发布」的图会进入清单；去掉标签或删除后下次同步下架。
4. **部署**：生成 `EagleSiteOutput` → 上传 Pages；同时用 `rsync --checksum` 增量同步到服务器。

## 本地生成

需要 [.NET 9 SDK](https://dotnet.microsoft.com/download)。

```bash
# 扫描仓库内本地 *.library
dotnet run --project ./EagleStaticSiteTool -- . ./EagleSiteOutput 发布

# 或指定单个库路径
dotnet run --project ./EagleStaticSiteTool -- "D:\Dropbox\MyLib.library" ./EagleSiteOutput 发布
```

浏览器打开：`EagleSiteOutput/index.html`。

## Dropbox 自动发布（GitHub Actions）

### 触发时机

| 方式 | 说明 |
|------|------|
| 定时 | 每天北京时间 **06:00 / 12:00 / 18:00** |
| 手动 | Actions → Deploy Eagle Site To Pages → Run workflow |
| Push `main` | 主要用于代码变更（忽略 `.eagle-sync/**`） |

建议：在 Eagle 打完「发布」并等 Dropbox 同步完成后，也可手动跑一次，不必干等定时。

### 需要配置的 Secrets / Variables

**Dropbox**

| 名称 | 类型 | 说明 |
|------|------|------|
| `DROPBOX_APP_KEY` | Secret | App key |
| `DROPBOX_APP_SECRET` | Secret | App secret |
| `DROPBOX_REFRESH_TOKEN` | Secret | 需含 `files.metadata.read`、`files.content.read` |
| `DROPBOX_LIBRARY_PATH` | Variable/Secret | 库根路径，如 `/Eagle`（会扫描其下所有 `*.library`） |

可选：`DROPBOX_BOOTSTRAP_MODE=cursor`（默认，只从当前时刻起跟踪）；`since-scan` 会按日期全量列举，大库极慢，不推荐。

**自建服务器（SSH）**

| 名称 | 说明 |
|------|------|
| `SERVER_IP` | 主机 |
| `SERVER_USER` | SSH 用户 |
| `SERVER_PASSWORD` | SSH 密码（服务器需允许密码登录） |
| `SERVER_EAGLEPATH` | 站点根目录（Nginx 指向的目录） |

### Dropbox 授权注意

- App Permissions 勾选并提交：`files.metadata.read`、`files.content.read`
- 授权链接需带 `token_access_type=offline` 以拿到 `refresh_token`
- 权限变更后必须重新授权换新 token

## 站点使用说明

- 左侧：多库 / 文件夹树（可折叠，默认折叠；显示各目录已发布数量）
- 中间：缩略图网格（虚拟列表 + 懒加载）
- 右侧：详情（预览为缩略图，点击打开原图灯箱）
- 灯箱：左右翻页、Esc 关闭、缩放动画
- 面包屑可回上级；`/` 聚焦搜索
- URL `#img=图片ID` 可直达某张图
- 折叠、排序、筛选等偏好会记在浏览器 `localStorage`

## 常见问题

**为什么刚打「发布」站点还没有？**  
等 Dropbox 云端同步完成，再等定时任务或手动跑 Action。

**删了图还会留在网站吗？**  
不会永久留下。同步到删除/下架后会从 state 移除，Pages 整包更新，服务器 rsync 也会删掉多余文件。

**离线可用的文件会被扫吗？**  
「离线可用」只是本机缓存，文件本来就在 Dropbox。Action 读的是云端变更，不是「你是否下载到本机」。

**列表为什么曾经是原图？**  
早期同步未带缩略图字段时会回退到原图；当前版本会在同步时补下 `_thumbnail`。

## 技术栈

- 生成工具：C# / .NET 9
- 前端：静态 HTML / CSS / JS
- CI：GitHub Actions
- 源库：Eagle `.library` + Dropbox API
- 部署：GitHub Pages + SSH/rsync

## 许可

按仓库所有者约定使用；演示库中的素材仅供开发测试。

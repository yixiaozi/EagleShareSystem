# Mac：Dropbox staging → iCloud 相册

GitHub Action 把 Eagle 新图片/视频复制到 Dropbox `/Share/iCloud-Inbox`。本脚本用 **Dropbox API 直接下载**到 `~/.eagle-sync/staging-inbox`（**不经过 Dropbox 本地目录**），导入「照片」App 后删除临时文件。

```text
Eagle 新图 → Action → Dropbox 云端 staging
    ↓ API 下载到 ~/.eagle-sync/staging-inbox
导入相册「生活」→ 删除临时文件
    ↓ iCloud 照片
iPhone
```

## 安装

```bash
cd /path/to/EagleShareSystem/scripts/icloud-photos-import
bash install.sh
# 编辑 ~/.config/eagle-icloud-photos-import/config.env
```

## 配置

| 变量 | 说明 |
|------|------|
| `DROPBOX_STAGING_REMOTE` | 云端路径 `/Share/iCloud-Inbox` |
| `DOWNLOAD_DIR` | 本机临时目录，默认 `~/.eagle-sync/staging-inbox` |
| `DROPBOX_APP_KEY` / `SECRET` / `REFRESH_TOKEN` | 与 GitHub Secrets 相同 |
| `PHOTOS_ALBUM` | 相册名，默认 `生活` |

## 手动运行

```bash
bash import-to-photos.sh --dry-run
bash import-to-photos.sh
```

导入成功后会删除 `DOWNLOAD_DIR` 里的临时文件；同一轮待导入文件会 **一次性批量导入**，在「照片」里只显示一条导入记录。

## 前置条件

- iCloud 照片已开启
- 允许终端/bash 控制「照片」App（自动化权限）

## 日志

```bash
tail -f ~/Library/Logs/eagle-icloud-photos-import.log
```

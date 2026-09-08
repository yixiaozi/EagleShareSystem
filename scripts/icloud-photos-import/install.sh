#!/usr/bin/env bash
# 安装 iCloud 相册自动导入（LaunchAgent，每 5 分钟扫描一次 staging 目录）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMPORT_SCRIPT="$SCRIPT_DIR/import-to-photos.sh"
CONFIG_DIR="$HOME/.config/eagle-icloud-photos-import"
CONFIG_FILE="$CONFIG_DIR/config.env"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"
PLIST_LABEL="com.eagleshare.icloud-photos-import"
PLIST_DEST="$LAUNCH_AGENTS_DIR/$PLIST_LABEL.plist"
LOG_FILE="$HOME/Library/Logs/eagle-icloud-photos-import.log"

chmod +x "$IMPORT_SCRIPT"

mkdir -p "$CONFIG_DIR"
if [[ ! -f "$CONFIG_FILE" ]]; then
  cp "$SCRIPT_DIR/config.env.example" "$CONFIG_FILE"
  echo "已创建配置文件: $CONFIG_FILE"
  echo "请填写 config.env 中的 Dropbox 凭证后重新运行 install.sh"
fi

mkdir -p "$LAUNCH_AGENTS_DIR" "$(dirname "$LOG_FILE")"
mkdir -p "$HOME/.eagle-sync"

sed \
  -e "s|__SCRIPT_PATH__|$IMPORT_SCRIPT|g" \
  -e "s|__LOG_PATH__|$LOG_FILE|g" \
  "$SCRIPT_DIR/com.eagleshare.icloud-photos-import.plist" >"$PLIST_DEST"

launchctl bootout "gui/$(id -u)/$PLIST_LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST_DEST"
launchctl enable "gui/$(id -u)/$PLIST_LABEL" 2>/dev/null || true

echo ""
echo "安装完成。"
echo "  配置: $CONFIG_FILE"
echo "  日志: $LOG_FILE"
echo "  LaunchAgent: $PLIST_DEST（每 5 分钟运行一次）"
echo ""
echo "首次使用前请："
echo "  1. 若弹出权限请求，允许 bash 控制「照片」"
echo "  2. 手动试跑: bash \"$IMPORT_SCRIPT\" --dry-run"
echo "  3. 正式导入: bash \"$IMPORT_SCRIPT\""
echo ""
echo "卸载: launchctl bootout gui/$(id -u)/$PLIST_LABEL && rm \"$PLIST_DEST\""

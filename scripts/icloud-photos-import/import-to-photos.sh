#!/usr/bin/env bash
# 从 Dropbox API 拉取 staging 文件，导入 macOS「照片」App（iCloud 相册）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_FILE="${EAGLE_ICLOUD_IMPORT_CONFIG:-$HOME/.config/eagle-icloud-photos-import/config.env}"

DRY_RUN=0
if [[ "${1:-}" == "--dry-run" ]]; then
  DRY_RUN=1
  shift
fi

if [[ -f "$CONFIG_FILE" ]]; then
  set -a
  # shellcheck source=/dev/null
  source "$CONFIG_FILE"
  set +a
fi

DOWNLOAD_DIR="${DOWNLOAD_DIR:-$HOME/.eagle-sync/staging-inbox}"
PHOTOS_ALBUM="${PHOTOS_ALBUM:-生活}"
STATE_FILE="${STATE_FILE:-$HOME/.eagle-sync/photos-import-state.json}"
LOG_FILE="${LOG_FILE:-$HOME/Library/Logs/eagle-icloud-photos-import.log}"

MEDIA_RE='\.(jpe?g|png|gif|webp|bmp|tiff?|heic|heif|avif|ico|raw|cr2|cr3|nef|arw|dng|orf|rw2|mp4|mov|mkv|avi|webm|m4v|wmv|flv|mpeg|mpg|3gp|mts)$'

log() {
  local msg="[$(date '+%Y-%m-%d %H:%M:%S')] $*"
  echo "$msg"
  mkdir -p "$(dirname "$LOG_FILE")"
  echo "$msg" >>"$LOG_FILE"
}

pull_from_dropbox() {
  log "从 Dropbox API 拉取 staging 文件..."
  if [[ "$DRY_RUN" -eq 1 ]]; then
    python3 "$SCRIPT_DIR/dropbox-pull-staging.py" --dry-run || return 1
  else
    python3 "$SCRIPT_DIR/dropbox-pull-staging.py" || return 1
  fi
}

is_media_file() {
  local name
  name="$(basename "$1")"
  [[ "$name" =~ $MEDIA_RE ]] || return 1
  [[ "$name" == .* ]] && return 1
  return 0
}

file_fingerprint() {
  local f="$1"
  local size mtime
  size=$(stat -f '%z' "$f")
  mtime=$(stat -f '%m' "$f")
  printf '%s|%s|%s' "$size" "$mtime" "$(basename "$f")"
}

already_processed() {
  local fp="$1"
  python3 - "$STATE_FILE" "$fp" <<'PY'
import json, sys
from pathlib import Path

state_path = Path(sys.argv[1])
fp = sys.argv[2]
if not state_path.exists():
    sys.exit(1)
try:
    data = json.loads(state_path.read_text(encoding="utf-8"))
except Exception:
    sys.exit(1)
sys.exit(0 if fp in set(data.get("processed", [])) else 1)
PY
}

mark_processed() {
  local fp="$1" src="$2" album="$3" remote_key="${4:-}"
  python3 - "$STATE_FILE" "$fp" "$src" "$album" "$remote_key" <<'PY'
import json, sys
from datetime import datetime, timezone
from pathlib import Path

state_path = Path(sys.argv[1])
fp, src, album, remote_key = sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
state_path.parent.mkdir(parents=True, exist_ok=True)
data = {"processed": [], "processedRemote": [], "history": []}
if state_path.exists():
    try:
        data = json.loads(state_path.read_text(encoding="utf-8"))
    except Exception:
        pass
processed = data.setdefault("processed", [])
if fp not in processed:
    processed.append(fp)
if remote_key:
    remote = data.setdefault("processedRemote", [])
    if remote_key not in remote:
        remote.append(remote_key)
data.setdefault("history", []).append({
    "fingerprint": fp,
    "source": src,
    "album": album,
    "remoteKey": remote_key or None,
    "at": datetime.now(timezone.utc).isoformat(),
})
if len(processed) > 50000:
    data["processed"] = processed[-50000:]
state_path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
PY
}

lookup_remote_key() {
  local name="$1"
  local manifest="$DOWNLOAD_DIR/.remote-manifest.json"
  [[ -f "$manifest" ]] || return 0
  python3 - "$manifest" "$name" <<'PY'
import json, sys
from pathlib import Path
manifest = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
print(manifest.get(sys.argv[2], ""))
PY
}

import_batch_to_photos() {
  local album_name="$1"
  shift
  local -a files=("$@")
  [[ ${#files[@]} -gt 0 ]] || return 0

  osascript - "$album_name" "${files[@]}" <<'APPLESCRIPT'
on run argv
  set albumName to item 1 of argv
  set importList to {}
  repeat with i from 2 to count of argv
    set end of importList to POSIX file (item i of argv)
  end repeat
  tell application "Photos"
    activate
    if not (exists album albumName) then
      make new album named albumName
    end if
    import importList into album albumName with skip check duplicates
  end tell
  return count of importList
end run
APPLESCRIPT
}

main() {
  pull_from_dropbox || true

  mkdir -p "$DOWNLOAD_DIR"

  local count=0 skipped=0
  local -a pending_files=()
  local -a pending_fps=()
  local -a pending_remote_keys=()

  shopt -s nullglob
  for file in "$DOWNLOAD_DIR"/*; do
    [[ -f "$file" ]] || continue
    is_media_file "$file" || continue

    count=$((count + 1))
    fp="$(file_fingerprint "$file")"
    remote_key="$(lookup_remote_key "$(basename "$file")")"

    if already_processed "$fp"; then
      log "已导入过，删除临时文件: $(basename "$file")"
      if [[ "$DRY_RUN" -eq 0 ]]; then
        rm -f "$file"
      fi
      skipped=$((skipped + 1))
      continue
    fi

    pending_files+=("$file")
    pending_fps+=("$fp")
    pending_remote_keys+=("$remote_key")
  done

  local pending_count=${#pending_files[@]}
  if [[ "$pending_count" -eq 0 ]]; then
    log "完成: 扫描 $count 个媒体文件，待导入 0，跳过 $skipped"
    return 0
  fi

  if [[ "$DRY_RUN" -eq 1 ]]; then
    log "[dry-run] 将批量导入 $pending_count 个文件 -> 相册「$PHOTOS_ALBUM」"
    for file in "${pending_files[@]}"; do
      log "  - $(basename "$file")"
    done
    log "完成: 扫描 $count 个媒体文件，待导入 $pending_count，跳过 $skipped"
    return 0
  fi

  log "批量导入 $pending_count 个文件 -> 相册「$PHOTOS_ALBUM」..."
  if import_batch_to_photos "$PHOTOS_ALBUM" "${pending_files[@]}"; then
    local i
    for i in "${!pending_files[@]}"; do
      mark_processed "${pending_fps[$i]}" "${pending_files[$i]}" "$PHOTOS_ALBUM" "${pending_remote_keys[$i]}"
      rm -f "${pending_files[$i]}"
    done
    log "已批量导入并确认: $pending_count 个文件 -> 相册「$PHOTOS_ALBUM」"
  else
    log "批量导入失败（保留 $pending_count 个文件待重试）"
    return 1
  fi

  log "完成: 扫描 $count 个媒体文件，导入 $pending_count，跳过 $skipped"
}

main "$@"

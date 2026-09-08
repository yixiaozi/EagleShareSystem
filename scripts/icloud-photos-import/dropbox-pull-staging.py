#!/usr/bin/env python3
"""从 Dropbox API 直接拉取 staging 目录文件，绕过 Mac File Provider 同步。"""
from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

MEDIA_EXT = {
    "jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "heic", "heif", "avif", "ico",
    "raw", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2",
    "mp4", "mov", "mkv", "avi", "webm", "m4v", "wmv", "flv", "mpeg", "mpg", "3gp", "mts",
}


def log(msg: str) -> None:
    print(msg, flush=True)


def is_media(name: str) -> bool:
    ext = Path(name).suffix.lstrip(".").lower()
    return ext in MEDIA_EXT and not name.startswith(".")


def load_state(path: Path) -> dict:
    if not path.exists():
        return {"processed": [], "processedRemote": [], "history": []}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {"processed": [], "processedRemote": [], "history": []}
    data.setdefault("processed", [])
    data.setdefault("processedRemote", [])
    data.setdefault("history", [])
    return data


def save_state(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def get_access_token(app_key: str, app_secret: str, refresh_token: str) -> str:
    body = urllib.parse.urlencode(
        {
            "grant_type": "refresh_token",
            "refresh_token": refresh_token,
            "client_id": app_key,
            "client_secret": app_secret,
        }
    ).encode()
    req = urllib.request.Request("https://api.dropboxapi.com/oauth2/token", data=body, method="POST")
    with urllib.request.urlopen(req, timeout=60) as resp:
        payload = json.loads(resp.read().decode())
    token = payload.get("access_token")
    if not token:
        raise RuntimeError(f"Dropbox token refresh failed: {payload}")
    return token


def api_json(token: str, url: str, payload: dict) -> dict:
    req = urllib.request.Request(url, data=json.dumps(payload).encode(), method="POST")
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.loads(resp.read().decode())


def list_folder(token: str, remote_path: str) -> list[dict]:
    entries: list[dict] = []
    url = "https://api.dropboxapi.com/2/files/list_folder"
    payload: dict = {"path": remote_path, "recursive": False, "include_deleted": False}
    while True:
        data = api_json(token, url, payload)
        entries.extend(data.get("entries", []))
        if not data.get("has_more"):
            break
        url = "https://api.dropboxapi.com/2/files/list_folder/continue"
        payload = {"cursor": data["cursor"]}
    return entries


def download_file(token: str, remote_path: str, local_path: Path) -> None:
    arg = json.dumps({"path": remote_path})
    req = urllib.request.Request("https://content.dropboxapi.com/2/files/download", method="POST")
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("Dropbox-API-Arg", arg)
    with urllib.request.urlopen(req, timeout=300) as resp:
        local_path.parent.mkdir(parents=True, exist_ok=True)
        local_path.write_bytes(resp.read())


def remote_key(entry: dict) -> str:
    rev = entry.get("rev") or entry.get("server_modified") or ""
    return f"{entry.get('path_display', '')}|{rev}"


def main() -> int:
    dry_run = "--dry-run" in sys.argv

    app_key = os.environ.get("DROPBOX_APP_KEY", "").strip()
    app_secret = os.environ.get("DROPBOX_APP_SECRET", "").strip()
    refresh_token = os.environ.get("DROPBOX_REFRESH_TOKEN", "").strip()
    remote_path = os.environ.get("DROPBOX_STAGING_REMOTE", "/Share/iCloud-Inbox").strip()
    download_dir = Path(
        os.environ.get("DOWNLOAD_DIR", str(Path.home() / ".eagle-sync/staging-inbox"))
    ).expanduser()
    state_file = Path(
        os.environ.get("STATE_FILE", str(Path.home() / ".eagle-sync/photos-import-state.json"))
    ).expanduser()

    missing = [n for n, v in [
        ("DROPBOX_APP_KEY", app_key),
        ("DROPBOX_APP_SECRET", app_secret),
        ("DROPBOX_REFRESH_TOKEN", refresh_token),
    ] if not v]
    if missing:
        log(f"Dropbox pull 跳过：缺少配置 {', '.join(missing)}")
        return 0

    if not remote_path.startswith("/"):
        remote_path = "/" + remote_path

    state = load_state(state_file)
    processed_remote = set(state.get("processedRemote", []))

    try:
        token = get_access_token(app_key, app_secret, refresh_token)
        entries = list_folder(token, remote_path)
    except urllib.error.HTTPError as exc:
        body = exc.read().decode(errors="replace")
        log(f"Dropbox pull 失败 ({exc.code}): {body}")
        return 1
    except Exception as exc:
        log(f"Dropbox pull 失败: {exc}")
        return 1

    pulled = skipped = 0
    manifest: dict[str, str] = {}
    if download_dir.joinpath(".remote-manifest.json").exists() and not dry_run:
        try:
            manifest = json.loads(download_dir.joinpath(".remote-manifest.json").read_text(encoding="utf-8"))
        except Exception:
            manifest = {}

    for entry in entries:
        if entry.get(".tag") != "file":
            continue
        name = entry.get("name", "")
        if not is_media(name):
            continue

        key = remote_key(entry)
        if key in processed_remote:
            skipped += 1
            continue

        remote_display = entry.get("path_display") or f"{remote_path.rstrip('/')}/{name}"
        local_path = download_dir / name

        if dry_run:
            log(f"[dry-run] 将从 Dropbox 下载: {name}")
            pulled += 1
            continue

        try:
            download_file(token, remote_display, local_path)
            manifest[name] = key
            log(f"已从 Dropbox 下载: {name}")
            pulled += 1
        except Exception as exc:
            log(f"下载失败 {name}: {exc}")

    if not dry_run:
        download_dir.mkdir(parents=True, exist_ok=True)
        download_dir.joinpath(".remote-manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    log(f"Dropbox pull 完成: 下载 {pulled}，跳过 {skipped}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE_USER="${SERVICE_USER:-osmanager}"

[[ "$(id -u)" -eq 0 ]] || { echo "请使用 sudo $0" >&2; exit 1; }
[[ "$#" -gt 0 ]] || { echo "用法：sudo $0 /opt/apps/app-a [/etc/app-a ...]" >&2; exit 1; }
id "$SERVICE_USER" >/dev/null 2>&1 || { echo "服务账户不存在：$SERVICE_USER" >&2; exit 1; }
command -v setfacl >/dev/null || { echo "未找到 setfacl，请安装 acl 软件包" >&2; exit 1; }

for raw_path in "$@"; do
  [[ "$raw_path" == /* ]] || { echo "必须使用绝对路径：$raw_path" >&2; exit 1; }
  [[ -e "$raw_path" ]] || { echo "路径不存在：$raw_path" >&2; exit 1; }
  resolved="$(readlink -f -- "$raw_path")"
  case "$resolved" in
    /|/bin|/boot|/dev|/etc|/home|/lib|/lib64|/proc|/root|/run|/sbin|/sys|/usr|/var)
      echo "拒绝为系统根目录授权：$resolved" >&2; exit 1 ;;
  esac
  setfacl -R -m "u:$SERVICE_USER:rwX" -- "$resolved"
  if [[ -d "$resolved" ]]; then setfacl -R -m "d:u:$SERVICE_USER:rwX" -- "$resolved"; fi
  echo "已授权 $SERVICE_USER：$resolved"
done

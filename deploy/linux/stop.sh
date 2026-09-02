#!/usr/bin/env bash
set -Eeuo pipefail
SERVICE_NAME="${SERVICE_NAME:-osmanager}"
[[ "$(id -u)" -eq 0 ]] || { echo "请使用 sudo $0" >&2; exit 1; }
systemctl stop "$SERVICE_NAME.service"
echo "$SERVICE_NAME.service 已停止"

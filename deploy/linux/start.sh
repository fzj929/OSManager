#!/usr/bin/env bash
set -Eeuo pipefail
SERVICE_NAME="${SERVICE_NAME:-osmanager}"
[[ "$(id -u)" -eq 0 ]] || { echo "请使用 sudo $0" >&2; exit 1; }
systemctl start "$SERVICE_NAME.service"
systemctl status "$SERVICE_NAME.service" --no-pager

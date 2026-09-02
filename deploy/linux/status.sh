#!/usr/bin/env bash
set -Eeuo pipefail
SERVICE_NAME="${SERVICE_NAME:-osmanager}"
systemctl status "$SERVICE_NAME.service" --no-pager

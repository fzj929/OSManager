#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE_NAME="${SERVICE_NAME:-osmanager}"
SERVICE_USER="${SERVICE_USER:-osmanager}"
SERVICE_GROUP="${SERVICE_GROUP:-osmanager}"
INSTALL_ROOT="${INSTALL_ROOT:-/opt/osmanager}"
CONFIG_DIR="${CONFIG_DIR:-/etc/osmanager}"
DATA_DIR="${DATA_DIR:-/var/lib/osmanager}"
UNIT_PATH="/etc/systemd/system/$SERVICE_NAME.service"
SUDOERS_PATH="/etc/sudoers.d/osmanager"
PURGE=0

info() { echo "[OSManager] $*"; }
fail() { echo "[ERROR] $*" >&2; exit 1; }
usage() {
  cat <<'EOF'
用法：sudo ./uninstall.sh [--purge]

默认卸载 systemd 服务、程序版本、管理命令和 sudo 规则，保留：
  /etc/osmanager       配置
  /var/lib/osmanager   SQLite 数据库和备份

--purge  同时删除配置、数据库、备份以及 osmanager 服务账户。
EOF
}

for arg in "$@"; do
  case "$arg" in
    --purge) PURGE=1 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "未知参数：$arg" ;;
  esac
done

[[ "$(id -u)" -eq 0 ]] || fail "请使用 sudo ./uninstall.sh 运行"
[[ "$(uname -s)" == "Linux" ]] || fail "该卸载脚本只支持 Linux"
[[ "$SERVICE_NAME" =~ ^[A-Za-z0-9_.@:-]+$ ]] || fail "服务名无效：$SERVICE_NAME"

safe_tree_path() {
  local resolved
  [[ "$1" == /* ]] || fail "拒绝删除非绝对路径：$1"
  resolved="$(readlink -m -- "$1")"
  case "$resolved" in
    /|/bin|/boot|/dev|/etc|/home|/lib|/lib64|/opt|/proc|/root|/run|/sbin|/sys|/usr|/var)
      fail "拒绝删除系统目录：$resolved" ;;
  esac
  printf '%s\n' "$resolved"
}

install_root="$(safe_tree_path "$INSTALL_ROOT")"
config_dir="$(safe_tree_path "$CONFIG_DIR")"
data_dir="$(safe_tree_path "$DATA_DIR")"

info "停止并禁用 $SERVICE_NAME.service"
systemctl disable --now "$SERVICE_NAME.service" >/dev/null 2>&1 || true
rm -f -- "$UNIT_PATH"
systemctl daemon-reload
systemctl reset-failed "$SERVICE_NAME.service" >/dev/null 2>&1 || true

info "删除程序文件和辅助授权"
rm -rf -- "$install_root"
rm -f -- "$SUDOERS_PATH"
rm -f -- \
  /usr/local/sbin/osmanager-start \
  /usr/local/sbin/osmanager-stop \
  /usr/local/sbin/osmanager-restart \
  /usr/local/sbin/osmanager-status \
  /usr/local/sbin/osmanager-configure-sudo \
  /usr/local/sbin/osmanager-grant-path \
  /usr/local/sbin/osmanager-service-helper
rm -f -- /etc/sudoers.d/osmanager-helper /etc/sudoers.d/osmanager-managed-*

if [[ "$PURGE" -eq 1 ]]; then
  info "彻底删除配置和数据"
  rm -rf -- "$config_dir" "$data_dir"
  if id "$SERVICE_USER" >/dev/null 2>&1; then userdel "$SERVICE_USER"; fi
  if getent group "$SERVICE_GROUP" >/dev/null 2>&1; then groupdel "$SERVICE_GROUP" || info "用户组仍被使用，已保留：$SERVICE_GROUP"; fi
else
  info "已保留配置：$config_dir"
  info "已保留数据：$data_dir"
  info "如需彻底删除，请从安装包执行：sudo ./uninstall.sh --purge"
fi

rm -f -- /usr/local/sbin/osmanager-uninstall
info "OSManager 已卸载"

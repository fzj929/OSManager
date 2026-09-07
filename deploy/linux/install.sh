#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE_NAME="${SERVICE_NAME:-osmanager}"
SERVICE_USER="${SERVICE_USER:-osmanager}"
SERVICE_GROUP="${SERVICE_GROUP:-osmanager}"
INSTALL_ROOT="${INSTALL_ROOT:-/opt/osmanager}"
CONFIG_DIR="${CONFIG_DIR:-/etc/osmanager}"
DATA_DIR="${DATA_DIR:-/var/lib/osmanager}"
PACKAGE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
VERSION="$(tr -d '\r\n' < "$PACKAGE_DIR/VERSION")"
RELEASE_ID="${VERSION}-$(date -u +%Y%m%d%H%M%S)"
RELEASE_DIR="$INSTALL_ROOT/releases/$RELEASE_ID"
CURRENT_LINK="$INSTALL_ROOT/current"
UNIT_PATH="/etc/systemd/system/$SERVICE_NAME.service"

fail() { echo "[ERROR] $*" >&2; exit 1; }
info() { echo "[OSManager] $*"; }

[[ "$(id -u)" -eq 0 ]] || fail "请使用 sudo ./install.sh 运行"
[[ "$(uname -s)" == "Linux" ]] || fail "该安装脚本只支持 Linux"
command -v systemctl >/dev/null || fail "未检测到 systemd/systemctl"
command -v sudo >/dev/null || fail "未检测到 sudo"
command -v visudo >/dev/null || fail "未检测到 visudo"
command -v dotnet >/dev/null || fail "未检测到 dotnet，请先安装 ASP.NET Core Runtime 8"
DOTNET_BIN="$(readlink -f -- "$(command -v dotnet)")"
SYSTEMCTL_BIN="$(readlink -f -- "$(command -v systemctl)")"
VISUDO_BIN="$(readlink -f -- "$(command -v visudo)")"
[[ "$DOTNET_BIN" == /* && -x "$DOTNET_BIN" ]] || fail "无法解析 dotnet 可执行文件：$DOTNET_BIN"
dotnet --list-runtimes | grep -Eq '^Microsoft\.AspNetCore\.App 8\.' || fail "未检测到 Microsoft.AspNetCore.App 8.x Runtime"
[[ -f "$PACKAGE_DIR/app/OSManager.Api.dll" ]] || fail "发布包不完整：缺少 app/OSManager.Api.dll"

info "创建服务账户和目录"
if ! getent group "$SERVICE_GROUP" >/dev/null; then groupadd --system "$SERVICE_GROUP"; fi
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --gid "$SERVICE_GROUP" --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
fi
JOURNAL_GROUP_LINE=""
if getent group systemd-journal >/dev/null; then
  JOURNAL_GROUP_LINE="SupplementaryGroups=systemd-journal"
else
  info "警告：系统不存在 systemd-journal 用户组，Journal 日志可能需要额外授权"
fi
install -d -o root -g root -m 0755 "$INSTALL_ROOT" "$INSTALL_ROOT/releases"
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$DATA_DIR"
install -d -o root -g "$SERVICE_GROUP" -m 0750 "$CONFIG_DIR"

info "安装版本 $RELEASE_ID"
install -d -o root -g root -m 0755 "$RELEASE_DIR"
cp -a "$PACKAGE_DIR/app/." "$RELEASE_DIR/"
chown -R root:root "$RELEASE_DIR"
find "$RELEASE_DIR" -type d -exec chmod 0755 {} +
find "$RELEASE_DIR" -type f -exec chmod 0644 {} +
[[ -f "$RELEASE_DIR/OSManager.Api" ]] && chmod 0755 "$RELEASE_DIR/OSManager.Api"

if [[ ! -f "$CONFIG_DIR/appsettings.Production.json" ]]; then
  install -o root -g "$SERVICE_GROUP" -m 0640 "$PACKAGE_DIR/appsettings.Production.example.json" "$CONFIG_DIR/appsettings.Production.json"
  info "已创建配置：$CONFIG_DIR/appsettings.Production.json"
else
  info "保留现有配置：$CONFIG_DIR/appsettings.Production.json"
fi
ln -sfn "$CONFIG_DIR/appsettings.Production.json" "$RELEASE_DIR/appsettings.Production.json"

PREVIOUS_TARGET=""
if [[ -L "$CURRENT_LINK" ]]; then PREVIOUS_TARGET="$(readlink -f "$CURRENT_LINK")"; fi
ln -sfn "$RELEASE_DIR" "$CURRENT_LINK.new"
mv -Tf "$CURRENT_LINK.new" "$CURRENT_LINK"

info "安装 systemd 服务"
sed \
  -e "s|__SERVICE_USER__|$SERVICE_USER|g" \
  -e "s|__SERVICE_GROUP__|$SERVICE_GROUP|g" \
  -e "s|__INSTALL_ROOT__|$INSTALL_ROOT|g" \
  -e "s|__DATA_DIR__|$DATA_DIR|g" \
  -e "s|__DOTNET_BIN__|$DOTNET_BIN|g" \
  -e "s|__JOURNAL_GROUP_LINE__|$JOURNAL_GROUP_LINE|g" \
  "$PACKAGE_DIR/osmanager.service.template" > "$UNIT_PATH.tmp"
chown root:root "$UNIT_PATH.tmp"
chmod 0644 "$UNIT_PATH.tmp"
mv -f "$UNIT_PATH.tmp" "$UNIT_PATH"
systemctl daemon-reload
systemctl enable "$SERVICE_NAME.service" >/dev/null
install -o root -g root -m 0755 "$PACKAGE_DIR/start.sh" "/usr/local/sbin/osmanager-start"
install -o root -g root -m 0755 "$PACKAGE_DIR/stop.sh" "/usr/local/sbin/osmanager-stop"
install -o root -g root -m 0755 "$PACKAGE_DIR/restart.sh" "/usr/local/sbin/osmanager-restart"
install -o root -g root -m 0755 "$PACKAGE_DIR/status.sh" "/usr/local/sbin/osmanager-status"
install -o root -g root -m 0755 "$PACKAGE_DIR/uninstall.sh" "/usr/local/sbin/osmanager-uninstall"
install -o root -g root -m 0755 "$PACKAGE_DIR/configure-sudo.sh" "/usr/local/sbin/osmanager-configure-sudo"
install -o root -g root -m 0755 "$PACKAGE_DIR/grant-path.sh" "/usr/local/sbin/osmanager-grant-path"
sed \
  -e "s|__SERVICE_USER__|$SERVICE_USER|g" \
  -e "s|__SERVICE_GROUP__|$SERVICE_GROUP|g" \
  -e "s|__DOTNET_BIN__|$DOTNET_BIN|g" \
  -e "s|__SYSTEMCTL_BIN__|$SYSTEMCTL_BIN|g" \
  -e "s|__VISUDO_BIN__|$VISUDO_BIN|g" \
  "$PACKAGE_DIR/managed-service-helper.sh.template" > "/usr/local/sbin/osmanager-service-helper"
chown root:root /usr/local/sbin/osmanager-service-helper
chmod 0755 /usr/local/sbin/osmanager-service-helper
helper_sudoers_tmp="$(mktemp /etc/sudoers.d/osmanager-helper.XXXXXX)"
{
  echo "# Allow OSManager to invoke its validated managed-service helper"
  echo "$SERVICE_USER ALL=(root) NOPASSWD: /usr/local/sbin/osmanager-service-helper *"
} > "$helper_sudoers_tmp"
chmod 0440 "$helper_sudoers_tmp"
"$VISUDO_BIN" -cf "$helper_sudoers_tmp" >/dev/null
install -o root -g root -m 0440 "$helper_sudoers_tmp" /etc/sudoers.d/osmanager-helper
rm -f -- "$helper_sudoers_tmp"

info "启动 OSManager"
if ! systemctl restart "$SERVICE_NAME.service"; then
  if [[ -n "$PREVIOUS_TARGET" && -d "$PREVIOUS_TARGET" ]]; then
    info "启动失败，恢复上一个版本"
    ln -sfn "$PREVIOUS_TARGET" "$CURRENT_LINK.new"
    mv -Tf "$CURRENT_LINK.new" "$CURRENT_LINK"
    systemctl restart "$SERVICE_NAME.service" || true
  fi
  systemctl status "$SERVICE_NAME.service" --no-pager || true
  fail "安装后的服务启动失败"
fi

sleep 2
if ! systemctl is-active --quiet "$SERVICE_NAME.service"; then
  systemctl status "$SERVICE_NAME.service" --no-pager -l || true
  journalctl -u "$SERVICE_NAME.service" -n 50 --no-pager -o cat || true
  fail "服务未进入 active 状态"
fi
info "安装成功。配置文件：$CONFIG_DIR/appsettings.Production.json"
info "dotnet 路径：$DOTNET_BIN"
info "服务地址默认：http://0.0.0.0:5080（请使用防火墙限制来源或配置 HTTPS 反向代理）"
info "生命周期命令：osmanager-start / osmanager-stop / osmanager-restart / osmanager-status"
info "卸载命令：sudo osmanager-uninstall（保留配置和数据）"
info "授权管理目录：sudo osmanager-grant-path /opt/apps/app-a"
info "授权受管服务：sudo osmanager-configure-sudo app-a.service"
info "Web 服务发布已启用：管理员可上传 ZIP 并安装受管 systemd 服务"
info "修改配置或目录授权后执行：sudo osmanager-restart"

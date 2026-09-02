# OSManager

OSManager 是部署在单台 Linux 主机上的本地 Web 运维控制台。它只允许操作配置白名单中的目录和 systemd 服务，提供文件发布与回滚、日志搜索、配置审批、服务/进程管理、Journal 查询和主机监控。

## 技术栈

- ASP.NET Core 8 Minimal API
- Vue 3 + TypeScript + Vite
- SQLite（WAL 模式）
- ECharts

## 本地运行

```bash
cd web
npm install
npm run build

cd ../src/OSManager.Api
dotnet restore
dotnet run
```

访问 `http://localhost:5080`。首次启动会创建：

- 用户名：`admin`
- 初始密码：`ChangeMe!123`

登录后请在“用户管理”立即修改密码。

在 Windows 上，systemd 和 Journal 命令自动进入演示模式，不会执行系统命令。

## Linux 配置

编辑 `src/OSManager.Api/appsettings.json`：

```json
{
  "OSManager": {
    "DataDirectory": "/var/lib/osmanager",
    "MaxUploadBytes": 104857600,
    "MaxExtractedBytes": 524288000,
    "MaxArchiveEntries": 5000,
    "LogSearchMaxDays": 7,
    "DemoMode": false,
    "ManagedDirectories": [
      {
        "Id": "app-a",
        "Name": "应用 A",
        "Path": "/opt/apps/app-a",
        "CanUpload": true,
        "RelatedService": "app-a.service"
      },
      {
        "Id": "app-a-logs",
        "Name": "应用 A 日志",
        "Path": "/var/log/app-a",
        "CanUpload": false
      }
    ],
    "ManagedServices": ["app-a.service", "nginx.service"]
  }
}
```

目录和服务名称采用精确白名单。前端传入的路径会被规范化并检查是否仍在根目录内；符号链接会被拒绝。

## 生成 Linux 发布包

在 Windows/PowerShell 中执行：

```powershell
.\scripts\publish-linux.ps1 -Version 1.0.0
```

生成文件：`artifacts/osmanager-1.0.0-linux-x64.tar.gz`。该包是依赖框架模式，Linux 服务器需要安装 `Microsoft.AspNetCore.App 8.x`，不包含 .NET Runtime。

复制到 Linux 后：

```bash
tar -xzf osmanager-1.0.0-linux-x64.tar.gz
cd osmanager-1.0.0-linux-x64
sudo bash install.sh
```

安装完成后的管理命令：

```bash
sudo osmanager-start
sudo osmanager-stop
sudo osmanager-restart
osmanager-status
```

授权 OSManager 读写具体管理目录，并为受管服务生成精确 sudo 规则：

```bash
sudo osmanager-grant-path /opt/apps/app-a /etc/app-a
sudo osmanager-configure-sudo app-a.service nginx.service
sudo osmanager-restart
```

生产配置位于 `/etc/osmanager/appsettings.Production.json`。重复运行新版本的 `install.sh` 会保留该配置，使用版本目录进行原子升级；新版本启动失败时自动恢复上一个版本。

默认监听 `0.0.0.0:5080`。请使用主机防火墙限制访问来源；正式环境建议通过 Nginx/Caddy 提供 HTTPS，并将配置中的监听地址改为 `127.0.0.1:5080`。

## 手动发布

```bash
dotnet publish src/OSManager.Api/OSManager.Api.csproj -c Release -o publish
sudo mkdir -p /opt/osmanager /var/lib/osmanager
sudo cp -r publish/* /opt/osmanager/
sudo useradd --system --home /var/lib/osmanager --shell /usr/sbin/nologin osmanager
sudo chown -R osmanager:osmanager /var/lib/osmanager
sudo cp deploy/osmanager.service /etc/systemd/system/osmanager.service
sudo systemctl daemon-reload
sudo systemctl enable --now osmanager
```

根据实际白名单修改并安装 `deploy/osmanager-sudoers.example`。不要授予 OSManager 任意 root 命令权限。

## 安全说明

- 服务进程不应以 root 运行。
- 建议通过 Nginx/Caddy 配置 HTTPS，并只在内网、VPN 或堡垒机后开放。
- ZIP 限制为 100 MB，默认解压上限 500 MB、5000 个条目，并拒绝目录穿越。
- 服务操作不经过 shell，参数通过 `ProcessStartInfo.ArgumentList` 传递。
- 配置修改必须提交审批，提交者不能审批自己的变更，应用前会检查内容版本冲突并备份原文件。
- 停止服务、终止进程、发布、回滚、审批和配置应用都会写入审计表。
- 生产环境建议定期备份 `/var/lib/osmanager`。

## 验证

```bash
dotnet build src/OSManager.Api/OSManager.Api.csproj
npm run build --prefix web
```

浏览器冒烟脚本位于 `tests/ui_smoke.py`，会验证登录、仪表盘、文件和日志页面。

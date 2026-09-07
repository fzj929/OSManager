# OSManager

OSManager 是部署在单台 Linux 主机上的本地 Web 运维控制台。它只允许操作配置白名单中的目录和 systemd 服务，提供文件发布与回滚、日志搜索、配置文件编辑、服务/进程管理、Journal 查询和主机监控。

CPU 和内存指标每秒采样、每秒刷新，内存中保留最近 5 分钟（300 个采样点）的历史曲线。

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

管理目录由管理员在“资源设置”维护，systemd 服务在“服务管理”维护，修改保存在 SQLite 中并立即生效。默认配置不再包含任何样例目录或样例服务：

```json
{
  "OSManager": {
    "DataDirectory": "/var/lib/osmanager",
    "MaxUploadBytes": 104857600,
    "MaxServicePackageBytes": 524288000,
    "MaxExtractedBytes": 524288000,
    "MaxArchiveEntries": 5000,
    "LogSearchMaxDays": 7,
    "DemoMode": false,
    "ManagedDirectories": [],
    "ManagedServices": []
  }
}
```

第一次初始化完成后，即使页面中删除了全部目录或服务，重启也不会重新导入配置种子。目录和服务名称采用精确白名单；前端传入的路径会被规范化并检查是否仍在根目录内，符号链接会被拒绝。

“备份排除文件夹”填写相对于管理目录的路径，多个目录使用分号分隔，例如 `logs;files;download;data/cache`。ZIP 发布时这些目录不会复制到备份中；回滚时也不会删除或恢复这些目录，因此回滚之后仍保留它们当时的内容。每次发布会把当时的排除规则写入对应备份，后续修改资源配置不会改变旧发布的回滚行为。普通文件发布仍只备份被替换的单个文件。

文件发布页面也允许删除受管目录中的文件或文件夹。删除确认框提供“直接删除”和“备份后删除”：直接删除不生成恢复点；备份后删除会完整备份所选内容（不应用发布备份排除规则），并可在“变更与备份”列表中点击“还原”。为避免误操作，管理根目录不能被删除。

文件列表的操作列提供下载、替换和删除。文件夹下载会实时打包为 ZIP；替换文件时上传一个新文件，替换文件夹时上传 ZIP。替换操作始终完整备份原目标，文件夹会在解压 ZIP 前清空，避免残留旧文件，并可从“变更与备份”列表回滚。

普通文件日志使用独立“日志源”，不要求日志位于程序发布目录下。可以在资源设置中配置目录：

```text
名称：IHR 日志
路径：/app/publish-v2.2/logs
匹配：*.log;*.txt
```

也可以把路径直接配置为 `/app/publish-v2.2/logs/SelfPrinting-20260903.txt`，此时匹配规则会被忽略。确保服务账户有读取权限：

```bash
sudo osmanager-grant-path /app/publish-v2.2/logs
```

服务统一在“服务管理”中新增，有两种方式：

- “添加已有服务”会检查 systemd 已经加载该服务，再把它加入 OSManager 管理范围并自动生成该服务的精确 sudo 操作规则。
- “上传 ZIP 安装服务”会备份并解压到指定运行目录，检查主程序，生成 systemd unit，加入管理范围并立即启动。主程序可以是依赖框架的 `.dll` 或原生可执行文件；业务服务以 `osmanager` 受限账户运行，不以 root 运行。

ZIP 安装会同时创建一个可用于“文件发布”和“配置文件”编辑的管理目录。填写的“备份排除文件夹”会用于本次安装以及以后通过文件发布页面进行的 ZIP 更新和回滚。

如果服务管理显示“未安装”或 `Unit xxx.service not loaded`，说明 systemd 未发现对应 unit；使用“添加已有服务”前请先确认并重新加载：

```bash
sudo test -f /etc/systemd/system/ihr.service
sudo systemctl daemon-reload
sudo systemctl status ihr.service
```

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
sudo osmanager-uninstall
```

`osmanager-uninstall` 会停止并删除 systemd 服务、程序版本、管理命令和 sudo 规则，默认保留 `/etc/osmanager` 配置以及 `/var/lib/osmanager` 数据库和备份。确认不再需要任何数据时，可从解压后的安装包执行 `sudo ./uninstall.sh --purge` 进行彻底卸载。

新版本安装脚本会安装 root 所有、仅允许校验参数的服务安装助手。升级旧版 OSManager 后必须重新执行发布包中的 `install.sh`，服务管理页面才能添加已有服务或安装新服务。

手工创建管理目录时，仍可用下面的命令授权 OSManager 读写；旧版配置也可以继续手工重建精确 sudo 规则：

```bash
sudo osmanager-grant-path /opt/apps/app-a /etc/app-a
sudo osmanager-configure-sudo app-a.service nginx.service
sudo osmanager-restart
```

`osmanager-configure-sudo` 每次都会按照参数重建精确授权，因此使用该兼容命令时应一次传入全部受管服务。正常情况下直接在页面添加服务即可自动授权；对于旧数据库中已有的服务，首次启动、停止或重启时会自动补齐该服务的精确 sudo 规则并重试。旧版本升级后需要重新执行一次新版 `install.sh`，以安装 root 所有的参数校验助手。安装生成的 OSManager systemd unit 会把进程加入 `systemd-journal` 附加组，以便查询和实时读取 Journal；修改 unit 后需要重新加载并重启 OSManager。

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
- 普通文件/ZIP 发布限制为 100 MB；服务安装 ZIP 限制为 500 MB。默认解压上限为 500 MB、5000 个条目，并拒绝目录穿越。
- 页面安装的业务 systemd unit 只允许使用普通相对主程序路径，并拒绝覆盖不是 OSManager 创建的同名 unit。
- 服务安装助手由 root 持有，Web 进程只能通过无 shell 参数调用它；添加已有服务后仍按具体服务名称生成 start/stop/restart 精确授权。
- 服务操作不经过 shell，参数通过 `ProcessStartInfo.ArgumentList` 传递。
- 配置页面会自动发现 `.json`、`.ini`、`.config`、`.yaml`、`.yml`、`.conf` 文件，也允许手动输入相对路径；保存前检查内容版本并备份原文件。
- 停止服务、终止进程、发布、回滚和配置保存都会写入审计表。
- 生产环境建议定期备份 `/var/lib/osmanager`。

## 验证

```bash
dotnet build src/OSManager.Api/OSManager.Api.csproj
npm run build --prefix web
```

浏览器冒烟脚本位于 `tests/ui_smoke.py`，会验证登录、仪表盘、文件和日志页面。

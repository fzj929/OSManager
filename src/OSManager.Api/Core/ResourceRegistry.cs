using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OSManager.Api.Core;

public sealed class ResourceRegistry(Database db, IOptions<OSManagerOptions> options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ManagedDirectory[] _directories = [];
    private string[] _services = [];
    private ManagedLogSource[] _logSources = [];

    public IReadOnlyList<ManagedDirectory> Directories => _directories;
    public IReadOnlyList<string> Services => _services;
    public IReadOnlyList<ManagedLogSource> LogSources => _logSources;

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var state = await db.QueryAsync("SELECT COUNT(*) AS count FROM resource_registry_meta WHERE id=1");
            if (Convert.ToInt64(state[0]["count"]) == 0)
            {
                foreach (var item in options.Value.ManagedDirectories) await SaveDirectoryCoreAsync(item);
                foreach (var service in options.Value.ManagedServices) await SaveServiceCoreAsync(service);
                await db.ExecuteAsync("INSERT INTO resource_registry_meta(id,initialized) VALUES(1,1)");
            }
            await ReloadCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveDirectoryAsync(ManagedDirectory item)
    {
        ValidateDirectory(item); await _gate.WaitAsync();
        try { await SaveDirectoryCoreAsync(item); await ReloadCoreAsync(); }
        finally { _gate.Release(); }
    }

    public async Task DeleteDirectoryAsync(string id)
    {
        await _gate.WaitAsync();
        try { await db.ExecuteAsync("DELETE FROM managed_directories WHERE id=$id", ("$id", id)); await ReloadCoreAsync(); }
        finally { _gate.Release(); }
    }

    public async Task SaveServiceAsync(string service)
    {
        ValidateService(service); await _gate.WaitAsync();
        try { await SaveServiceCoreAsync(service); await ReloadCoreAsync(); }
        finally { _gate.Release(); }
    }

    public async Task DeleteServiceAsync(string service)
    {
        await _gate.WaitAsync();
        try { await db.ExecuteAsync("DELETE FROM managed_services WHERE name=$name", ("$name", service)); await ReloadCoreAsync(); }
        finally { _gate.Release(); }
    }

    public async Task SaveLogSourceAsync(ManagedLogSource source)
    {
        ValidateLogSource(source); await _gate.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            await db.ExecuteAsync("INSERT INTO managed_log_sources(id,name,path,file_pattern,created_at,updated_at) VALUES($id,$name,$path,$pattern,$now,$now) ON CONFLICT(id) DO UPDATE SET name=$name,path=$path,file_pattern=$pattern,updated_at=$now", ("$id", source.Id.Trim()), ("$name", source.Name.Trim()), ("$path", source.Path.Trim()), ("$pattern", source.FilePattern.Trim()), ("$now", now));
            await ReloadCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteLogSourceAsync(string id)
    {
        await _gate.WaitAsync();
        try { await db.ExecuteAsync("DELETE FROM managed_log_sources WHERE id=$id", ("$id", id)); await ReloadCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task ReloadCoreAsync()
    {
        var directories = await db.QueryAsync("SELECT id,name,path,can_upload,related_service,backup_excludes FROM managed_directories ORDER BY name,id");
        _directories = directories.Select(x => new ManagedDirectory(x["id"]!.ToString()!, x["name"]!.ToString()!, x["path"]!.ToString()!, Convert.ToInt64(x["can_upload"]) == 1, x["related_service"]?.ToString(), x["backup_excludes"]?.ToString() ?? "")).ToArray();
        var services = await db.QueryAsync("SELECT name FROM managed_services ORDER BY name");
        _services = services.Select(x => x["name"]!.ToString()!).ToArray();
        var logs = await db.QueryAsync("SELECT id,name,path,file_pattern FROM managed_log_sources ORDER BY name,id");
        _logSources = logs.Select(x => new ManagedLogSource(x["id"]!.ToString()!, x["name"]!.ToString()!, x["path"]!.ToString()!, x["file_pattern"]!.ToString()!)).ToArray();
    }

    private async Task SaveDirectoryCoreAsync(ManagedDirectory item)
    {
        ValidateDirectory(item); var now = DateTimeOffset.UtcNow.ToString("O");
        await db.ExecuteAsync("INSERT INTO managed_directories(id,name,path,can_upload,related_service,backup_excludes,created_at,updated_at) VALUES($id,$name,$path,$upload,$service,$excludes,$now,$now) ON CONFLICT(id) DO UPDATE SET name=$name,path=$path,can_upload=$upload,related_service=$service,backup_excludes=$excludes,updated_at=$now", ("$id", item.Id.Trim()), ("$name", item.Name.Trim()), ("$path", item.Path.Trim()), ("$upload", item.CanUpload ? 1 : 0), ("$service", string.IsNullOrWhiteSpace(item.RelatedService) ? null : item.RelatedService.Trim()), ("$excludes", NormalizeBackupExcludes(item.BackupExcludes)), ("$now", now));
    }

    private async Task SaveServiceCoreAsync(string service)
    {
        ValidateService(service); await db.ExecuteAsync("INSERT OR IGNORE INTO managed_services(name,created_at) VALUES($name,$now)", ("$name", service.Trim()), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private static void ValidateDirectory(ManagedDirectory item)
    {
        if (!Regex.IsMatch(item.Id ?? "", "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$")) throw new ArgumentException("目录标识只能包含字母、数字、横线和下划线，最长 64 个字符");
        if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100) throw new ArgumentException("目录名称不能为空且不能超过 100 个字符");
        if (string.IsNullOrWhiteSpace(item.Path) || (!item.Path.StartsWith('/') && !item.Path.StartsWith("${CONTENT_ROOT}/", StringComparison.Ordinal))) throw new ArgumentException("管理目录必须是 Linux 绝对路径");
        if (item.Path.Contains('\0')) throw new ArgumentException("目录路径无效");
        if (!string.IsNullOrWhiteSpace(item.RelatedService)) ValidateService(item.RelatedService);
        _ = NormalizeBackupExcludes(item.BackupExcludes);
    }

    public static string[] ParseBackupExcludes(string? value) => (value ?? "")
        .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.Replace('\\', '/').Trim('/'))
        .Where(x => x.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string NormalizeBackupExcludes(string? value)
    {
        var rawEntries = (value ?? "")
            .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Replace('\\', '/'))
            .ToArray();
        if (rawEntries.Any(x => x.StartsWith('/') || Regex.IsMatch(x, "^[A-Za-z]:") || x.Split('/').Any(segment => segment is "." or "..") || x.Contains('\0')))
            throw new ArgumentException("备份排除项必须是管理目录下的相对文件夹，例如：logs;files;download");
        var entries = rawEntries.Select(x => x.Trim('/')).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return string.Join(';', entries);
    }

    private static void ValidateService(string service)
    {
        if (!Regex.IsMatch(service?.Trim() ?? "", "^[A-Za-z0-9_.@:-]+\\.service$")) throw new ArgumentException("systemd 服务名无效，必须以 .service 结尾");
    }

    private static void ValidateLogSource(ManagedLogSource source)
    {
        if (!Regex.IsMatch(source.Id ?? "", "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$")) throw new ArgumentException("日志源标识格式无效");
        if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 100) throw new ArgumentException("日志源名称不能为空且不能超过 100 个字符");
        if (string.IsNullOrWhiteSpace(source.Path) || (!source.Path.StartsWith('/') && !source.Path.StartsWith("${CONTENT_ROOT}/", StringComparison.Ordinal))) throw new ArgumentException("日志路径必须是 Linux 绝对路径");
        if (source.Path.StartsWith("${CONTENT_ROOT}/", StringComparison.Ordinal) && source.Path[16..].Split(['/', '\\']).Contains("..")) throw new ArgumentException("日志路径不能超出应用目录");
        var patterns = source.FilePattern.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0 || patterns.Any(x => x.Contains('/') || x.Contains('\\') || x.Contains(".."))) throw new ArgumentException("文件匹配规则无效，例如：*.log;*.txt");
    }
}

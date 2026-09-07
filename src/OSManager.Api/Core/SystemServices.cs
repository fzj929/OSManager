using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OSManager.Api.Core;

public sealed record CommandResult(int ExitCode, string Output, string Error);

public sealed class CommandRunner(IOptions<OSManagerOptions> options)
{
    public async Task<CommandResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || options.Value.DemoMode) return new(0, "演示模式：未执行系统命令", "");
        var psi = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动系统命令");
        var outputTask = process.StandardOutput.ReadToEndAsync(ct); var errorTask = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct); timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { process.Kill(true); throw new TimeoutException("系统命令执行超时"); }
        return new(process.ExitCode, await outputTask, await errorTask);
    }
}

public sealed class FileOperations(PathPolicy paths, Database db, IConfiguration config)
{
    private readonly string _backupRoot = Path.GetFullPath(Path.Combine(config["OSManager:DataDirectory"] ?? "data", "backups"));

    public object Browse(string rootId, string? path)
    {
        var resolved = paths.Resolve(rootId, path);
        if (!Directory.Exists(resolved.FullPath)) throw new ArgumentException("目标不是目录");
        var rootPath = Path.GetFullPath(resolved.Root.Path);
        var items = new DirectoryInfo(resolved.FullPath).EnumerateFileSystemInfos().Where(x => !x.Attributes.HasFlag(FileAttributes.ReparsePoint)).Select(x => new
        {
            x.Name,
            path = Path.GetRelativePath(rootPath, x.FullName).Replace('\\', '/'),
            isDirectory = x is DirectoryInfo,
            size = x is FileInfo f ? f.Length : 0,
            modified = x.LastWriteTimeUtc
        }).OrderByDescending(x => x.isDirectory).ThenBy(x => x.Name).ToList();
        return new { root = resolved.Root, path = path ?? "", items };
    }

    public async Task<DeploymentResult> DeployAsync(string rootId, string relativePath, IFormFile upload, AuthUser user, CancellationToken ct, long? maxUploadBytes = null)
    {
        if (upload.Length == 0) throw new ArgumentException("上传文件为空");
        var uploadLimit = maxUploadBytes ?? long.Parse(config["OSManager:MaxUploadBytes"] ?? "104857600");
        if (upload.Length > uploadLimit) throw new ArgumentException($"上传文件超过 {uploadLimit / 1024 / 1024} MB 限制");
        var resolved = paths.Resolve(rootId, relativePath, false);
        if (!resolved.Root.CanUpload) throw new UnauthorizedAccessException("该目录不允许更新");
        Directory.CreateDirectory(_backupRoot);
        var operation = Guid.NewGuid().ToString("N");
        var backup = Path.Combine(_backupRoot, operation);
        Directory.CreateDirectory(backup);
        var temp = Path.Combine(_backupRoot, operation + ".upload");
        try
        {
            await using (var target = File.Create(temp)) await upload.CopyToAsync(target, ct);
            string hash;
            await using (var hashInput = File.OpenRead(temp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, ct));
            if (upload.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(resolved.FullPath);
                await File.WriteAllTextAsync(Path.Combine(backup, ".type"), "directory", ct);
                var excludes = ResourceRegistry.ParseBackupExcludes(resolved.Root.BackupExcludes);
                await File.WriteAllLinesAsync(Path.Combine(backup, ".excludes"), excludes, ct);
                var excludedPaths = ResolveBackupExclusions(resolved.Root, excludes);
                await BackupDirectoryAsync(resolved.FullPath, Path.Combine(backup, "content"), excludedPaths, ct);
                await SafeExtractAsync(temp, resolved.FullPath, ct);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolved.FullPath)!);
                if (File.Exists(resolved.FullPath)) { File.Copy(resolved.FullPath, Path.Combine(backup, "original"), true); await File.WriteAllTextAsync(Path.Combine(backup, ".type"), "file-existing", ct); }
                else await File.WriteAllTextAsync(Path.Combine(backup, ".type"), "file-new", ct);
                File.Move(temp, resolved.FullPath, true);
            }
            var id = await db.AddDeploymentAsync(user.UserName, rootId, relativePath, backup, hash, resolved.Root.RelatedService);
            return new DeploymentResult(id, hash, resolved.Root.RelatedService, !string.IsNullOrWhiteSpace(resolved.Root.RelatedService));
        }
        catch { if (Directory.Exists(backup)) Directory.Delete(backup, true); throw; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<DeploymentResult> ReplaceAsync(string rootId, string relativePath, IFormFile upload, AuthUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidOperationException("禁止替换管理根目录");
        if (upload.Length == 0) throw new ArgumentException("上传文件为空");
        var uploadLimit = long.Parse(config["OSManager:MaxUploadBytes"] ?? "104857600");
        if (upload.Length > uploadLimit) throw new ArgumentException($"上传文件超过 {uploadLimit / 1024 / 1024} MB 限制");
        var resolved = paths.Resolve(rootId, relativePath);
        if (!resolved.Root.CanUpload) throw new UnauthorizedAccessException("该目录不允许更新");
        var isDirectory = Directory.Exists(resolved.FullPath);
        if (isDirectory && !upload.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("替换文件夹时请选择 ZIP 压缩包");

        Directory.CreateDirectory(_backupRoot);
        var operationId = Guid.NewGuid().ToString("N");
        var backup = Path.Combine(_backupRoot, operationId);
        var temp = Path.Combine(_backupRoot, operationId + ".replace");
        Directory.CreateDirectory(backup);
        var backupComplete = false;
        try
        {
            await using (var output = File.Create(temp)) await upload.CopyToAsync(output, ct);
            string hash;
            await using (var hashInput = File.OpenRead(temp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, ct));
            if (isDirectory)
            {
                await File.WriteAllTextAsync(Path.Combine(backup, ".type"), "directory", ct);
                await File.WriteAllTextAsync(Path.Combine(backup, ".excludes"), "", ct);
                await BackupDirectoryAsync(resolved.FullPath, Path.Combine(backup, "content"), [], ct);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(backup, ".type"), "file-existing", ct);
                File.Copy(resolved.FullPath, Path.Combine(backup, "original"), true);
            }
            backupComplete = true;
            DeleteTarget(resolved.FullPath);
            if (isDirectory)
            {
                Directory.CreateDirectory(resolved.FullPath);
                await SafeExtractAsync(temp, resolved.FullPath, ct);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolved.FullPath)!);
                File.Move(temp, resolved.FullPath, true);
            }
            var id = await db.AddDeploymentAsync(user.UserName, rootId, relativePath, backup, hash, resolved.Root.RelatedService, "Replace");
            return new DeploymentResult(id, hash, resolved.Root.RelatedService, !string.IsNullOrWhiteSpace(resolved.Root.RelatedService));
        }
        catch
        {
            if (backupComplete) await RestoreBackupAsync(resolved, backup, CancellationToken.None);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<string> CreateDirectoryArchiveAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var resolved = paths.Resolve(rootId, relativePath);
        if (!Directory.Exists(resolved.FullPath)) throw new ArgumentException("目标不是文件夹");
        Directory.CreateDirectory(_backupRoot);
        var archivePath = Path.Combine(_backupRoot, $"download-{Guid.NewGuid():N}.zip");
        try
        {
            await using var output = File.Create(archivePath);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var directory in Directory.EnumerateDirectories(resolved.FullPath, "*", enumeration))
            {
                var entryName = Path.GetRelativePath(resolved.FullPath, directory).Replace('\\', '/').TrimEnd('/') + "/";
                archive.CreateEntry(entryName);
            }
            foreach (var file in Directory.EnumerateFiles(resolved.FullPath, "*", enumeration))
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(resolved.FullPath, file).Replace('\\', '/'), CompressionLevel.Fastest);
                await using var input = File.OpenRead(file);
                await using var entryOutput = entry.Open();
                await input.CopyToAsync(entryOutput, ct);
            }
            return archivePath;
        }
        catch
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            throw;
        }
    }

    public async Task RollbackAsync(long id, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT root_id,relative_path,backup_path,status FROM deployments WHERE id=$id", ("$id", id));
        if (rows.Count == 0) throw new KeyNotFoundException("发布记录不存在");
        if (rows[0]["status"]?.ToString() == "RolledBack") throw new InvalidOperationException("该发布已经回滚");
        var resolved = paths.Resolve(rows[0]["root_id"]!.ToString()!, rows[0]["relative_path"]!.ToString()!, false);
        var backup = rows[0]["backup_path"]!.ToString()!;
        if (!Directory.Exists(backup)) throw new FileNotFoundException("备份已经不存在");
        await RestoreBackupAsync(resolved, backup, ct);
        await db.ExecuteAsync("UPDATE deployments SET status='RolledBack',rolled_back_at=$t WHERE id=$id", ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));
    }

    public async Task<FileDeleteResult> DeleteAsync(string rootId, string relativePath, bool createBackup, AuthUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidOperationException("禁止删除管理根目录");
        var resolved = paths.Resolve(rootId, relativePath);
        if (!resolved.Root.CanUpload) throw new UnauthorizedAccessException("该目录不允许删除文件");
        if (!createBackup)
        {
            DeleteTarget(resolved.FullPath);
            return new FileDeleteResult(false, null, resolved.Root.RelatedService, !string.IsNullOrWhiteSpace(resolved.Root.RelatedService));
        }

        Directory.CreateDirectory(_backupRoot);
        var operationId = Guid.NewGuid().ToString("N");
        var backup = Path.Combine(_backupRoot, operationId);
        Directory.CreateDirectory(backup);
        var isDirectory = Directory.Exists(resolved.FullPath);
        var backupComplete = false;
        try
        {
            if (isDirectory)
            {
                await File.WriteAllTextAsync(Path.Combine(backup, ".type"), "directory", ct);
                await File.WriteAllTextAsync(Path.Combine(backup, ".excludes"), "", ct);
                await BackupDirectoryAsync(resolved.FullPath, Path.Combine(backup, "content"), [], ct);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(backup, ".type"), "file-existing", ct);
                File.Copy(resolved.FullPath, Path.Combine(backup, "original"), true);
            }
            backupComplete = true;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"delete:{rootId}:{relativePath}:{operationId}")));
            DeleteTarget(resolved.FullPath);
            var id = await db.AddDeploymentAsync(user.UserName, rootId, relativePath, backup, hash, resolved.Root.RelatedService, "Delete");
            return new FileDeleteResult(true, id, resolved.Root.RelatedService, !string.IsNullOrWhiteSpace(resolved.Root.RelatedService));
        }
        catch
        {
            if (backupComplete) await RestoreBackupAsync(resolved, backup, CancellationToken.None);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            throw;
        }
    }

    private async Task RestoreBackupAsync((ManagedDirectory Root, string FullPath) resolved, string backup, CancellationToken ct)
    {
        var type = File.Exists(Path.Combine(backup, ".type")) ? await File.ReadAllTextAsync(Path.Combine(backup, ".type"), ct) : "directory";
        if (type == "file-existing")
        {
            if (Directory.Exists(resolved.FullPath)) Directory.Delete(resolved.FullPath, true);
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.FullPath)!);
            File.Copy(Path.Combine(backup, "original"), resolved.FullPath, true);
        }
        else if (type == "file-new")
        {
            if (File.Exists(resolved.FullPath)) File.Delete(resolved.FullPath);
        }
        else
        {
            var excludesFile = Path.Combine(backup, ".excludes");
            var excludes = File.Exists(excludesFile) ? await File.ReadAllLinesAsync(excludesFile, ct) : [];
            var excludedPaths = ResolveBackupExclusions(resolved.Root, excludes.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (File.Exists(resolved.FullPath)) File.Delete(resolved.FullPath);
            if (Directory.Exists(resolved.FullPath)) DeleteDirectoryContentsPreservingExclusions(resolved.FullPath, excludedPaths);
            Directory.CreateDirectory(resolved.FullPath);
            await CopyDirectoryAsync(Path.Combine(backup, "content"), resolved.FullPath, [], ct);
        }
    }

    private static void DeleteTarget(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
        else throw new FileNotFoundException("文件或目录不存在");
    }

    private string[] ResolveBackupExclusions(ManagedDirectory root, IEnumerable<string> excludes)
    {
        var rootPath = paths.ResolveConfiguredPath(root.Path, false);
        return excludes.Select(x => Path.GetFullPath(Path.Combine(rootPath, x.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
    }

    private static async Task BackupDirectoryAsync(string source, string destination, IReadOnlyCollection<string> excludedPaths, CancellationToken ct)
    { if (!Directory.Exists(source)) return; Directory.CreateDirectory(destination); await CopyDirectoryAsync(source, destination, excludedPaths, ct); }

    private static async Task CopyDirectoryAsync(string source, string destination, IReadOnlyCollection<string> excludedPaths, CancellationToken ct)
    {
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var dir in Directory.EnumerateDirectories(source, "*", enumeration))
            if (!IsExcluded(dir, excludedPaths)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", enumeration))
        {
            if (IsExcluded(file, excludedPaths)) continue;
            var dst = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            await using var input = File.OpenRead(file);
            await using var output = File.Create(dst);
            await input.CopyToAsync(output, ct);
        }
    }

    private static void DeleteDirectoryContentsPreservingExclusions(string directory, IReadOnlyCollection<string> excludedPaths)
    {
        if (IsExcluded(directory, excludedPaths)) return;
        foreach (var file in Directory.EnumerateFiles(directory))
            if (!IsExcluded(file, excludedPaths)) File.Delete(file);
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsExcluded(child, excludedPaths)) continue;
            if (ContainsExcludedPath(child, excludedPaths)) DeleteDirectoryContentsPreservingExclusions(child, excludedPaths);
            else Directory.Delete(child, true);
        }
    }

    private static bool IsExcluded(string path, IReadOnlyCollection<string> excludedPaths)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return excludedPaths.Any(excluded => full.Equals(excluded, comparison) || full.StartsWith(excluded.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison));
    }

    private static bool ContainsExcludedPath(string directory, IReadOnlyCollection<string> excludedPaths)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return excludedPaths.Any(excluded => excluded.StartsWith(prefix, comparison));
    }
    private async Task SafeExtractAsync(string archivePath, string destination, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var maxEntries = int.Parse(config["OSManager:MaxArchiveEntries"] ?? "5000"); var maxBytes = long.Parse(config["OSManager:MaxExtractedBytes"] ?? "524288000");
        if (zip.Entries.Count > maxEntries) throw new InvalidOperationException("ZIP 文件数量超过限制");
        long total = 0; var prefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            total += entry.Length; if (total > maxBytes) throw new InvalidOperationException("ZIP 解压后大小超过限制");
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName)); if (!target.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("ZIP 包含非法路径");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); await using var input = entry.Open(); await using var output = File.Create(target); await input.CopyToAsync(output, ct);
        }
    }
}

public sealed class LogSearcher(PathPolicy paths, ResourceRegistry registry, IOptions<OSManagerOptions> options)
{
    public async Task<List<object>> SearchAsync(LogSearchRequest request, CancellationToken ct)
    {
        if (request.End <= request.Start || request.End - request.Start > TimeSpan.FromDays(options.Value.LogSearchMaxDays)) throw new ArgumentException($"查询时间范围必须在 {options.Value.LogSearchMaxDays} 天内");
        var source = registry.LogSources.FirstOrDefault(x => x.Id.Equals(request.RootId, StringComparison.OrdinalIgnoreCase));
        string searchPath; string pattern;
        if (source is not null) { searchPath = paths.ResolveConfiguredPath(source.Path); pattern = source.FilePattern; }
        else { var resolved = paths.Resolve(request.RootId, request.Path); searchPath = resolved.FullPath; pattern = "*.log;*.txt"; }
        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Regex? regex = request.Regex ? new Regex(request.Query, request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) : null;
        var limit = Math.Clamp(request.Limit, 1, 10000); var results = new List<object>();
        IEnumerable<string> candidates = File.Exists(searchPath)
            ? [searchPath]
            : pattern.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SelectMany(x => Directory.EnumerateFiles(searchPath, x, SearchOption.AllDirectories)).Distinct(StringComparer.Ordinal);
        foreach (var file in candidates.Select(x => new FileInfo(x)).Where(x => x.LastWriteTimeUtc >= request.Start.UtcDateTime && x.LastWriteTimeUtc <= request.End.UtcDateTime))
        {
            var lineNo = 0; using var reader = file.OpenText();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lineNo++; bool match; try { match = regex?.IsMatch(line) ?? line.Contains(request.Query, comparison); } catch (RegexMatchTimeoutException) { throw new ArgumentException("正则表达式执行超时"); }
                if (match) results.Add(new { file = File.Exists(searchPath) ? file.Name : Path.GetRelativePath(searchPath, file.FullName), line = lineNo, content = line, modified = file.LastWriteTimeUtc });
                if (results.Count >= limit) return results;
            }
        }
        return results;
    }

    public string ResolveFile(string sourceId, string file)
    {
        var source = registry.LogSources.FirstOrDefault(x => x.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("日志源不存在");
        var sourcePath = paths.ResolveConfiguredPath(source.Path);
        if (File.Exists(sourcePath))
        {
            if (!string.Equals(file, Path.GetFileName(sourcePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new UnauthorizedAccessException("文件不属于指定日志源");
            return sourcePath;
        }
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException("日志目录不存在");
        if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("日志文件不能为空");
        var full = Path.GetFullPath(Path.Combine(sourcePath, file));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = sourcePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison) || !File.Exists(full)) throw new FileNotFoundException("日志文件不存在或不属于指定日志源");
        var patterns = source.FilePattern.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(full), OperatingSystem.IsWindows()))) throw new UnauthorizedAccessException("文件不符合日志源匹配规则");
        var cursor = full;
        while (cursor.StartsWith(prefix, comparison))
        {
            if (File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("禁止通过符号链接读取日志");
            cursor = Path.GetDirectoryName(cursor)!;
        }
        return full;
    }

    public async Task<object> ReadContextAsync(string sourceId, string file, int line, int radius, CancellationToken ct)
    {
        if (line < 1) throw new ArgumentException("日志行号无效");
        radius = Math.Clamp(radius, 1, 200);
        var full = ResolveFile(sourceId, file);
        var start = Math.Max(1, line - radius);
        var end = line + radius;
        var lines = new List<object>();
        using var reader = File.OpenText(full);
        var current = 0;
        while (current < end && await reader.ReadLineAsync(ct) is { } content)
        {
            current++;
            if (current >= start) lines.Add(new { number = current, content, matched = current == line });
        }
        if (current < line) throw new ArgumentException("日志文件已经变化，原匹配行不存在，请重新搜索");
        return new { file, targetLine = line, startLine = start, endLine = current, lines, modified = File.GetLastWriteTimeUtc(full) };
    }
}

public sealed class LinuxManager(CommandRunner runner, IOptions<OSManagerOptions> options, ResourceRegistry registry)
{
    private const string ServiceHelper = "/usr/local/sbin/osmanager-service-helper";
    public bool IsAllowed(string service) => registry.Services.Contains(service, StringComparer.Ordinal);
    public async Task<object> ServiceStatusAsync(string service, CancellationToken ct)
    {
        Ensure(service); var r = await runner.RunAsync("/usr/bin/systemctl", ["show", service, "--property=Id,LoadState,ActiveState,SubState,MainPID,ActiveEnterTimestamp", "--no-pager"], ct: ct);
        var values = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        return new { service, loadState = values.GetValueOrDefault("LoadState", r.ExitCode == 0 ? "loaded" : "not-found"), activeState = values.GetValueOrDefault("ActiveState", "unknown"), subState = values.GetValueOrDefault("SubState", "unknown"), mainPid = values.GetValueOrDefault("MainPID", "0"), since = values.GetValueOrDefault("ActiveEnterTimestamp", ""), error = r.Error };
    }
    public async Task ActionAsync(string service, string action, CancellationToken ct)
    {
        Ensure(service); if (action is not ("start" or "stop" or "restart")) throw new ArgumentException("不支持的服务操作");
        if (OperatingSystem.IsLinux() && !options.Value.DemoMode)
        {
            var check = await runner.RunAsync("/usr/bin/systemctl", ["show", service, "--property=LoadState", "--value", "--no-pager"], ct: ct);
            if (check.ExitCode != 0 || check.Output.Trim() is "not-found" or "") throw new InvalidOperationException($"systemd 未加载 {service}。请确认 /etc/systemd/system/{service} 存在，并执行 sudo systemctl daemon-reload");
        }
        var r = await runner.RunAsync("/usr/bin/sudo", ["-n", "/usr/bin/systemctl", action, service], ct: ct);
        if (r.ExitCode != 0 && (r.Error + r.Output).Contains("sudo", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await RegisterExistingAsync(service, ct);
                r = await runner.RunAsync("/usr/bin/sudo", ["-n", "/usr/bin/systemctl", action, service], ct: ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法自动补齐 {service} 的受限 sudo 权限。请使用最新发布包重新执行 sudo bash install.sh。详细信息：{ex.Message}");
            }
        }
        if (r.ExitCode != 0)
        {
            if ((r.Error + r.Output).Contains("sudo", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"OSManager 服务账户仍无法操作 {service}。请使用最新发布包重新执行 sudo bash install.sh 后再试");
            throw new InvalidOperationException(r.Error.Length > 0 ? r.Error : "服务操作失败");
        }
    }
    public Task RegisterExistingAsync(string service, CancellationToken ct) => RunServiceHelperAsync(["register", service], ct);
    public Task PrepareManagedDirectoryAsync(string service, string runDirectory, CancellationToken ct) => RunServiceHelperAsync(["prepare", service, runDirectory], ct);
    public Task InstallManagedAsync(string service, string runDirectory, string mainProgram, CancellationToken ct) => RunServiceHelperAsync(["install", service, runDirectory, mainProgram], ct, TimeSpan.FromSeconds(60));
    public Task UnregisterAsync(string service, CancellationToken ct) => RunServiceHelperAsync(["unregister", service], ct);

    private async Task RunServiceHelperAsync(string[] args, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (OperatingSystem.IsLinux() && !options.Value.DemoMode && !File.Exists(ServiceHelper)) throw new InvalidOperationException("服务安装助手不存在，请使用最新发布包重新执行 install.sh");
        var command = new[] { "-n", ServiceHelper }.Concat(args);
        var result = await runner.RunAsync("/usr/bin/sudo", command, timeout ?? TimeSpan.FromSeconds(20), ct);
        if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }
    public async Task<List<object>> JournalAsync(JournalQuery q, CancellationToken ct)
    {
        Ensure(q.Service); if (q.End <= q.Start || q.End - q.Start > TimeSpan.FromDays(7)) throw new ArgumentException("Journal 查询范围必须在 7 天内");
        var args = new List<string> { "--unit", q.Service, "--since", q.Start.ToString("O"), "--until", q.End.ToString("O"), "--output", "json", "--no-pager", "--lines", Math.Clamp(q.Limit, 1, 10000).ToString() };
        if (!string.IsNullOrWhiteSpace(q.Priority)) { args.Add("--priority"); args.Add(q.Priority); }
        var r = await runner.RunAsync("/usr/bin/journalctl", args, TimeSpan.FromSeconds(30), ct); if (r.ExitCode != 0) throw new InvalidOperationException(r.Error);
        var result = new List<object>(); foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { using var doc = JsonDocument.Parse(line); var root = doc.RootElement; var message = root.TryGetProperty("MESSAGE", out var m) ? m.ToString() : ""; if (!string.IsNullOrWhiteSpace(q.Query) && !message.Contains(q.Query, StringComparison.OrdinalIgnoreCase)) continue; result.Add(new { timestamp = root.TryGetProperty("__REALTIME_TIMESTAMP", out var t) && long.TryParse(t.ToString(), out var micro) ? DateTimeOffset.FromUnixTimeMilliseconds(micro / 1000) : DateTimeOffset.MinValue, priority = root.TryGetProperty("PRIORITY", out var p) ? p.ToString() : "", pid = root.TryGetProperty("_PID", out var pid) ? pid.ToString() : "", message }); }
        return result;
    }
    public async Task FollowJournalAsync(string service, HttpResponse response, CancellationToken ct)
    {
        Ensure(service); response.ContentType = "text/event-stream"; response.Headers.CacheControl = "no-cache"; response.Headers.Connection = "keep-alive";
        if (!OperatingSystem.IsLinux() || options.Value.DemoMode)
        {
            await response.WriteAsync("data: {\"message\":\"演示模式：实时 Journal 仅在 Linux 上可用\"}\n\n", ct); await response.Body.FlushAsync(ct); return;
        }
        var psi = new ProcessStartInfo("/usr/bin/journalctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--follow", "--unit", service, "--output", "json", "--lines", "50", "--no-pager" }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 journalctl");
        try
        {
            while (!ct.IsCancellationRequested && await process.StandardOutput.ReadLineAsync(ct) is { } line)
            { await response.WriteAsync($"data: {line}\n\n", ct); await response.Body.FlushAsync(ct); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    private void Ensure(string service) { if (!IsAllowed(service)) throw new UnauthorizedAccessException("服务不在管理白名单中"); }
}

public sealed class MetricsCollector : BackgroundService
{
    private readonly object _gate = new(); private readonly Queue<MetricPoint> _points = new(); private (long idle, long total)? _cpu;
    public IReadOnlyList<MetricPoint> Snapshot() { lock (_gate) return _points.ToArray(); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested) { try { var p = Sample(); lock (_gate) { _points.Enqueue(p); while (_points.Count > 300) _points.Dequeue(); } } catch { } await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
    }
    private MetricPoint Sample()
    {
        if (!OperatingSystem.IsLinux()) { var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; var used = GC.GetTotalMemory(false); return new(DateTimeOffset.Now, Random.Shared.Next(8, 42), total > 0 ? used * 100d / total : 0, used, total, 0, 0, 0); }
        var cpuParts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray(); var idle = cpuParts[3] + (cpuParts.Length > 4 ? cpuParts[4] : 0); var totalTicks = cpuParts.Sum(); var cpuValue = _cpu is { } old ? 100d * (1 - (idle - old.idle) / (double)Math.Max(1, totalTicks - old.total)) : 0; _cpu = (idle, totalTicks);
        var mem = File.ReadLines("/proc/meminfo").Select(x => x.Split(':', 2)).ToDictionary(x => x[0], x => long.Parse(x[1].Trim().Split(' ')[0]) * 1024); var totalMem = mem["MemTotal"]; var usedMem = totalMem - mem.GetValueOrDefault("MemAvailable"); var loads = File.ReadAllText("/proc/loadavg").Split(' ').Take(3).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return new(DateTimeOffset.Now, Math.Clamp(cpuValue, 0, 100), usedMem * 100d / totalMem, usedMem, totalMem, loads[0], loads[1], loads[2]);
    }
}

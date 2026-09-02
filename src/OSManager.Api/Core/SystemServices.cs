using System.Diagnostics;
using System.Globalization;
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

    public async Task<object> DeployAsync(string rootId, string relativePath, IFormFile upload, AuthUser user, CancellationToken ct)
    {
        if (upload.Length == 0) throw new ArgumentException("上传文件为空");
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
                await BackupDirectoryAsync(resolved.FullPath, Path.Combine(backup, "content"), ct);
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
            return new { id, hash, relatedService = resolved.Root.RelatedService, canRestart = !string.IsNullOrWhiteSpace(resolved.Root.RelatedService) };
        }
        catch { if (Directory.Exists(backup)) Directory.Delete(backup, true); throw; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task RollbackAsync(long id, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT root_id,relative_path,backup_path,status FROM deployments WHERE id=$id", ("$id", id));
        if (rows.Count == 0) throw new KeyNotFoundException("发布记录不存在");
        if (rows[0]["status"]?.ToString() == "RolledBack") throw new InvalidOperationException("该发布已经回滚");
        var resolved = paths.Resolve(rows[0]["root_id"]!.ToString()!, rows[0]["relative_path"]!.ToString()!, false);
        var backup = rows[0]["backup_path"]!.ToString()!;
        if (!Directory.Exists(backup)) throw new FileNotFoundException("备份已经不存在");
        var type = File.Exists(Path.Combine(backup, ".type")) ? await File.ReadAllTextAsync(Path.Combine(backup, ".type"), ct) : "directory";
        if (type == "file-existing") File.Copy(Path.Combine(backup, "original"), resolved.FullPath, true);
        else if (type == "file-new") { if (File.Exists(resolved.FullPath)) File.Delete(resolved.FullPath); }
        else { if (Directory.Exists(resolved.FullPath)) Directory.Delete(resolved.FullPath, true); Directory.CreateDirectory(resolved.FullPath); await CopyDirectoryAsync(Path.Combine(backup, "content"), resolved.FullPath, ct); }
        await db.ExecuteAsync("UPDATE deployments SET status='RolledBack',rolled_back_at=$t WHERE id=$id", ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));
    }

    private static async Task BackupDirectoryAsync(string source, string destination, CancellationToken ct)
    { if (!Directory.Exists(source)) return; await CopyDirectoryAsync(source, destination, ct); }
    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { var dst = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(dst)!); await using var input = File.OpenRead(file); await using var output = File.Create(dst); await input.CopyToAsync(output, ct); }
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

public sealed class LogSearcher(PathPolicy paths, IOptions<OSManagerOptions> options)
{
    public async Task<List<object>> SearchAsync(LogSearchRequest request, CancellationToken ct)
    {
        if (request.End <= request.Start || request.End - request.Start > TimeSpan.FromDays(options.Value.LogSearchMaxDays)) throw new ArgumentException($"查询时间范围必须在 {options.Value.LogSearchMaxDays} 天内");
        var resolved = paths.Resolve(request.RootId, request.Path); if (!Directory.Exists(resolved.FullPath)) throw new ArgumentException("日志路径不是目录");
        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Regex? regex = request.Regex ? new Regex(request.Query, request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) : null;
        var limit = Math.Clamp(request.Limit, 1, 10000); var results = new List<object>();
        foreach (var file in Directory.EnumerateFiles(resolved.FullPath, "*.log", SearchOption.AllDirectories).Select(x => new FileInfo(x)).Where(x => x.LastWriteTimeUtc >= request.Start.UtcDateTime && x.LastWriteTimeUtc <= request.End.UtcDateTime))
        {
            var lineNo = 0; using var reader = file.OpenText();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lineNo++; bool match; try { match = regex?.IsMatch(line) ?? line.Contains(request.Query, comparison); } catch (RegexMatchTimeoutException) { throw new ArgumentException("正则表达式执行超时"); }
                if (match) results.Add(new { file = Path.GetRelativePath(resolved.FullPath, file.FullName), line = lineNo, content = line, modified = file.LastWriteTimeUtc });
                if (results.Count >= limit) return results;
            }
        }
        return results;
    }
}

public sealed class LinuxManager(CommandRunner runner, IOptions<OSManagerOptions> options)
{
    public bool IsAllowed(string service) => options.Value.ManagedServices.Contains(service, StringComparer.Ordinal);
    public async Task<object> ServiceStatusAsync(string service, CancellationToken ct)
    {
        Ensure(service); var r = await runner.RunAsync("/usr/bin/systemctl", ["show", service, "--property=Id,ActiveState,SubState,MainPID,ActiveEnterTimestamp", "--no-pager"], ct: ct);
        var values = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        return new { service, activeState = values.GetValueOrDefault("ActiveState", "unknown"), subState = values.GetValueOrDefault("SubState", "unknown"), mainPid = values.GetValueOrDefault("MainPID", "0"), since = values.GetValueOrDefault("ActiveEnterTimestamp", ""), error = r.Error };
    }
    public async Task ActionAsync(string service, string action, CancellationToken ct)
    {
        Ensure(service); if (action is not ("start" or "stop" or "restart")) throw new ArgumentException("不支持的服务操作");
        var r = await runner.RunAsync("/usr/bin/sudo", ["-n", "/usr/bin/systemctl", action, service], ct: ct); if (r.ExitCode != 0) throw new InvalidOperationException(r.Error.Length > 0 ? r.Error : "服务操作失败");
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
        while (!stoppingToken.IsCancellationRequested) { try { var p = Sample(); lock (_gate) { _points.Enqueue(p); while (_points.Count > 150) _points.Dequeue(); } } catch { } await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
    }
    private MetricPoint Sample()
    {
        if (!OperatingSystem.IsLinux()) { var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; var used = GC.GetTotalMemory(false); return new(DateTimeOffset.Now, Random.Shared.Next(8, 42), total > 0 ? used * 100d / total : 0, used, total, 0, 0, 0); }
        var cpuParts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray(); var idle = cpuParts[3] + (cpuParts.Length > 4 ? cpuParts[4] : 0); var totalTicks = cpuParts.Sum(); var cpuValue = _cpu is { } old ? 100d * (1 - (idle - old.idle) / (double)Math.Max(1, totalTicks - old.total)) : 0; _cpu = (idle, totalTicks);
        var mem = File.ReadLines("/proc/meminfo").Select(x => x.Split(':', 2)).ToDictionary(x => x[0], x => long.Parse(x[1].Trim().Split(' ')[0]) * 1024); var totalMem = mem["MemTotal"]; var usedMem = totalMem - mem.GetValueOrDefault("MemAvailable"); var loads = File.ReadAllText("/proc/loadavg").Split(' ').Take(3).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return new(DateTimeOffset.Now, Math.Clamp(cpuValue, 0, 100), usedMem * 100d / totalMem, usedMem, totalMem, loads[0], loads[1], loads[2]);
    }
}

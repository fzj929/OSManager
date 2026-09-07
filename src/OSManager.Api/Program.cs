using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OSManager.Api.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(x => { x.SingleLine = true; x.TimestampFormat = "HH:mm:ss "; });
builder.Services.Configure<OSManagerOptions>(builder.Configuration.GetSection("OSManager"));
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<ResourceRegistry>();
builder.Services.AddSingleton<PathPolicy>();
builder.Services.AddSingleton<FileOperations>();
builder.Services.AddSingleton<LogSearcher>();
builder.Services.AddSingleton<CommandRunner>();
builder.Services.AddSingleton<LinuxManager>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsCollector>());
builder.Services.AddAuthentication("Bearer").AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>("Bearer", null);
builder.Services.AddAuthorization();
builder.Services.AddCors(x => x.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(x => x.MultipartBodyLengthLimit = 501L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 501L * 1024 * 1024);

var app = builder.Build();
await app.Services.GetRequiredService<Database>().InitializeAsync();
await app.Services.GetRequiredService<ResourceRegistry>().InitializeAsync();
app.UseMiddleware<AuditMiddleware>();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");

api.MapPost("/auth/login", async (LoginRequest input, Database db, HttpContext ctx) =>
{
    var result = await db.LoginAsync(input.UserName.Trim(), input.Password);
    await db.AuditAsync(result?.User, "auth.login", input.UserName, result is not null, null, ctx.Connection.RemoteIpAddress?.ToString());
    return result is null ? Results.Json(new { message = "用户名或密码错误" }, statusCode: 401) : Results.Ok(result);
});

var secured = api.MapGroup("").RequireAuthorization();
secured.MapGet("/auth/me", (HttpContext ctx) => Results.Ok(ctx.CurrentUser()));
secured.MapPost("/auth/password", async (ChangePasswordRequest input, HttpContext ctx, Database db) =>
{
    if (input.NewPassword.Length < 10) throw new ArgumentException("新密码至少需要 10 个字符"); var user = ctx.CurrentUser();
    if (!await db.ChangePasswordAsync(user.Id, input.CurrentPassword, input.NewPassword)) return Results.Json(new { message = "当前密码错误" }, statusCode: 400);
    await db.AuditAsync(user, "auth.password.change", user.UserName, true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "密码已修改" });
});
secured.MapGet("/resources", (ResourceRegistry resources, IOptions<OSManagerOptions> o) => Results.Ok(new { directories = resources.Directories, services = resources.Services, logs = resources.LogSources, platform = OperatingSystem.IsLinux() ? "linux" : "development", demoMode = o.Value.DemoMode || !OperatingSystem.IsLinux() }));

secured.MapGet("/dashboard", async (MetricsCollector metrics, LinuxManager linux, ResourceRegistry resources, CancellationToken ct) =>
{
    var statuses = new List<object>(); foreach (var service in resources.Services) statuses.Add(await linux.ServiceStatusAsync(service, ct));
    return Results.Ok(new { metrics = metrics.Snapshot(), services = statuses, host = Environment.MachineName, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, uptimeSeconds = Environment.TickCount64 / 1000 });
});
secured.MapGet("/metrics", (MetricsCollector metrics) => Results.Ok(metrics.Snapshot()));

secured.MapGet("/files", (string rootId, string? path, FileOperations files) => Results.Ok(files.Browse(rootId, path)));
secured.MapGet("/files/download", async (string rootId, string path, PathPolicy policy, FileOperations files, HttpContext ctx, CancellationToken ct) =>
{
    var target = policy.Resolve(rootId, path);
    if (File.Exists(target.FullPath)) return Results.File(target.FullPath, "application/octet-stream", Path.GetFileName(target.FullPath), enableRangeProcessing: true);
    var archive = await files.CreateDirectoryArchiveAsync(rootId, path, ct);
    ctx.Response.OnCompleted(() => { if (File.Exists(archive)) File.Delete(archive); return Task.CompletedTask; });
    return Results.File(archive, "application/zip", Path.GetFileName(target.FullPath.TrimEnd(Path.DirectorySeparatorChar)) + ".zip", enableRangeProcessing: true);
});
secured.MapPost("/files/deploy", async (string rootId, string path, IFormFile file, HttpContext ctx, FileOperations files, Database db, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden();
    try { var result = await files.DeployAsync(rootId, path, file, user, ct); await db.AuditAsync(user, "file.deploy", $"{rootId}:{path}", true, new { file.FileName, file.Length }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(result); }
    catch { await db.AuditAsync(user, "file.deploy", $"{rootId}:{path}", false, new { file.FileName, file.Length }, ctx.Connection.RemoteIpAddress?.ToString()); throw; }
}).DisableAntiforgery();
secured.MapPost("/files/replace", async (string rootId, string path, IFormFile file, HttpContext ctx, FileOperations files, Database db, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden();
    try { var result = await files.ReplaceAsync(rootId, path, file, user, ct); await db.AuditAsync(user, "file.replace", $"{rootId}:{path}", true, new { file.FileName, file.Length, result.Id }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(result); }
    catch { await db.AuditAsync(user, "file.replace", $"{rootId}:{path}", false, new { file.FileName, file.Length }, ctx.Connection.RemoteIpAddress?.ToString()); throw; }
}).DisableAntiforgery();
secured.MapPost("/files/delete", async (FileDeleteRequest input, HttpContext ctx, FileOperations files, Database db, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden();
    try { var result = await files.DeleteAsync(input.RootId, input.Path, input.Backup, user, ct); await db.AuditAsync(user, "file.delete", $"{input.RootId}:{input.Path}", true, new { input.Backup, result.DeploymentId }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(result); }
    catch { await db.AuditAsync(user, "file.delete", $"{input.RootId}:{input.Path}", false, new { input.Backup }, ctx.Connection.RemoteIpAddress?.ToString()); throw; }
});
secured.MapGet("/deployments", async (Database db) => Results.Ok(await db.QueryAsync("SELECT id,user_name,root_id,relative_path,sha256,status,operation,related_service,created_at,rolled_back_at FROM deployments ORDER BY id DESC LIMIT 100")));
secured.MapPost("/deployments/{id:long}/rollback", async (long id, HttpContext ctx, FileOperations files, Database db, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden(); await files.RollbackAsync(id, ct); await db.AuditAsync(user, "file.rollback", id.ToString(), true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "回滚完成" });
});

secured.MapPost("/logs/search", async (LogSearchRequest input, LogSearcher logs, Database db, HttpContext ctx, CancellationToken ct) =>
{
    var result = await logs.SearchAsync(input, ct); await db.AuditAsync(ctx.CurrentUser(), "log.search", $"{input.RootId}:{input.Path}", true, new { input.Start, input.End, input.Query, count = result.Count }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(result);
});
secured.MapGet("/logs/context", async (string rootId, string file, int line, int? radius, LogSearcher logs, Database db, HttpContext ctx, CancellationToken ct) =>
{
    var result = await logs.ReadContextAsync(rootId, file, line, radius ?? 100, ct); await db.AuditAsync(ctx.CurrentUser(), "log.context", $"{rootId}:{file}:{line}", true, new { radius = radius ?? 100 }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(result);
});
secured.MapGet("/logs/download", async (string rootId, string file, LogSearcher logs, Database db, HttpContext ctx) =>
{
    var full = logs.ResolveFile(rootId, file); await db.AuditAsync(ctx.CurrentUser(), "log.download", $"{rootId}:{file}", true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.File(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
});

secured.MapGet("/services", async (LinuxManager linux, ResourceRegistry resources, CancellationToken ct) =>
{
    var result = new List<object>(); foreach (var service in resources.Services) result.Add(await linux.ServiceStatusAsync(service, ct)); return Results.Ok(result);
});
secured.MapPost("/services/{service}/action", async (string service, ServiceActionRequest input, LinuxManager linux, Database db, HttpContext ctx, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden(); await linux.ActionAsync(service, input.Action, ct); await db.AuditAsync(user, $"service.{input.Action}", service, true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "操作成功" });
});
secured.MapPost("/services/publish", async (string serviceName, string runDirectory, string mainProgram, string? backupExcludes, IFormFile file, HttpContext ctx, ResourceRegistry resources, FileOperations files, LinuxManager linux, PathPolicy paths, Database db, IOptions<OSManagerOptions> options, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden();
    serviceName = serviceName?.Trim() ?? "";
    runDirectory = runDirectory?.Trim() ?? "";
    mainProgram = mainProgram?.Trim().Replace('\\', '/') ?? "";
    if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("服务发布仅支持 ZIP 安装包");
    if (!System.Text.RegularExpressions.Regex.IsMatch(serviceName, "^[A-Za-z0-9_.@:-]+\\.service$")) throw new ArgumentException("服务名无效，必须以 .service 结尾");
    if (!runDirectory.StartsWith('/') || runDirectory.Contains('\0')) throw new ArgumentException("运行目录必须是 Linux 绝对路径");
    if (!System.Text.RegularExpressions.Regex.IsMatch(mainProgram, "^[A-Za-z0-9][A-Za-z0-9._/-]*$") || mainProgram.Split('/').Any(x => x is "." or "..")) throw new ArgumentException("主程序必须是运行目录内的相对路径");
    var baseName = serviceName[..^8]; var resourceId = System.Text.RegularExpressions.Regex.Replace(baseName, "[^A-Za-z0-9_-]", "-").Trim('-');
    if (string.IsNullOrWhiteSpace(resourceId)) resourceId = $"service-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serviceName)))[..10].ToLowerInvariant()}";
    if (resourceId.Length > 64) resourceId = resourceId[..53] + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serviceName)))[..10].ToLowerInvariant();
    var existing = resources.Directories.FirstOrDefault(x => x.Id.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
    if (existing is not null && (!existing.Path.Equals(runDirectory, StringComparison.Ordinal) || !string.Equals(existing.RelatedService, serviceName, StringComparison.Ordinal))) throw new InvalidOperationException($"资源标识 {resourceId} 已被其他运行目录使用");
    var directory = new ManagedDirectory(resourceId, baseName, runDirectory, true, serviceName, backupExcludes ?? "");
    DeploymentResult? deployment = null;
    try
    {
        await linux.PrepareManagedDirectoryAsync(serviceName, runDirectory, ct);
        await resources.SaveDirectoryAsync(directory);
        deployment = await files.DeployAsync(resourceId, "", file, user, ct, options.Value.MaxServicePackageBytes);
        var targetDirectory = paths.Resolve(resourceId, "");
        var main = paths.Resolve(resourceId, mainProgram); if (!File.Exists(main.FullPath)) throw new FileNotFoundException($"ZIP 中未找到主程序：{mainProgram}");
        await linux.InstallManagedAsync(serviceName, targetDirectory.FullPath, mainProgram, ct);
        await resources.SaveServiceAsync(serviceName);
        await db.AuditAsync(user, "service.publish", serviceName, true, new { runDirectory, mainProgram, backupExcludes, deployment.Id, file.FileName, file.Length }, ctx.Connection.RemoteIpAddress?.ToString());
        return Results.Ok(new { message = "服务发布并安装成功", service = serviceName, rootId = resourceId, deploymentId = deployment.Id });
    }
    catch (Exception ex)
    {
        if (deployment is not null) { try { await files.RollbackAsync(deployment.Id, ct); } catch { } }
        if (existing is null) await resources.DeleteDirectoryAsync(resourceId); else await resources.SaveDirectoryAsync(existing);
        await db.AuditAsync(user, "service.publish", serviceName, false, new { runDirectory, mainProgram, error = ex.Message }, ctx.Connection.RemoteIpAddress?.ToString());
        throw;
    }
}).DisableAntiforgery();
secured.MapPost("/journal/query", async (JournalQuery input, LinuxManager linux, Database db, HttpContext ctx, CancellationToken ct) =>
{
    var result = await linux.JournalAsync(input, ct); await db.AuditAsync(ctx.CurrentUser(), "journal.query", input.Service, true, new { input.Start, input.End, count = result.Count }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(result);
});
secured.MapGet("/journal/follow", async (string service, LinuxManager linux, Database db, HttpContext ctx, CancellationToken ct) =>
{
    await db.AuditAsync(ctx.CurrentUser(), "journal.follow", service, true, null, ctx.Connection.RemoteIpAddress?.ToString());
    await linux.FollowJournalAsync(service, ctx.Response, ct);
});

secured.MapGet("/processes", () =>
{
    return Results.Ok(Process.GetProcesses().Select(p => { try { return new { pid = p.Id, name = p.ProcessName, memory = p.WorkingSet64, cpu = p.TotalProcessorTime.TotalSeconds, started = (DateTime?)p.StartTime }; } catch { return new { pid = p.Id, name = p.ProcessName, memory = 0L, cpu = 0d, started = (DateTime?)null }; } }).OrderByDescending(x => x.memory).Take(500));
});
secured.MapPost("/processes/{pid:int}/terminate", async (int pid, bool force, HttpContext ctx, Database db) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden(); if (pid is 1 || pid == Environment.ProcessId) throw new InvalidOperationException("禁止停止系统或 OSManager 进程");
    using var process = Process.GetProcessById(pid); process.Kill(force); await db.AuditAsync(user, force ? "process.kill" : "process.terminate", $"{pid}:{process.ProcessName}", true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "终止信号已发送" });
});

secured.MapGet("/configs", (ResourceRegistry resources) => Results.Ok(resources.Directories.Where(x => x.CanUpload).Select(x => new { x.Id, x.Name, x.Path, x.RelatedService })));
secured.MapGet("/configs/files", (string rootId, PathPolicy policy) =>
{
    var root = policy.Resolve(rootId, "");
    if (!root.Root.CanUpload) throw new UnauthorizedAccessException("该目录不允许编辑配置");
    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
    var extensions = new HashSet<string>([".json", ".ini", ".config", ".yaml", ".yml", ".conf"], StringComparer.OrdinalIgnoreCase);
    var files = Directory.EnumerateFiles(root.FullPath, "*", options)
        .Where(x => extensions.Contains(Path.GetExtension(x)))
        .Select(x => Path.GetRelativePath(root.FullPath, x).Replace('\\', '/'))
        .Order(StringComparer.OrdinalIgnoreCase)
        .Take(5000)
        .ToArray();
    return Results.Ok(files);
});
secured.MapGet("/configs/content", async (string rootId, string path, PathPolicy policy, CancellationToken ct) =>
{
    var target = policy.Resolve(rootId, path); if (!File.Exists(target.FullPath)) throw new ArgumentException("目标不是文件");
    var content = await File.ReadAllTextAsync(target.FullPath, ct); return Results.Ok(new { content, version = Hash(content), modified = File.GetLastWriteTimeUtc(target.FullPath) });
});
secured.MapPost("/configs/save", async (ConfigSaveRequest input, HttpContext ctx, PathPolicy policy, Database db, IConfiguration config, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanEdit(user.Role)) return HttpUserExtensions.Forbidden();
    if (string.IsNullOrWhiteSpace(input.Path)) throw new ArgumentException("配置文件路径不能为空");
    if (input.Content is null) throw new ArgumentException("配置文件内容不能为空");
    var target = policy.Resolve(input.RootId, input.Path); if (!target.Root.CanUpload || !File.Exists(target.FullPath)) throw new UnauthorizedAccessException("该配置文件不允许编辑");
    var current = await File.ReadAllTextAsync(target.FullPath, ct); if (Hash(current) != input.Version) throw new InvalidOperationException("配置文件已被其他程序修改，请重新读取后再保存");
    var backupDir = Path.GetFullPath(Path.Combine(config["OSManager:DataDirectory"] ?? "data", "config-backups")); Directory.CreateDirectory(backupDir);
    var backupName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{Path.GetFileName(target.FullPath)}"; File.Copy(target.FullPath, Path.Combine(backupDir, backupName), false);
    var temp = target.FullPath + $".osmanager.{Guid.NewGuid():N}.tmp";
    try { await File.WriteAllTextAsync(temp, input.Content, ct); File.Move(temp, target.FullPath, true); }
    finally { if (File.Exists(temp)) File.Delete(temp); }
    var version = Hash(input.Content); await db.AuditAsync(user, "config.save", $"{input.RootId}:{input.Path}", true, new { backupName }, ctx.Connection.RemoteIpAddress?.ToString());
    return Results.Ok(new { message = "配置已保存", version, relatedService = target.Root.RelatedService, canRestart = !string.IsNullOrWhiteSpace(target.Root.RelatedService) });
});

secured.MapGet("/audit", async (HttpContext ctx, Database db) => ctx.CurrentUser().Role != Roles.Admin ? HttpUserExtensions.Forbidden() : Results.Ok(await db.QueryAsync("SELECT * FROM audit_logs ORDER BY id DESC LIMIT 500")));
secured.MapGet("/users", async (HttpContext ctx, Database db) => ctx.CurrentUser().Role != Roles.Admin ? HttpUserExtensions.Forbidden() : Results.Ok(await db.QueryAsync("SELECT id,username,display_name,role,is_enabled,created_at FROM users ORDER BY id")));
secured.MapPost("/users", async (UserCreate input, HttpContext ctx, Database db) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden(); if (input.Password.Length < 10) throw new ArgumentException("密码至少需要 10 个字符"); if (input.Role is not (Roles.Viewer or Roles.Editor or Roles.Approver or Roles.Operator or Roles.Admin)) throw new ArgumentException("角色无效");
    await db.ExecuteAsync("INSERT INTO users(username,display_name,role,password_hash,created_at) VALUES($u,$d,$r,$p,$t)", ("$u", input.UserName), ("$d", input.DisplayName), ("$r", input.Role), ("$p", Passwords.Hash(input.Password)), ("$t", DateTimeOffset.UtcNow.ToString("O"))); await db.AuditAsync(user, "user.create", input.UserName, true, new { input.Role }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "用户已创建" });
});
secured.MapPut("/users/{id:long}", async (long id, UserUpdateRequest input, HttpContext ctx, Database db) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden();
    if (string.IsNullOrWhiteSpace(input.DisplayName)) throw new ArgumentException("显示名称不能为空");
    if (input.Role is not (Roles.Viewer or Roles.Editor or Roles.Approver or Roles.Operator or Roles.Admin)) throw new ArgumentException("角色无效");
    if (id == user.Id && !input.IsEnabled) throw new InvalidOperationException("不能禁用当前登录账户");
    var changed = await db.ExecuteAsync("UPDATE users SET display_name=$name,role=$role,is_enabled=$enabled WHERE id=$id", ("$name", input.DisplayName.Trim()), ("$role", input.Role), ("$enabled", input.IsEnabled ? 1 : 0), ("$id", id));
    if (changed == 0) return Results.NotFound(); if (!input.IsEnabled) await db.ExecuteAsync("DELETE FROM sessions WHERE user_id=$id", ("$id", id)); await db.AuditAsync(user, "user.update", id.ToString(), true, new { input.DisplayName, input.Role, input.IsEnabled }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "用户已更新" });
});
secured.MapPost("/users/{id:long}/password", async (long id, AdminResetPasswordRequest input, HttpContext ctx, Database db) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden(); if (input.NewPassword.Length < 10) throw new ArgumentException("密码至少需要 10 个字符");
    var changed = await db.ExecuteAsync("UPDATE users SET password_hash=$hash WHERE id=$id", ("$hash", Passwords.Hash(input.NewPassword)), ("$id", id));
    if (changed == 0) return Results.NotFound(); await db.ExecuteAsync("DELETE FROM sessions WHERE user_id=$id", ("$id", id)); await db.AuditAsync(user, "user.password.reset", id.ToString(), true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "密码已重置" });
});

secured.MapPut("/settings/directories/{id}", async (string id, ManagedDirectory input, HttpContext ctx, ResourceRegistry resources, Database db) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden();
    if (!id.Equals(input.Id, StringComparison.Ordinal)) throw new ArgumentException("路径中的目录标识与提交内容不一致");
    await resources.SaveDirectoryAsync(input); await db.AuditAsync(user, "settings.directory.save", input.Id, true, new { input.Name, input.Path, input.CanUpload, input.RelatedService, input.BackupExcludes }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "管理目录已保存" });
});
secured.MapDelete("/settings/directories/{id}", async (string id, HttpContext ctx, ResourceRegistry resources, Database db) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden();
    await resources.DeleteDirectoryAsync(id); await db.AuditAsync(user, "settings.directory.delete", id, true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "管理目录已删除" });
});
secured.MapPut("/settings/services/{name}", async (string name, ManagedServiceRequest input, HttpContext ctx, ResourceRegistry resources, LinuxManager linux, Database db, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden();
    if (!name.Equals(input.Name, StringComparison.Ordinal)) throw new ArgumentException("路径中的服务名与提交内容不一致");
    await linux.RegisterExistingAsync(input.Name, ct); await resources.SaveServiceAsync(input.Name); await db.AuditAsync(user, "settings.service.save", input.Name, true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "已有服务已加入管理" });
});
secured.MapDelete("/settings/services/{name}", async (string name, HttpContext ctx, ResourceRegistry resources, LinuxManager linux, Database db, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden();
    await linux.UnregisterAsync(name, ct); await resources.DeleteServiceAsync(name); await db.AuditAsync(user, "settings.service.delete", name, true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "服务已移出管理，systemd unit 和运行状态未改变" });
});
secured.MapPut("/settings/logs/{id}", async (string id, ManagedLogSource input, HttpContext ctx, ResourceRegistry resources, Database db) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden(); if (!id.Equals(input.Id, StringComparison.Ordinal)) throw new ArgumentException("路径中的日志源标识与提交内容不一致");
    await resources.SaveLogSourceAsync(input); await db.AuditAsync(user, "settings.log.save", input.Id, true, new { input.Name, input.Path, input.FilePattern }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "日志源已保存" });
});
secured.MapDelete("/settings/logs/{id}", async (string id, HttpContext ctx, ResourceRegistry resources, Database db) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden(); await resources.DeleteLogSourceAsync(id); await db.AuditAsync(user, "settings.log.delete", id, true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "日志源已删除" });
});

app.MapFallbackToFile("index.html");
app.Run();

static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
public sealed record UserCreate(string UserName, string DisplayName, string Password, string Role);

public partial class Program { }

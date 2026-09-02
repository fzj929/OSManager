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
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 101 * 1024 * 1024);

var app = builder.Build();
await app.Services.GetRequiredService<Database>().InitializeAsync();
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
secured.MapGet("/resources", (IOptions<OSManagerOptions> o) => Results.Ok(new { directories = o.Value.ManagedDirectories, services = o.Value.ManagedServices, platform = OperatingSystem.IsLinux() ? "linux" : "development", demoMode = o.Value.DemoMode || !OperatingSystem.IsLinux() }));

secured.MapGet("/dashboard", async (MetricsCollector metrics, LinuxManager linux, IOptions<OSManagerOptions> o, CancellationToken ct) =>
{
    var statuses = new List<object>(); foreach (var service in o.Value.ManagedServices) statuses.Add(await linux.ServiceStatusAsync(service, ct));
    return Results.Ok(new { metrics = metrics.Snapshot(), services = statuses, host = Environment.MachineName, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, uptimeSeconds = Environment.TickCount64 / 1000 });
});
secured.MapGet("/metrics", (MetricsCollector metrics) => Results.Ok(metrics.Snapshot()));

secured.MapGet("/files", (string rootId, string? path, FileOperations files) => Results.Ok(files.Browse(rootId, path)));
secured.MapGet("/files/download", (string rootId, string path, PathPolicy policy) =>
{
    var file = policy.Resolve(rootId, path); if (!File.Exists(file.FullPath)) throw new ArgumentException("只能下载文件");
    return Results.File(file.FullPath, "application/octet-stream", Path.GetFileName(file.FullPath), enableRangeProcessing: true);
});
secured.MapPost("/files/deploy", async (string rootId, string path, IFormFile file, HttpContext ctx, FileOperations files, Database db, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden();
    try { var result = await files.DeployAsync(rootId, path, file, user, ct); await db.AuditAsync(user, "file.deploy", $"{rootId}:{path}", true, new { file.FileName, file.Length }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(result); }
    catch { await db.AuditAsync(user, "file.deploy", $"{rootId}:{path}", false, new { file.FileName, file.Length }, ctx.Connection.RemoteIpAddress?.ToString()); throw; }
}).DisableAntiforgery();
secured.MapGet("/deployments", async (Database db) => Results.Ok(await db.QueryAsync("SELECT id,user_name,root_id,relative_path,sha256,status,related_service,created_at,rolled_back_at FROM deployments ORDER BY id DESC LIMIT 100")));
secured.MapPost("/deployments/{id:long}/rollback", async (long id, HttpContext ctx, FileOperations files, Database db, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden(); await files.RollbackAsync(id, ct); await db.AuditAsync(user, "file.rollback", id.ToString(), true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "回滚完成" });
});

secured.MapPost("/logs/search", async (LogSearchRequest input, LogSearcher logs, Database db, HttpContext ctx, CancellationToken ct) =>
{
    var result = await logs.SearchAsync(input, ct); await db.AuditAsync(ctx.CurrentUser(), "log.search", $"{input.RootId}:{input.Path}", true, new { input.Start, input.End, input.Query, count = result.Count }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(result);
});

secured.MapGet("/services", async (LinuxManager linux, IOptions<OSManagerOptions> o, CancellationToken ct) =>
{
    var result = new List<object>(); foreach (var service in o.Value.ManagedServices) result.Add(await linux.ServiceStatusAsync(service, ct)); return Results.Ok(result);
});
secured.MapPost("/services/{service}/action", async (string service, ServiceActionRequest input, LinuxManager linux, Database db, HttpContext ctx, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role)) return HttpUserExtensions.Forbidden(); await linux.ActionAsync(service, input.Action, ct); await db.AuditAsync(user, $"service.{input.Action}", service, true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "操作成功" });
});
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

secured.MapGet("/configs", (IOptions<OSManagerOptions> o) => Results.Ok(o.Value.ManagedDirectories.Where(x => x.CanUpload).Select(x => new { x.Id, x.Name, x.Path, x.RelatedService })));
secured.MapGet("/configs/content", async (string rootId, string path, PathPolicy policy, CancellationToken ct) =>
{
    var target = policy.Resolve(rootId, path); if (!File.Exists(target.FullPath)) throw new ArgumentException("目标不是文件");
    var content = await File.ReadAllTextAsync(target.FullPath, ct); return Results.Ok(new { content, version = Hash(content), modified = File.GetLastWriteTimeUtc(target.FullPath) });
});
secured.MapPost("/configs/changes", async (ConfigSubmitRequest input, HttpContext ctx, PathPolicy policy, Database db) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanEdit(user.Role)) return HttpUserExtensions.Forbidden(); policy.Resolve(input.RootId, input.Path);
    await db.ExecuteAsync("INSERT INTO config_changes(root_id,relative_path,content,base_version,reason,submitter,status,created_at) VALUES($r,$p,$c,$v,$x,$u,'PendingApproval',$t)", ("$r", input.RootId), ("$p", input.Path), ("$c", input.Content), ("$v", input.Version), ("$x", input.Reason), ("$u", user.UserName), ("$t", DateTimeOffset.UtcNow.ToString("O")));
    await db.AuditAsync(user, "config.submit", $"{input.RootId}:{input.Path}", true, new { input.Reason }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "已提交审批" });
});
secured.MapGet("/configs/changes", async (Database db) => Results.Ok(await db.QueryAsync("SELECT id,root_id,relative_path,reason,submitter,status,reviewer,review_comment,created_at,reviewed_at,applied_at FROM config_changes ORDER BY id DESC LIMIT 200")));
secured.MapGet("/configs/changes/{id:long}", async (long id, Database db) =>
{
    var rows = await db.QueryAsync("SELECT * FROM config_changes WHERE id=$id", ("$id", id)); return rows.Count == 0 ? Results.NotFound() : Results.Ok(rows[0]);
});
secured.MapPost("/configs/changes/{id:long}/review", async (long id, ApprovalRequest input, HttpContext ctx, Database db) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanApprove(user.Role)) return HttpUserExtensions.Forbidden(); var rows = await db.QueryAsync("SELECT submitter,status FROM config_changes WHERE id=$id", ("$id", id)); if (rows.Count == 0) return Results.NotFound();
    if (rows[0]["submitter"]?.ToString() == user.UserName) throw new InvalidOperationException("提交人不能审批自己的修改"); if (rows[0]["status"]?.ToString() != "PendingApproval") throw new InvalidOperationException("审批单当前状态不允许审核");
    var status = input.Decision.Equals("approve", StringComparison.OrdinalIgnoreCase) ? "Approved" : "Rejected"; await db.ExecuteAsync("UPDATE config_changes SET status=$s,reviewer=$r,review_comment=$c,reviewed_at=$t WHERE id=$id", ("$s", status), ("$r", user.UserName), ("$c", input.Comment), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$id", id)); await db.AuditAsync(user, "config.review", id.ToString(), true, new { status, input.Comment }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "审批完成" });
});
secured.MapPost("/configs/changes/{id:long}/apply", async (long id, HttpContext ctx, PathPolicy policy, Database db, IConfiguration config, CancellationToken ct) =>
{
    var user = ctx.CurrentUser(); if (!Roles.CanOperate(user.Role) && user.Role != Roles.Approver) return HttpUserExtensions.Forbidden(); var rows = await db.QueryAsync("SELECT * FROM config_changes WHERE id=$id", ("$id", id)); if (rows.Count == 0) return Results.NotFound(); var row = rows[0]; if (row["status"]?.ToString() != "Approved") throw new InvalidOperationException("配置尚未批准");
    var target = policy.Resolve(row["root_id"]!.ToString()!, row["relative_path"]!.ToString()!); var current = await File.ReadAllTextAsync(target.FullPath, ct); if (Hash(current) != row["base_version"]!.ToString()) throw new InvalidOperationException("配置文件已被其他程序修改，请重新提交");
    var backupDir = Path.GetFullPath(Path.Combine(config["OSManager:DataDirectory"] ?? "data", "config-backups")); Directory.CreateDirectory(backupDir); File.Copy(target.FullPath, Path.Combine(backupDir, $"{id}-{Path.GetFileName(target.FullPath)}"), true); var temp = target.FullPath + ".osmanager.tmp"; await File.WriteAllTextAsync(temp, row["content"]!.ToString(), ct); File.Move(temp, target.FullPath, true);
    await db.ExecuteAsync("UPDATE config_changes SET status='Applied',applied_at=$t WHERE id=$id", ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$id", id)); await db.AuditAsync(user, "config.apply", id.ToString(), true, null, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "配置已应用" });
});

secured.MapGet("/audit", async (HttpContext ctx, Database db) => ctx.CurrentUser().Role != Roles.Admin ? HttpUserExtensions.Forbidden() : Results.Ok(await db.QueryAsync("SELECT * FROM audit_logs ORDER BY id DESC LIMIT 500")));
secured.MapGet("/users", async (HttpContext ctx, Database db) => ctx.CurrentUser().Role != Roles.Admin ? HttpUserExtensions.Forbidden() : Results.Ok(await db.QueryAsync("SELECT id,username,display_name,role,created_at FROM users ORDER BY id")));
secured.MapPost("/users", async (UserCreate input, HttpContext ctx, Database db) =>
{
    var user = ctx.CurrentUser(); if (user.Role != Roles.Admin) return HttpUserExtensions.Forbidden(); if (input.Password.Length < 10) throw new ArgumentException("密码至少需要 10 个字符"); if (input.Role is not (Roles.Viewer or Roles.Editor or Roles.Approver or Roles.Operator or Roles.Admin)) throw new ArgumentException("角色无效");
    await db.ExecuteAsync("INSERT INTO users(username,display_name,role,password_hash,created_at) VALUES($u,$d,$r,$p,$t)", ("$u", input.UserName), ("$d", input.DisplayName), ("$r", input.Role), ("$p", Passwords.Hash(input.Password)), ("$t", DateTimeOffset.UtcNow.ToString("O"))); await db.AuditAsync(user, "user.create", input.UserName, true, new { input.Role }, ctx.Connection.RemoteIpAddress?.ToString()); return Results.Ok(new { message = "用户已创建" });
});

app.MapFallbackToFile("index.html");
app.Run();

static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
public sealed record UserCreate(string UserName, string DisplayName, string Password, string Role);

public partial class Program { }

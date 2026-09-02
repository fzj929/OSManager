namespace OSManager.Api.Core;

public sealed class OSManagerOptions
{
    public string DataDirectory { get; set; } = "data";
    public long MaxUploadBytes { get; set; } = 100 * 1024 * 1024;
    public long MaxExtractedBytes { get; set; } = 500 * 1024 * 1024;
    public int MaxArchiveEntries { get; set; } = 5000;
    public int LogSearchMaxDays { get; set; } = 7;
    public bool DemoMode { get; set; }
    public List<ManagedDirectory> ManagedDirectories { get; set; } = [];
    public List<string> ManagedServices { get; set; } = [];
}

public sealed record ManagedDirectory(string Id, string Name, string Path, bool CanUpload = false, string? RelatedService = null);
public sealed record AuthUser(long Id, string UserName, string DisplayName, string Role);
public sealed record LoginRequest(string UserName, string Password);
public sealed record LoginResponse(string Token, AuthUser User);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ServiceActionRequest(string Action);
public sealed record ConfigSubmitRequest(string RootId, string Path, string Content, string? Reason, string Version);
public sealed record ApprovalRequest(string Decision, string? Comment);
public sealed record LogSearchRequest(string RootId, string Path, DateTimeOffset Start, DateTimeOffset End, string Query, bool Regex = false, bool CaseSensitive = false, int Limit = 1000);
public sealed record JournalQuery(string Service, DateTimeOffset Start, DateTimeOffset End, string? Query, string? Priority, int Limit = 1000);
public sealed record MetricPoint(DateTimeOffset Time, double Cpu, double Memory, long UsedMemory, long TotalMemory, double Load1, double Load5, double Load15);

public static class Roles
{
    public const string Viewer = "Viewer";
    public const string Editor = "Editor";
    public const string Approver = "Approver";
    public const string Operator = "Operator";
    public const string Admin = "Admin";
    public static bool CanOperate(string role) => role is Operator or Admin;
    public static bool CanEdit(string role) => role is Editor or Admin;
    public static bool CanApprove(string role) => role is Approver or Admin;
}

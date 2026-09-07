namespace OSManager.Api.Core;

public sealed class PathPolicy(ResourceRegistry registry, IWebHostEnvironment environment)
{
    public IReadOnlyList<ManagedDirectory> Roots => registry.Directories;
    public string ResolveConfiguredPath(string configuredPath, bool mustExist = true)
    {
        var expanded = configuredPath.StartsWith("${CONTENT_ROOT}/", StringComparison.Ordinal)
            ? Path.Combine(environment.ContentRootPath, configuredPath[16..])
            : configuredPath;
        var full = Path.GetFullPath(expanded);
        if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("配置的路径不存在");
        if ((File.Exists(full) || Directory.Exists(full)) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("禁止访问符号链接");
        return full;
    }
    public (ManagedDirectory Root, string FullPath) Resolve(string rootId, string? relativePath, bool mustExist = true)
    {
        var root = Roots.FirstOrDefault(x => x.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("管理目录不存在");
        var rootPath = ResolveConfiguredPath(root.Path, false);
        Directory.CreateDirectory(rootPath);
        var full = Path.GetFullPath(Path.Combine(rootPath, relativePath ?? ""));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.Equals(rootPath, comparison) && !full.StartsWith(prefix, comparison)) throw new UnauthorizedAccessException("路径超出管理目录");
        if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("文件或目录不存在");
        var cursor = full;
        while (cursor.StartsWith(prefix, comparison) && !cursor.Equals(rootPath, comparison))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("禁止访问符号链接");
            cursor = Path.GetDirectoryName(cursor)!;
        }
        return (root, full);
    }
}

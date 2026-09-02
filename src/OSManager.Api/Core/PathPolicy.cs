using Microsoft.Extensions.Options;

namespace OSManager.Api.Core;

public sealed class PathPolicy(IOptions<OSManagerOptions> options)
{
    public IReadOnlyList<ManagedDirectory> Roots => options.Value.ManagedDirectories;
    public (ManagedDirectory Root, string FullPath) Resolve(string rootId, string? relativePath, bool mustExist = true)
    {
        var root = Roots.FirstOrDefault(x => x.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("管理目录不存在");
        var rootPath = Path.GetFullPath(root.Path);
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

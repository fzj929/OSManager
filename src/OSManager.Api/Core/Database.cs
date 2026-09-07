using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OSManager.Api.Core;

public sealed class Database
{
    private readonly string _connectionString;
    public Database(IConfiguration config)
    {
        var dir = Path.GetFullPath(config["OSManager:DataDirectory"] ?? "data");
        Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "osmanager.db") }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var db = Open();
        await db.OpenAsync();
        var sql = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS users(id INTEGER PRIMARY KEY, username TEXT UNIQUE NOT NULL, display_name TEXT NOT NULL, role TEXT NOT NULL, password_hash TEXT NOT NULL, is_enabled INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sessions(token TEXT PRIMARY KEY, user_id INTEGER NOT NULL, expires_at TEXT NOT NULL, FOREIGN KEY(user_id) REFERENCES users(id));
        CREATE TABLE IF NOT EXISTS audit_logs(id INTEGER PRIMARY KEY, user_name TEXT NOT NULL, action TEXT NOT NULL, target TEXT NOT NULL, success INTEGER NOT NULL, details TEXT, ip TEXT, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS deployments(id INTEGER PRIMARY KEY, user_name TEXT NOT NULL, root_id TEXT NOT NULL, relative_path TEXT NOT NULL, backup_path TEXT NOT NULL, sha256 TEXT NOT NULL, status TEXT NOT NULL, operation TEXT NOT NULL DEFAULT 'Deploy', related_service TEXT, created_at TEXT NOT NULL, rolled_back_at TEXT);
        CREATE TABLE IF NOT EXISTS config_changes(id INTEGER PRIMARY KEY, root_id TEXT NOT NULL, relative_path TEXT NOT NULL, content TEXT NOT NULL, base_version TEXT NOT NULL, reason TEXT, submitter TEXT NOT NULL, status TEXT NOT NULL, reviewer TEXT, review_comment TEXT, created_at TEXT NOT NULL, reviewed_at TEXT, applied_at TEXT);
        CREATE TABLE IF NOT EXISTS managed_directories(id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, can_upload INTEGER NOT NULL DEFAULT 0, related_service TEXT, backup_excludes TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS managed_services(name TEXT PRIMARY KEY, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS managed_log_sources(id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, file_pattern TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS resource_registry_meta(id INTEGER PRIMARY KEY CHECK(id=1), initialized INTEGER NOT NULL);
        """;
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "ALTER TABLE users ADD COLUMN is_enabled INTEGER NOT NULL DEFAULT 1";
        try { await cmd.ExecuteNonQueryAsync(); } catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }

        cmd.CommandText = "ALTER TABLE managed_directories ADD COLUMN backup_excludes TEXT NOT NULL DEFAULT ''";
        try { await cmd.ExecuteNonQueryAsync(); } catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }

        cmd.CommandText = "ALTER TABLE deployments ADD COLUMN operation TEXT NOT NULL DEFAULT 'Deploy'";
        try { await cmd.ExecuteNonQueryAsync(); } catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }

        cmd.CommandText = "SELECT COUNT(*) FROM users";
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        if (count == 0)
        {
            cmd.CommandText = "INSERT INTO users(username,display_name,role,password_hash,created_at) VALUES($u,$d,$r,$p,$t)";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$u", "admin");
            cmd.Parameters.AddWithValue("$d", "系统管理员");
            cmd.Parameters.AddWithValue("$r", Roles.Admin);
            cmd.Parameters.AddWithValue("$p", Passwords.Hash("ChangeMe!123"));
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<LoginResponse?> LoginAsync(string username, string password)
    {
        await using var db = Open(); await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,username,display_name,role,password_hash FROM users WHERE username=$u AND is_enabled=1";
        cmd.Parameters.AddWithValue("$u", username);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || !Passwords.Verify(password, reader.GetString(4))) return null;
        var user = new AuthUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await reader.DisposeAsync();
        cmd.CommandText = "INSERT INTO sessions(token,user_id,expires_at) VALUES($token,$id,$expires)";
        cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$token", token); cmd.Parameters.AddWithValue("$id", user.Id); cmd.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddHours(12).ToString("O"));
        await cmd.ExecuteNonQueryAsync();
        return new LoginResponse(token, user);
    }

    public async Task<AuthUser?> FindSessionAsync(string token)
    {
        await using var db = Open(); await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT u.id,u.username,u.display_name,u.role FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token=$t AND s.expires_at>$now AND u.is_enabled=1";
        cmd.Parameters.AddWithValue("$t", token); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new AuthUser(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    public async Task<bool> ChangePasswordAsync(long userId, string currentPassword, string newPassword)
    {
        await using var db = Open(); await db.OpenAsync(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT password_hash FROM users WHERE id=$id"; cmd.Parameters.AddWithValue("$id", userId);
        var hash = (string?)await cmd.ExecuteScalarAsync(); if (hash is null || !Passwords.Verify(currentPassword, hash)) return false;
        cmd.CommandText = "UPDATE users SET password_hash=$p WHERE id=$id"; cmd.Parameters.AddWithValue("$p", Passwords.Hash(newPassword)); await cmd.ExecuteNonQueryAsync(); return true;
    }

    public async Task AuditAsync(AuthUser? user, string action, string target, bool success, object? details, string? ip)
    {
        await using var db = Open(); await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_logs(user_name,action,target,success,details,ip,created_at) VALUES($u,$a,$t,$s,$d,$i,$c)";
        cmd.Parameters.AddWithValue("$u", user?.UserName ?? "anonymous"); cmd.Parameters.AddWithValue("$a", action); cmd.Parameters.AddWithValue("$t", target); cmd.Parameters.AddWithValue("$s", success ? 1 : 0); cmd.Parameters.AddWithValue("$d", details is null ? DBNull.Value : JsonSerializer.Serialize(details)); cmd.Parameters.AddWithValue("$i", ip ?? ""); cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> AddDeploymentAsync(string user, string rootId, string path, string backup, string hash, string? service, string operation = "Deploy")
    {
        await using var db = Open(); await db.OpenAsync(); await using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO deployments(user_name,root_id,relative_path,backup_path,sha256,status,operation,related_service,created_at) VALUES($u,$r,$p,$b,$h,'Succeeded',$o,$s,$t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$u", user); cmd.Parameters.AddWithValue("$r", rootId); cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$b", backup); cmd.Parameters.AddWithValue("$h", hash); cmd.Parameters.AddWithValue("$o", operation); cmd.Parameters.AddWithValue("$s", service ?? ""); cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params (string, object?)[] values)
    {
        await using var db = Open(); await db.OpenAsync(); await using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(); var rows = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync()) { var row = new Dictionary<string, object?>(); for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i); rows.Add(row); }
        return rows;
    }

    public async Task<int> ExecuteAsync(string sql, params (string, object?)[] values)
    {
        await using var db = Open(); await db.OpenAsync(); await using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private SqliteConnection Open() => new(_connectionString);
}

internal static class Passwords
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"210000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('.'); if (parts.Length != 3 || !int.TryParse(parts[0], out var rounds)) return false;
        var salt = Convert.FromBase64String(parts[1]); var expected = Convert.FromBase64String(parts[2]);
        return CryptographicOperations.FixedTimeEquals(expected, Rfc2898DeriveBytes.Pbkdf2(password, salt, rounds, HashAlgorithmName.SHA256, expected.Length));
    }
}

using Microsoft.Data.Sqlite;

namespace ZomboidManager;

public sealed class WhitelistUser
{
    public string Username { get; set; } = "";
    public string AccessLevel { get; set; } = "";
    public string SteamId { get; set; } = "";
    public bool Banned { get; set; }
    public string Extra { get; set; } = "";
}

public static class WhitelistStore
{
    public static string? ResolveWorldName(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.LastIniFilePath))
            return Path.GetFileNameWithoutExtension(config.LastIniFilePath);
        return null;
    }

    public static string? ResolveDbPath(AppConfig config)
    {
        string? world = ResolveWorldName(config);
        if (string.IsNullOrWhiteSpace(world))
            return null;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.ZomboidDataPath))
            candidates.Add(Path.Combine(config.ZomboidDataPath, "db", world + ".db"));

        if (!string.IsNullOrWhiteSpace(config.LastIniFilePath))
        {
            string? iniDir = Path.GetDirectoryName(config.LastIniFilePath);
            if (!string.IsNullOrWhiteSpace(iniDir))
            {
                candidates.Add(Path.Combine(iniDir, "db", world + ".db"));
                string parent = Directory.GetParent(iniDir)?.FullName ?? "";
                if (!string.IsNullOrWhiteSpace(parent))
                    candidates.Add(Path.Combine(parent, "db", world + ".db"));
                if (string.Equals(Path.GetFileName(iniDir), "Server", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(parent))
                {
                    candidates.Add(Path.Combine(parent, "db", world + ".db"));
                }
            }
        }

        foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        return candidates.FirstOrDefault();
    }

    public static bool? ReadOpenJoinAllowed(AppConfig config)
    {
        string? ini = config.LastIniFilePath;
        if (string.IsNullOrWhiteSpace(ini) || !File.Exists(ini))
            return null;

        Dictionary<string, string> raw = IniManager.ReadIni(ini);
        if (!raw.TryGetValue("Open", out string? value) || string.IsNullOrWhiteSpace(value))
            return null;

        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value == "1";
    }

    public static (bool success, string message, List<WhitelistUser> users, string? dbPath) ReadUsers(AppConfig config)
    {
        string? dbPath = ResolveDbPath(config);
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            return (true, "Database not found yet. Start the dedicated server once so Project Zomboid creates db/{world}.db.",
                new List<WhitelistUser>(), dbPath);
        }

        string? tempDir = null;
        try
        {
            tempDir = CopyDbSnapshot(dbPath);
            string openPath = Path.Combine(tempDir, Path.GetFileName(dbPath));
            var users = ReadUsersFromSqlite(openPath);
            return (true, "OK", users, dbPath);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, new List<WhitelistUser>(), dbPath);
        }
        finally
        {
            if (tempDir is not null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    public static void RunSelfTest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zm-wl-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "servertest.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    """
                    CREATE TABLE whitelist (
                      username TEXT,
                      password TEXT,
                      accesslevel TEXT,
                      banned INTEGER,
                      steamid TEXT
                    );
                    INSERT INTO whitelist VALUES ('alice', '$2a$hashed', 'admin', 0, '76561198000000001');
                    INSERT INTO whitelist VALUES ('bob', '$2a$hashed', '', 1, '');
                    """;
                cmd.ExecuteNonQuery();
            }

            var users = ReadUsersFromSqlite(db);
            bool ok = users.Count == 2
                      && users.Any(u => u.Username == "alice" && u.AccessLevel == "admin" && u.SteamId.StartsWith("7656"))
                      && users.Any(u => u.Username == "bob" && u.Banned);

            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok,
                count = users.Count,
                names = users.Select(u => u.Username).ToArray()
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Environment.Exit(ok ? 0 : 1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static List<WhitelistUser> ReadUsersFromSqlite(string dbPath)
    {
        var users = new List<WhitelistUser>();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        };

        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND lower(name)='whitelist'";
            object? table = check.ExecuteScalar();
            if (table is null || table is DBNull)
                return users;
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(whitelist)";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                string col = reader.GetString(1);
                columns.Add(col);
            }
        }

        string? usernameCol = FirstColumn(columns, "username", "user", "name", "account");
        if (usernameCol is null)
            return users;

        string? accessCol = FirstColumn(columns, "accesslevel", "access_level", "role");
        string? steamCol = FirstColumn(columns, "steamid", "steam_id", "steamid64");
        string? bannedCol = FirstColumn(columns, "banned");
        string? extraCol = FirstColumn(columns, "lastconnection", "last_connection", "world", "displayName");

        var select = new List<string> { QuoteIdent(usernameCol) };
        if (accessCol is not null) select.Add(QuoteIdent(accessCol));
        if (steamCol is not null) select.Add(QuoteIdent(steamCol));
        if (bannedCol is not null) select.Add(QuoteIdent(bannedCol));
        if (extraCol is not null) select.Add(QuoteIdent(extraCol));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", select)} FROM whitelist ORDER BY {QuoteIdent(usernameCol)} COLLATE NOCASE";
        using SqliteDataReader rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var user = new WhitelistUser
            {
                Username = ReadString(rows, usernameCol)
            };
            if (string.IsNullOrWhiteSpace(user.Username))
                continue;
            if (accessCol is not null)
                user.AccessLevel = ReadString(rows, accessCol);
            if (steamCol is not null)
                user.SteamId = ReadString(rows, steamCol);
            if (bannedCol is not null)
                user.Banned = ReadBool(rows, bannedCol);
            if (extraCol is not null)
                user.Extra = ReadString(rows, extraCol);
            users.Add(user);
        }

        return users;
    }

    private static string CopyDbSnapshot(string dbPath)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "zm-wl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dest = Path.Combine(tempDir, Path.GetFileName(dbPath));
        CopyShared(dbPath, dest);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            string extra = dbPath + suffix;
            if (File.Exists(extra))
                CopyShared(extra, dest + suffix);
        }

        return tempDir;
    }

    private static void CopyShared(string source, string dest)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using FileStream output = File.Create(dest);
        input.CopyTo(output);
    }

    private static string? FirstColumn(HashSet<string> columns, params string[] names)
    {
        foreach (string name in names)
        {
            string? match = columns.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }

    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string ReadString(SqliteDataReader rows, string column)
    {
        int i = rows.GetOrdinal(column);
        if (rows.IsDBNull(i))
            return "";
        return rows.GetValue(i)?.ToString()?.Trim() ?? "";
    }

    private static bool ReadBool(SqliteDataReader rows, string column)
    {
        int i = rows.GetOrdinal(column);
        if (rows.IsDBNull(i))
            return false;
        object value = rows.GetValue(i);
        return value switch
        {
            bool b => b,
            long l => l != 0,
            int n => n != 0,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
            _ => false
        };
    }
}

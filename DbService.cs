using System.Text;
using Microsoft.Data.Sqlite;

namespace ODCL;

public static class DbPaths
{
    public static string DefaultDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "opencode.db");
}

public sealed class DbStats
{
    public long PageSize;
    public long FreelistPages;
    public long FreelistBytes => PageSize * FreelistPages;
    public long EventBytes;
    public long RelatedBytes;
    public long DbSize;
    public long WalSize;
    public long ShmSize;
    public long FreeDisk;
    public long TotalFileSize => DbSize + WalSize + ShmSize;
}

public sealed class SessionItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public long EventBytes { get; set; }
    public long EventCount { get; set; }
    public long RelatedBytes { get; set; }
    public long Created { get; set; }
    public long TotalBytes => EventBytes + RelatedBytes;
}

public sealed class MsgPart
{
    public string Id = "";
    public string MessageId = "";
    public long Time;
    public string Json = "";
}

public sealed class DbService
{
    public string DbPath { get; }
    public string DbDirectory => Path.GetDirectoryName(DbPath)!;

    public DbService(string dbPath)
    {
        DbPath = dbPath;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(
            $"Data Source={DbPath};Mode=ReadWrite;Foreign Keys=True;Default Timeout=60");
        conn.Open();
        return conn;
    }

    private static long Scalar(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static int Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteNonQuery();
    }

    public bool Exists() => File.Exists(DbPath);

    public string? Integrity()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return cmd.ExecuteScalar()?.ToString();
    }

    public DbStats GetStats()
    {
        var st = new DbStats();
        var fi = new FileInfo(DbPath);
        st.DbSize = fi.Exists ? fi.Length : 0;
        var wal = new FileInfo(DbPath + "-wal");
        st.WalSize = wal.Exists ? wal.Length : 0;
        var shm = new FileInfo(DbPath + "-shm");
        st.ShmSize = shm.Exists ? shm.Length : 0;
        try { st.FreeDisk = new DriveInfo(Path.GetPathRoot(DbPath) ?? "C:\\").AvailableFreeSpace; } catch { }
        using var c = Open();
        st.PageSize = Scalar(c, "PRAGMA page_size;");
        st.FreelistPages = Scalar(c, "PRAGMA freelist_count;");
        try
        {
            st.EventBytes = Scalar(c, "SELECT COALESCE(SUM(pgsize),0) FROM dbstat WHERE name LIKE 'event%' AND aggregate=TRUE;");
        }
        catch { st.EventBytes = Scalar(c, "SELECT COALESCE(SUM(LENGTH(data)),0) FROM event;"); }
        try
        {
            st.RelatedBytes = Scalar(c,
                "SELECT COALESCE(SUM(pgsize),0) FROM dbstat WHERE name IN ('message','part','session_message','session_input','session_context_epoch','todo','session') AND aggregate=TRUE;");
        }
        catch
        {
            st.RelatedBytes = Scalar(c, "SELECT COALESCE(SUM(LENGTH(data)),0) FROM message;")
                + Scalar(c, "SELECT COALESCE(SUM(LENGTH(data)),0) FROM part;");
        }
        return st;
    }

    public long OrphanEvents;
    public long OrphanBytes;

    public List<SessionItem> GetSessions()
    {
        var items = new Dictionary<string, SessionItem>();
        using var c = Open();
        using (var r = c.CreateCommand())
        {
            r.CommandText = "SELECT id,title,directory,time_created FROM session;";
            using var rd = r.ExecuteReader();
            while (rd.Read())
            {
                var it = new SessionItem
                {
                    Id = rd.GetString(0),
                    Title = rd.GetString(1),
                    Created = rd.GetInt64(3),
                    EventBytes = 0,
                };
                it.Sub = Path.GetFileName(rd.GetString(2).TrimEnd('\\', '/'));
                items[it.Id] = it;
            }
        }
        void AddAgg(SqliteConnection c, string sql, Action<(string, SessionItem), long, long> add)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                if (items.TryGetValue(rd.GetString(0), out var it))
                    add((rd.GetString(0), it), rd.GetInt64(1), rd.GetInt64(2));
            }
        }
        AddAgg(c, "SELECT aggregate_id, COUNT(*), COALESCE(SUM(LENGTH(data)),0) FROM event GROUP BY aggregate_id;",
            (p, cnt, b) => { p.Item2.EventCount = cnt; p.Item2.EventBytes = b; });
        AddAgg(c, "SELECT session_id, CAST(0 AS INTEGER), COALESCE(SUM(LENGTH(data)),0) FROM message GROUP BY session_id;",
            (p, _, b) => p.Item2.RelatedBytes += b);
        AddAgg(c, "SELECT session_id, CAST(0 AS INTEGER), COALESCE(SUM(LENGTH(data)),0) FROM part GROUP BY session_id;",
            (p, _, b) => p.Item2.RelatedBytes += b);
        using (var r = c.CreateCommand())
        {
            r.CommandText = "SELECT COUNT(*), COALESCE(SUM(LENGTH(data)),0) FROM event WHERE NOT EXISTS (SELECT 1 FROM session s WHERE s.id=event.aggregate_id);";
            using var rd = r.ExecuteReader();
            if (rd.Read()) { OrphanEvents = rd.GetInt64(0); OrphanBytes = rd.GetInt64(1); }
        }
        return items.Values.ToList();
    }

    public List<MsgPart> GetMessages(string sessionId)
    {
        var list = new List<MsgPart>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,time_created,data FROM message WHERE session_id=$s ORDER BY time_created,id;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new MsgPart { Id = rd.GetString(0), Time = rd.GetInt64(1), Json = rd.GetString(2) });
        return list;
    }

    public List<MsgPart> GetParts(string sessionId)
    {
        var list = new List<MsgPart>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,message_id,time_created,data FROM part WHERE session_id=$s ORDER BY time_created,id;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new MsgPart { Id = rd.GetString(0), MessageId = rd.GetString(1), Time = rd.GetInt64(2), Json = rd.GetString(3) });
        return list;
    }

    public (long events, long bytes) DeleteSession(string id, int batch, IProgress<long>? progress)
    {
        using var c = Open();
        long beforeE = Scalar(c, "SELECT COUNT(*) FROM event WHERE aggregate_id=$i;", ("$i", id));
        long beforeB = Scalar(c, "SELECT COALESCE(SUM(LENGTH(data)),0) FROM event WHERE aggregate_id=$i;", ("$i", id));
        if (batch > 0)
        {
            long done = 0;
            while (true)
            {
                int n = Exec(c,
                    "DELETE FROM event WHERE aggregate_id=$i AND rowid IN (SELECT rowid FROM event WHERE aggregate_id=$i LIMIT $b);",
                    ("$i", id), ("$b", batch));
                if (n == 0) break;
                done += n;
                TryCheckpoint(c);
                progress?.Report(done);
            }
        }
        else
        {
            Exec(c, "DELETE FROM event WHERE aggregate_id=$i;", ("$i", id));
        }
        Exec(c, "DELETE FROM event_sequence WHERE aggregate_id=$i;", ("$i", id));
        Exec(c, "DELETE FROM session WHERE id=$i;", ("$i", id));
        long afterE = Scalar(c, "SELECT COUNT(*) FROM event WHERE aggregate_id=$i;", ("$i", id));
        long afterB = Scalar(c, "SELECT COALESCE(SUM(LENGTH(data)),0) FROM event WHERE aggregate_id=$i;", ("$i", id));
        return (beforeE - afterE, beforeB - afterB);
    }

    public (long events, long bytes) DeleteOrphans(int batch, IProgress<long>? progress)
    {
        const string orphan = "NOT EXISTS (SELECT 1 FROM session s WHERE s.id=event.aggregate_id)";
        using var c = Open();
        long beforeE = Scalar(c, $"SELECT COUNT(*) FROM event WHERE {orphan};");
        long beforeB = Scalar(c, $"SELECT COALESCE(SUM(LENGTH(data)),0) FROM event WHERE {orphan};");
        if (batch > 0)
        {
            long done = 0;
            while (true)
            {
                int n = Exec(c, $"DELETE FROM event WHERE {orphan} AND rowid IN (SELECT rowid FROM event WHERE {orphan} LIMIT $b);",
                    ("$b", batch));
                if (n == 0) break;
                done += n;
                TryCheckpoint(c);
                progress?.Report(done);
            }
        }
        else
        {
            Exec(c, $"DELETE FROM event WHERE {orphan};");
        }
        Exec(c, "DELETE FROM event_sequence WHERE aggregate_id NOT IN (SELECT id FROM session) AND aggregate_id NOT IN (SELECT DISTINCT aggregate_id FROM event);");
        long afterB = Scalar(c, $"SELECT COALESCE(SUM(LENGTH(data)),0) FROM event WHERE {orphan};");
        return (beforeE, beforeB - afterB);
    }

    public void Vacuum()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM;";
        cmd.CommandTimeout = 900;
        cmd.ExecuteNonQuery();
    }

    public static void TryCheckpoint(SqliteConnection c)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var rd = cmd.ExecuteReader();
        }
        catch { }
    }

    public static void MoveDirectory(string source, string target, IProgress<string>? progress)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            progress?.Report(rel);
        }
    }
}
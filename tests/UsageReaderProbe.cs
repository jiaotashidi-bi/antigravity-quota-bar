using System;
using System.IO;
using System.Linq;
using System.Text;
using GeminiQuota;

internal static class UsageReaderProbe
{
    private static readonly DateTime Today = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Local);
    private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static byte[] Join(params byte[][] blocks) { return blocks.SelectMany(b => b).ToArray(); }
    private static byte[] Var(long n)
    {
        using (var stream = new MemoryStream())
        {
            while (n >= 128) { stream.WriteByte((byte)((n & 127) | 128)); n >>= 7; }
            stream.WriteByte((byte)n);
            return stream.ToArray();
        }
    }
    private static byte[] N(int field, long n) { return Join(Var(field * 8), Var(n)); }
    private static byte[] B(int field, byte[] bytes) { return Join(Var(field * 8 + 2), Var(bytes.Length), bytes); }
    private static byte[] S(int field, string s) { return B(field, Encoding.UTF8.GetBytes(s)); }

    private static byte[] Gen(string model, string id, string exec, byte[] indices, int input, int cached, int output)
    {
        var chat = B(4, Join(N(2, input), N(5, cached), N(3, output), N(9, output)));
        if (model != null) chat = Join(chat, S(21, model));
        if (id != null) chat = Join(chat, B(20, Join(S(1, "request_id"), S(2, id))));
        chat = Join(chat, B(9, B(10, Join(N(1, 200), N(4, 1000)))));
        return Join(B(1, chat), indices, exec == null ? new byte[0] : S(4, exec));
    }
    private static byte[] Step(DateTime local)
    {
        return B(1, N(1, (long)(local.ToUniversalTime() - Epoch).TotalSeconds));
    }
    private static byte[] Idx(int index) { return N(2, index); }

    private static void Exec(IntPtr db, string sql)
    {
        if (Sql.sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
            throw new Exception("Fixture SQL failed.");
    }
    private static IntPtr Open(string path)
    {
        IntPtr db;
        if (Sql.sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out db, 6, IntPtr.Zero) != 0)
            throw new Exception("Cannot create fixture.");
        return db;
    }
    private static string Create(string folder, Action<IntPtr> populate)
    {
        string file = Path.Combine(folder, Guid.NewGuid().ToString("D") + ".db");
        IntPtr db = Open(file);
        try
        {
            Exec(db, "CREATE TABLE gen_metadata(idx INTEGER PRIMARY KEY,data BLOB);CREATE TABLE steps(idx INTEGER PRIMARY KEY,metadata BLOB)");
            populate(db);
        }
        finally { Sql.sqlite3_close(db); }
        return file;
    }
    private static void Row(IntPtr db, string table, int index, byte[] value)
    {
        Exec(db, "INSERT INTO " + table + " VALUES(" + index + ", x'" + BitConverter.ToString(value).Replace("-", "") + "')");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    public static int Main(string[] args)
    {
        string folder = args[0];
        Check(!UsageReader.Read(Path.Combine(folder, "absent")).DirectoryExists, "missing folder");
        byte[] packed = B(2, Join(Var(300), Var(301)));
        var fallback = Gen("Beta", null, "execution-copy", packed, 20, 10, 5);
        string first = Create(folder, delegate(IntPtr db) {
            Row(db, "gen_metadata", 0, Gen("Alpha", "provider-request", null, Idx(0), 100, 25, 6));
            Row(db, "gen_metadata", 1, fallback);
            Row(db, "gen_metadata", 2, Gen(null, null, null, Idx(2), 1, 2, 3));
            Row(db, "gen_metadata", 3, Gen("Alpha", null, null, Idx(3), 2, 3, 4));
            Row(db, "gen_metadata", 4, Gen("Undated", "undated", null, Idx(4), 3, 4, 5));
            Row(db, "gen_metadata", 5, B(1, S(21, "Usage missing")));
            Row(db, "gen_metadata", 6, new byte[] { 0x0a, 0x80 });
            Row(db, "steps", 0, Step(Today.Date)); // local midnight
            Row(db, "steps", 300, Step(Today.AddDays(-5)));
            Row(db, "steps", 301, Step(Today.AddDays(-6))); // earliest linked step
            Row(db, "steps", 2, Step(Today.AddDays(-29)));
            Row(db, "steps", 3, Step(Today.AddDays(-30)));
        });
        Check(ContextReader.ReadFile(first) == null, "latest incomplete generation should clear context");
        string branch = Create(folder, delegate(IntPtr db) {
            Row(db, "gen_metadata", 0, Gen("Alpha", "provider-request", null, Idx(0), 100, 25, 9));
            Row(db, "gen_metadata", 1, fallback);
            Row(db, "steps", 0, Step(Today.Date));
            Row(db, "steps", 300, Step(Today.AddDays(-5)));
            Row(db, "steps", 301, Step(Today.AddDays(-6)));
        });
        var context = ContextReader.ReadFile(branch);
        Check(context != null && context.Model == "Beta" && context.Used == 200 && context.Limit == 1000, "existing context reading");
        Create(folder, delegate(IntPtr db) { }); // readable empty database
        File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("D") + ".db"), "broken");
        File.WriteAllText(Path.Combine(folder, "unrelated.db"), "ignored");
        var locked = Open(Create(folder, delegate(IntPtr db) { }));
        UsageReport report;
        try
        {
            Exec(locked, "BEGIN EXCLUSIVE");
            report = UsageReader.Read(folder);
        }
        finally { Sql.sqlite3_close(locked); }
        Check(report.DirectoryExists && report.ReadDatabases == 3 && report.SkippedDatabases == 2 &&
            report.SkippedRecords == 1 && report.UndatedRequests == 1, "partial database diagnostics");
        var all = report.Summarize(0, Today);
        Check(all.Requests == 5 && all.Input == 126 && all.Cache == 44 && all.Output == 26, "deduplicated provider usage");
        Check(all.Models.Count == 4 && all.Models[0].Model == "Alpha" && all.Models[0].Requests == 2 &&
            all.Models.Any(m => m.Model == "未知模型"), "model totals");
        Check(all.Days.Count == 4 && all.Days[0].Day == Today.Date.AddDays(-30), "all-time daily series");
        var week = report.Summarize(7, Today);
        Check(week.Requests == 2 && week.Days.Count == 2 && week.Days[0].Day == Today.Date.AddDays(-6) &&
            week.Days[0].Input == 20 && week.Days[1].Output == 9, "local 7-day boundary and packed steps");
        Check(report.Summarize(30, Today).Requests == 3 && report.Summarize(1, Today).Requests == 1,
            "30-day and today boundaries");
        string walPath = Create(folder, delegate(IntPtr db) { });
        var writer = Open(walPath);
        try
        {
            Exec(writer, "PRAGMA journal_mode=WAL;PRAGMA wal_autocheckpoint=0;BEGIN IMMEDIATE");
            Row(writer, "gen_metadata", 12, Gen("Active", "live-wal", null, Idx(12), 9, 3, 1));
            Row(writer, "steps", 12, Step(Today));
            Check(UsageReader.Read(folder).Summarize(1, Today).Requests == 1, "exclude uncommitted WAL rows");
            Exec(writer, "COMMIT");
            Check(UsageReader.Read(folder).Summarize(1, Today).Requests == 2, "include committed WAL rows");
            Exec(writer, "DROP TABLE steps");
            var older = UsageReader.Read(folder);
            Check(older.UndatedRequests == 2 && older.Summarize(0, Today).Requests == 6,
                "preserve metered generations when old databases have no steps table");
        }
        finally { Sql.sqlite3_close(writer); }
        Console.WriteLine("PASS: local ranges, daily/model totals, branch copies, missing/locked data and context reading.");
        return 0;
    }
}

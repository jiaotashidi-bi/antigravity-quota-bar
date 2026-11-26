using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GeminiQuota
{
    public sealed class ContextReading
    {
        public long Used, Limit;
        public string Model;

        internal static string Describe(ContextReading reading)
        {
            if (reading == null || reading.Limit <= 0)
                return "暂无可读的上下文记录。模型完成一次请求后会更新。";
            string model = String.IsNullOrWhiteSpace(reading.Model) ? "" : "模型   " + reading.Model + "\n";
            return model + String.Format("预计剩余   {0:N0} tokens\n已用估算   {1:N0} tokens\n窗口上限   {2:N0} tokens",
                Math.Max(0L, reading.Limit - reading.Used), reading.Used, reading.Limit);
        }
    }

    public static class ContextReader
    {
        internal static string ConversationsDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "antigravity", "conversations"); }
        }

        public static ContextReading Read(string id)
        {
            Guid uuid;
            if (!Guid.TryParseExact(id, "D", out uuid)) return null;
            return ReadFile(Path.Combine(ConversationsDirectory, uuid.ToString("D") + ".db"));
        }

        internal static ContextReading ReadFile(string file)
        {
            if (!File.Exists(file)) return null;
            try
            {
                using (var db = new ConversationDb(file))
                {
                    foreach (var row in db.Rows("SELECT idx, rowid FROM gen_metadata ORDER BY idx DESC LIMIT 1"))
                    using (var blob = db.Blob("gen_metadata", "data", row.Id))
                    {
                        // Antigravity 2.34+: chat_model -> chat_start_metadata -> context_window_metadata.
                        var chat = blob.Message(blob.Root, 1);
                        var window = blob.Message(blob.Message(chat, 9), 10);
                        var used = blob.First(window, 1, 0);
                        var limit = blob.First(window, 4, 0);
                        if (!used.Present || limit.Number == 0 || used.Number > Int32.MaxValue ||
                            limit.Number > Int32.MaxValue) return null;
                        return new ContextReading { Used = (long)used.Number, Limit = (long)limit.Number,
                            Model = blob.Text(blob.First(chat, 21, 2), 256) };
                    }
                }
            }
            catch (InvalidDataException) { }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return null;
        }
    }

    internal struct MetadataRow
    {
        internal long Index, Id;
    }

    internal sealed class ConversationDb : IDisposable
    {
        private IntPtr db;

        internal ConversationDb(string path)
        {
            if (Sql.sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out db, 1, IntPtr.Zero) != 0)
            {
                Dispose();
                throw new InvalidDataException("Could not open the conversation database read-only.");
            }
            Sql.sqlite3_busy_timeout(db, 100);
        }

        // Statements yield (index, rowid). The open SELECT keeps the read snapshot
        // alive while the caller inspects its row using SQLite's incremental blob API.
        internal IEnumerable<MetadataRow> Rows(string sql)
        {
            IntPtr stmt = IntPtr.Zero;
            try
            {
                if (Sql.sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero) != 0)
                    throw new InvalidDataException("Could not prepare the metadata query.");
                int rc;
                while ((rc = Sql.sqlite3_step(stmt)) == 100)
                    yield return new MetadataRow { Index = Sql.sqlite3_column_int64(stmt, 0), Id = Sql.sqlite3_column_int64(stmt, 1) };
                if (rc != 101) throw new InvalidDataException("Could not finish the metadata query.");
            }
            finally { if (stmt != IntPtr.Zero) Sql.sqlite3_finalize(stmt); }
        }

        internal void BeginSnapshot()
        {
            if (Sql.sqlite3_exec(db, "BEGIN", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
                throw new InvalidDataException("Could not start the read snapshot.");
        }

        internal MetadataBlob Blob(string table, string column, long rowid)
        {
            IntPtr blob;
            if (Sql.sqlite3_blob_open(db, "main", table, column, rowid, 0, out blob) != 0)
                throw new InvalidDataException("Could not open the metadata blob.");
            return new MetadataBlob(blob);
        }

        public void Dispose()
        {
            if (db != IntPtr.Zero) { Sql.sqlite3_close(db); db = IntPtr.Zero; }
        }
    }

    internal struct MetadataField
    {
        internal bool Present;
        internal int Id, Wire, Start, End;
        internal ulong Number;
    }

    internal sealed class MetadataBlob : IDisposable
    {
        private IntPtr blob;
        private readonly byte[] one = new byte[1];
        internal readonly MetadataField Root;

        internal MetadataBlob(IntPtr source)
        {
            blob = source;
            Root = new MetadataField { Present = true, End = Sql.sqlite3_blob_bytes(blob) };
        }

        internal MetadataField Message(MetadataField parent, int id) { return First(parent, id, 2); }

        internal MetadataField First(MetadataField parent, int id, int wire)
        {
            foreach (var field in Fields(parent))
                if (field.Id == id && field.Wire == wire) return field;
            return new MetadataField();
        }

        internal string Text(MetadataField field, int maxBytes)
        {
            int length = field.End - field.Start;
            if (!field.Present || length == 0 || length > maxBytes) return null;
            var bytes = new byte[length];
            if (Sql.sqlite3_blob_read(blob, bytes, bytes.Length, field.Start) != 0)
                throw new InvalidDataException();
            return Encoding.UTF8.GetString(bytes);
        }

        // Inspect wire tags and varints only. Skip length-delimited payloads such as
        // message bodies, tool output and prompts by offset without loading their bytes.
        internal IEnumerable<MetadataField> Fields(MetadataField parent)
        {
            int at = parent.Start;
            while (at < parent.End)
            {
                ulong tag = Varint(ref at, parent.End);
                ulong number = tag >> 3;
                if (number == 0 || number > 536870911) throw new InvalidDataException();
                var field = new MetadataField { Present = true, Id = (int)number, Wire = (int)(tag & 7) };
                switch (field.Wire)
                {
                    case 0:
                        field.Number = Varint(ref at, parent.End);
                        break;
                    case 1:
                    case 2:
                    case 5:
                        ulong length = field.Wire == 2 ? Varint(ref at, parent.End) : (field.Wire == 1 ? 8UL : 4UL);
                        if (length > (ulong)(parent.End - at)) throw new InvalidDataException();
                        field.Start = at;
                        at += (int)length;
                        field.End = at;
                        break;
                    default: throw new InvalidDataException();
                }
                yield return field;
            }
        }

        internal ulong Varint(ref int at, int end)
        {
            ulong result = 0;
            for (int shift = 0; shift <= 63; shift += 7)
            {
                if (at >= end || Sql.sqlite3_blob_read(blob, one, 1, at++) != 0) throw new InvalidDataException();
                byte b = one[0];
                if (shift == 63 && b > 1) throw new InvalidDataException();
                result |= (ulong)(b & 127) << shift;
                if (b < 128) return result;
            }
            throw new InvalidDataException();
        }

        public void Dispose()
        {
            if (blob != IntPtr.Zero) { Sql.sqlite3_blob_close(blob); blob = IntPtr.Zero; }
        }
    }

    internal static class Sql
    {
        private const string Dll = "winsqlite3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr db, string sql, IntPtr callback, IntPtr parameter, IntPtr error);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, string sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern int sqlite3_blob_open(IntPtr db, string schema, string table, string column,
            long rowid, int write, out IntPtr blob);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_blob_bytes(IntPtr blob);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_blob_read(IntPtr blob, [Out] byte[] data, int count, int offset);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_blob_close(IntPtr blob);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr db);
    }
}

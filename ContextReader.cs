using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GeminiQuota
{
    public sealed class ContextReading
    {
        public long Used, Limit;
        public string Model;
    }

    public static class ContextReader
    {
        // The persisted generation contains its own context estimate and request budget.
        // Read the newest generation only, even when it does not yet have both fields.
        public static ContextReading Read(string id)
        {
            Guid uuid;
            if (!Guid.TryParseExact(id, "D", out uuid)) return null;
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "antigravity", "conversations", uuid.ToString("D") + ".db");
            if (!File.Exists(file)) return null;

            IntPtr db = IntPtr.Zero, statement = IntPtr.Zero, blob = IntPtr.Zero;
            try
            {
                if (Sql.sqlite3_open_v2(Encoding.UTF8.GetBytes(file + "\0"), out db, 1, IntPtr.Zero) != 0)
                    return null; // SQLITE_OPEN_READONLY
                Sql.sqlite3_busy_timeout(db, 100);
                const string latest = "SELECT rowid FROM gen_metadata ORDER BY idx DESC LIMIT 1";
                if (Sql.sqlite3_prepare_v2(db, latest, -1, out statement, IntPtr.Zero) != 0 ||
                    Sql.sqlite3_step(statement) != 100) return null; // SQLITE_ROW
                // The unfinalized SELECT holds the read snapshot until the blob is closed.
                if (Sql.sqlite3_blob_open(db, "main", "gen_metadata", "data",
                    Sql.sqlite3_column_int64(statement, 0), 0, out blob) != 0) return null;
                return new Generation(blob).Read();
            }
            catch (InvalidDataException) { return null; }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            finally
            {
                if (blob != IntPtr.Zero) Sql.sqlite3_blob_close(blob);
                if (statement != IntPtr.Zero) Sql.sqlite3_finalize(statement);
                if (db != IntPtr.Zero) Sql.sqlite3_close(db);
            }
        }

        private struct Field
        {
            public int Start, End;
            public ulong Number;
        }

        private sealed class Generation
        {
            private readonly IntPtr blob;
            private readonly byte[] one = new byte[1];
            internal Generation(IntPtr source) { blob = source; }

            internal ContextReading Read()
            {
                var root = new Field { End = Sql.sqlite3_blob_bytes(blob) };
                Field chat, start, window, used, limit, model;
                // Antigravity 2.34: generator.chat_model -> chat_start_metadata ->
                // context_window_metadata -> estimated_tokens_used / max_context_tokens.
                if (!Find(root, 1, 2, out chat) || !Find(chat, 9, 2, out start) ||
                    !Find(start, 10, 2, out window) || !Find(window, 1, 0, out used) ||
                    !Find(window, 4, 0, out limit) || limit.Number == 0 ||
                    used.Number > Int32.MaxValue || limit.Number > Int32.MaxValue) return null;

                var reading = new ContextReading { Used = (long)used.Number, Limit = (long)limit.Number };
                // model_display_name belongs to the very same call as both counts.
                if (Find(chat, 21, 2, out model) && model.End > model.Start && model.End - model.Start <= 256)
                {
                    var text = new byte[model.End - model.Start];
                    if (Sql.sqlite3_blob_read(blob, text, text.Length, model.Start) == 0)
                        reading.Model = Encoding.UTF8.GetString(text);
                }
                return reading;
            }

            // Use SQLite incremental blob reads. Unrelated length-delimited fields,
            // including prompts and tool output, are skipped by offset without loading them.
            private bool Find(Field parent, int field, int type, out Field value)
            {
                value = new Field();
                int at = parent.Start;
                while (at < parent.End)
                {
                    ulong tag = Varint(ref at, parent.End);
                    ulong number = tag >> 3;
                    int wire = (int)(tag & 7);
                    if (number == 0 || number > 536870911) throw new InvalidDataException();
                    var current = new Field();
                    switch (wire)
                    {
                        case 0:
                            current.Number = Varint(ref at, parent.End);
                            break;
                        case 1:
                        case 2:
                        case 5:
                            ulong length = wire == 2 ? Varint(ref at, parent.End) : (wire == 1 ? 8UL : 4UL);
                            if (length > (ulong)(parent.End - at)) throw new InvalidDataException();
                            current.Start = at;
                            at += (int)length;
                            current.End = at;
                            break;
                        default: throw new InvalidDataException();
                    }
                    if (number == (ulong)field && wire == type) { value = current; return true; }
                }
                return false;
            }

            private ulong Varint(ref int at, int end)
            {
                ulong result = 0;
                for (int shift = 0; shift <= 63; shift += 7)
                {
                    if (at >= end || Sql.sqlite3_blob_read(blob, one, 1, at++) != 0)
                        throw new InvalidDataException();
                    byte b = one[0];
                    if (shift == 63 && b > 1) throw new InvalidDataException();
                    result |= (ulong)(b & 127) << shift;
                    if (b < 128) return result;
                }
                throw new InvalidDataException();
            }
        }

        private static class Sql
        {
            private const string Dll = "winsqlite3.dll";
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
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
}

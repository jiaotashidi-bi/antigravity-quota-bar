using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GeminiQuota
{
    internal class UsageCounts
    {
        internal long Requests, Input, Cache, Output;
        internal void Add(UsageEntry entry)
        {
            Requests++;
            Input += entry.Input;
            Cache += entry.Cache;
            Output += entry.Output;
        }
    }

    internal sealed class ModelUsage : UsageCounts { internal string Model; }
    internal sealed class DayUsage : UsageCounts { internal DateTime Day; }
    internal sealed class UsageSummary : UsageCounts
    {
        internal readonly List<ModelUsage> Models = new List<ModelUsage>();
        internal readonly List<DayUsage> Days = new List<DayUsage>();
    }

    internal sealed class UsageEntry
    {
        internal string Model, RequestId, ExecutionId;
        internal long Index, Input, Cache, Output;
        internal DateTime? TimeUtc;
    }

    internal sealed class UsageReport
    {
        internal bool DirectoryExists;
        internal int ReadDatabases, SkippedDatabases, SkippedRecords;
        internal long UndatedRequests;
        private readonly List<UsageEntry> entries = new List<UsageEntry>();
        private readonly Dictionary<string, UsageEntry> byId = new Dictionary<string, UsageEntry>(StringComparer.Ordinal);

        internal void Add(UsageEntry entry)
        {
            string key = null;
            if (entry.RequestId != null) key = "r:" + entry.RequestId;
            // A branch can contain an exact copy of a generation without a provider
            // request ID. Match only if its execution ID, index, timestamp, model and
            // all counts agree. Leave ambiguous generations separate.
            else if (entry.ExecutionId != null && entry.TimeUtc.HasValue)
                key = String.Format(CultureInfo.InvariantCulture, "e:{0}:{1}:{2}:{3}:{4}:{5}:{6}:{7}",
                    entry.ExecutionId.Length, entry.ExecutionId, entry.Index, entry.TimeUtc.Value.Ticks,
                    entry.Input, entry.Cache, entry.Output, entry.Model);
            UsageEntry existing;
            if (key != null && byId.TryGetValue(key, out existing))
            {
                // Prefer one complete set of provider counts for a request. An earlier
                // branch may have captured the same request before output was written.
                if (entry.Output > existing.Output || (entry.Output == existing.Output &&
                    entry.Input + entry.Cache > existing.Input + existing.Cache))
                {
                    existing.Input = entry.Input;
                    existing.Cache = entry.Cache;
                    existing.Output = entry.Output;
                }
                if (String.IsNullOrWhiteSpace(existing.Model)) existing.Model = entry.Model;
                if (entry.TimeUtc.HasValue && (!existing.TimeUtc.HasValue || entry.TimeUtc.Value < existing.TimeUtc.Value))
                {
                    if (!existing.TimeUtc.HasValue) UndatedRequests--;
                    existing.TimeUtc = entry.TimeUtc;
                }
                return;
            }
            if (key != null) byId.Add(key, entry);
            if (!entry.TimeUtc.HasValue) UndatedRequests++;
            entries.Add(entry);
        }

        internal UsageSummary Summarize(int days, DateTime nowLocal)
        {
            var total = new UsageSummary();
            var groups = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            var daily = new SortedDictionary<DateTime, DayUsage>();
            DateTime today = (nowLocal.Kind == DateTimeKind.Utc ? nowLocal.ToLocalTime() : nowLocal).Date;
            DateTime first = days > 0 ? today.AddDays(1 - days) : DateTime.MinValue;
            foreach (var entry in entries)
            {
                if (entry.TimeUtc.HasValue)
                {
                    DateTime date = entry.TimeUtc.Value.ToLocalTime().Date;
                    if (days > 0 && (date < first || date > today)) continue;
                    DayUsage day;
                    if (!daily.TryGetValue(date, out day)) daily[date] = day = new DayUsage { Day = date };
                    day.Add(entry);
                }
                else if (days > 0) continue;
                total.Add(entry);
                string model = String.IsNullOrWhiteSpace(entry.Model) ? "未知模型" : entry.Model;
                ModelUsage group;
                if (!groups.TryGetValue(model, out group))
                    groups[model] = group = new ModelUsage { Model = model };
                group.Add(entry);
            }
            total.Models.AddRange(groups.Values);
            total.Days.AddRange(daily.Values);
            total.Models.Sort(delegate(ModelUsage a, ModelUsage b)
            {
                int byTokens = (b.Input + b.Cache + b.Output).CompareTo(a.Input + a.Cache + a.Output);
                return byTokens != 0 ? byTokens : StringComparer.Ordinal.Compare(a.Model, b.Model);
            });
            return total;
        }
    }

    internal static class UsageReader
    {
        internal static UsageReport Read(string directory)
        {
            var report = new UsageReport { DirectoryExists = Directory.Exists(directory) };
            if (!report.DirectoryExists) return report;
            string[] files;
            try { files = Directory.GetFiles(directory, "*.db"); }
            catch (UnauthorizedAccessException) { report.SkippedDatabases++; return report; }
            catch (IOException) { report.SkippedDatabases++; return report; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                Guid id;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out id)) continue;
                try { ReadFile(file, report); report.ReadDatabases++; }
                catch (InvalidDataException) { report.SkippedDatabases++; }
                catch (DllNotFoundException) { report.SkippedDatabases++; }
                catch (EntryPointNotFoundException) { report.SkippedDatabases++; }
            }
            return report;
        }

        private static void ReadFile(string path, UsageReport report)
        {
            using (var db = new ConversationDb(path))
            {
                db.BeginSnapshot();
                var entries = new List<UsageEntry>();
                var byStep = new Dictionary<long, List<UsageEntry>>();
                foreach (var row in db.Rows("SELECT idx, rowid FROM gen_metadata ORDER BY idx"))
                {
                    try
                    {
                        using (var blob = db.Blob("gen_metadata", "data", row.Id))
                        {
                            var steps = new List<long>();
                            var entry = DecodeGeneration(blob, row.Index, steps);
                            if (entry == null) continue;
                            entries.Add(entry);
                            foreach (long index in steps)
                            {
                                List<UsageEntry> linked;
                                if (!byStep.TryGetValue(index, out linked)) byStep[index] = linked = new List<UsageEntry>();
                                linked.Add(entry);
                            }
                        }
                    }
                    catch (InvalidDataException) { report.SkippedRecords++; }
                }
                if (byStep.Count != 0) ReadDates(db, byStep);
                foreach (var entry in entries) report.Add(entry);
            }
        }

        private static UsageEntry DecodeGeneration(MetadataBlob blob, long index, List<long> steps)
        {
            var chat = blob.Message(blob.Root, 1);
            var usage = blob.Message(chat, 4);
            var entry = new UsageEntry { Index = index };
            bool hasCount = false;
            foreach (var f in blob.Fields(usage))
            {
                if (f.Wire != 0 || (f.Id != 2 && f.Id != 3 && f.Id != 5)) continue;
                if (f.Number > UInt32.MaxValue) throw new InvalidDataException();
                hasCount = true;
                if (f.Id == 2) entry.Input = (long)f.Number;
                if (f.Id == 3) entry.Output = (long)f.Number;
                if (f.Id == 5) entry.Cache = (long)f.Number;
            }
            if (!hasCount) return null;
            entry.Model = blob.Text(blob.First(chat, 21, 2), 256) ?? blob.Text(blob.First(chat, 19, 2), 256);
            foreach (var pair in blob.Fields(chat))
            {
                if (pair.Id != 20 || pair.Wire != 2 || blob.Text(blob.First(pair, 1, 2), 32) != "request_id") continue;
                entry.RequestId = blob.Text(blob.First(pair, 2, 2), 512);
                if (entry.RequestId != null) break;
            }
            foreach (var f in blob.Fields(blob.Root))
            {
                if (f.Id == 4 && f.Wire == 2) entry.ExecutionId = blob.Text(f, 512);
                if (f.Id != 2) continue;
                if (f.Wire == 0 && f.Number <= Int32.MaxValue) steps.Add((long)f.Number);
                if (f.Wire != 2) continue;
                int at = f.Start;
                while (at < f.End)
                {
                    ulong idx = blob.Varint(ref at, f.End);
                    if (idx <= Int32.MaxValue) steps.Add((long)idx);
                }
            }
            return entry;
        }

        private static void ReadDates(ConversationDb db, Dictionary<long, List<UsageEntry>> byStep)
        {
            try
            {
                foreach (var row in db.Rows("SELECT idx, rowid FROM steps"))
                {
                    List<UsageEntry> linked;
                    if (!byStep.TryGetValue(row.Index, out linked)) continue;
                    try
                    {
                        using (var blob = db.Blob("steps", "metadata", row.Id))
                        {
                            var stamp = blob.Message(blob.Root, 1); // native step creation timestamp
                            ulong seconds = blob.First(stamp, 1, 0).Number;
                            ulong nanos = blob.First(stamp, 2, 0).Number;
                            if (seconds == 0 || seconds > 253402300799UL || nanos >= 1000000000UL) continue;
                            var time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                .AddTicks((long)seconds * TimeSpan.TicksPerSecond + (long)nanos / 100L);
                            foreach (var entry in linked)
                                if (!entry.TimeUtc.HasValue || time < entry.TimeUtc.Value) entry.TimeUtc = time;
                        }
                    }
                    catch (InvalidDataException) { /* Count usage without a date. */ }
                }
            }
            catch (InvalidDataException) { /* Older databases may lack the steps table. */ }
        }
    }
}

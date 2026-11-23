using System;
using System.Collections.Generic;

namespace GeminiQuota
{
    public sealed class QuotaBucket
    {
        public int Percent = -1;
        public DateTime? ResetUtc;
        public bool ResetApproximate;
        public DateTime SeenUtc;
    }

    public sealed class QuotaGroup
    {
        public string Name;
        public QuotaBucket Weekly, FiveHour;
    }

    public sealed class QuotaSnapshot
    {
        public readonly List<QuotaGroup> Groups = new List<QuotaGroup>();
        public DateTime SeenUtc;

        public QuotaGroup FindGroup(string name)
        {
            return Groups.Find(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Earlier consumers access the standard Gemini pool directly.
        private QuotaGroup StandardGemini(bool create)
        {
            var group = FindGroup("Gemini Models");
            if (group == null && create)
            {
                group = new QuotaGroup { Name = "Gemini Models" };
                Groups.Add(group);
            }
            return group;
        }

        public QuotaBucket Weekly
        {
            get { var group = StandardGemini(false); return group == null ? null : group.Weekly; }
            set { StandardGemini(true).Weekly = value; }
        }

        public QuotaBucket FiveHour
        {
            get { var group = StandardGemini(false); return group == null ? null : group.FiveHour; }
            set { StandardGemini(true).FiveHour = value; }
        }

        public static string Display(QuotaBucket bucket, DateTime now)
        {
            if (bucket == null || bucket.Percent < 0 || bucket.Percent > 100 ||
                (bucket.ResetUtc.HasValue && now >= bucket.ResetUtc.Value))
                return "—";
            return bucket.Percent + "%";
        }

        public static string ResetIn(QuotaBucket bucket, DateTime nowUtc)
        {
            if (bucket == null || bucket.Percent < 0 || bucket.Percent > 100 || !bucket.ResetUtc.HasValue) return "—";
            TimeSpan left = bucket.ResetUtc.Value - nowUtc;
            if (left <= TimeSpan.Zero) return "重置中";
            if (left.TotalMinutes < 1) return "不足1分";
            string prefix = bucket.ResetApproximate ? "约" : "";
            if (left.Days > 0) return prefix + left.Days + "天" + (left.Hours == 0 ? "" : left.Hours + "小时");
            if (left.Hours > 0) return prefix + left.Hours + "小时" + (left.Minutes == 0 ? "" : left.Minutes + "分");
            return prefix + left.Minutes + "分";
        }
    }
}

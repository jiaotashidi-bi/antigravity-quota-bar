using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace GeminiQuota
{
    public static class NativeUsage
    {
        private const string Weekly = "Weekly limit", FiveHour = "5h limit";
        private static readonly Condition UsageMenu = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
            new PropertyCondition(AutomationElement.NameProperty, "View Usage"));
        private static readonly Condition TextOnly =
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        private static readonly Regex Remaining = new Regex(@"^(100|[1-9]?[0-9])\s*%\s*left$", RegexOptions.CultureInvariant);
        private static readonly Regex Reset = new Regex(
            @"^Resets in\s+(?:(?<value>[0-9]{1,4})\s*(?<unit>[wdhms])\s*)+$", RegexOptions.CultureInvariant);

        public static QuotaSnapshot Read(AutomationElement root)
        {
            if (root == null) return null;
            try
            {
                var request = new CacheRequest { TreeFilter = Automation.RawViewCondition };
                request.Add(AutomationElement.NameProperty);
                request.Add(AutomationElement.ControlTypeProperty);
                using (request.Activate())
                {
                    var host = root.Current.BoundingRectangle;
                    foreach (AutomationElement menu in root.FindAll(TreeScope.Descendants, UsageMenu))
                    {
                        // Chromium's portalled submenu reports IsOffscreen even while visible.
                        var bounds = menu.Current.BoundingRectangle;
                        if (host.IsEmpty || bounds.IsEmpty || bounds.Width <= 0 ||
                            bounds.Height <= 0 || !host.IntersectsWith(bounds)) continue;

                        var result = new QuotaSnapshot { SeenUtc = DateTime.UtcNow };
                        var compact = new List<string>();
                        foreach (AutomationElement child in menu.FindAll(TreeScope.Children, Condition.TrueCondition))
                        {
                            var name = (child.Cached.Name ?? "").Trim();
                            if (child.Cached.ControlType == ControlType.Text)
                            {
                                compact.Add(name);
                            }
                            else if (child.Cached.ControlType == ControlType.Group && name.Length > 0)
                            {
                                ReadCompact(result, compact);
                                compact.Clear();
                                var labels = new List<string>();
                                foreach (AutomationElement text in child.FindAll(TreeScope.Descendants, TextOnly))
                                    labels.Add((text.Cached.Name ?? "").Trim());
                                ReadGroup(result, name, labels);
                            }
                        }
                        ReadCompact(result, compact);
                        if (result.Groups.Count > 0) return result;
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
            return null;
        }

        // Named groups put each window label before the percentage.
        private static void ReadGroup(QuotaSnapshot result, string group, List<string> labels)
        {
            for (int i = 0; i + 1 < labels.Count; i++)
            {
                string window = labels[i];
                if (!IsWindow(window)) continue;
                int valueIndex = i + 1;
                QuotaBucket bucket = ReadPercent(labels, ref valueIndex);
                if (bucket == null) continue;
                ReadReset(bucket, labels, valueIndex + 1, result.SeenUtc);
                Save(result, group, window, bucket);
                i = valueIndex;
            }
        }

        // A single-window group, such as Gemini 4 Argon, is rendered directly
        // inside the menu as a name and percentage followed by its window label.
        private static void ReadCompact(QuotaSnapshot result, List<string> labels)
        {
            for (int i = 0; i + 2 < labels.Count; i++)
            {
                int end = i + 1;
                QuotaBucket bucket = ReadPercent(labels, ref end);
                if (bucket == null || end + 1 >= labels.Count || !IsWindow(labels[end + 1])) continue;
                string name = labels[i];
                if (name.Length == 0 || name == "Usage" || IsWindow(name)) continue;
                ReadReset(bucket, labels, end + 2, result.SeenUtc);
                Save(result, name, labels[end + 1], bucket);
                i = end + 1;
            }
        }

        private static bool IsWindow(string text) { return text == Weekly || text == FiveHour; }

        private static QuotaBucket ReadPercent(List<string> labels, ref int index)
        {
            // Chromium exposes "64" and "% left" as siblings in the desktop app.
            string text = labels[index];
            var match = Remaining.Match(text);
            if (!match.Success && index + 1 < labels.Count)
            {
                match = Remaining.Match(text + labels[index + 1]);
                if (match.Success) index++;
            }
            return match.Success
                ? new QuotaBucket { Percent = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) }
                : null;
        }

        private static void ReadReset(QuotaBucket bucket, List<string> labels, int index, DateTime seenUtc)
        {
            if (index >= labels.Count) return;
            var match = Reset.Match(labels[index]);
            if (!match.Success) return;
            var values = match.Groups["value"].Captures;
            var units = match.Groups["unit"].Captures;
            double seconds = 0, precision = double.MaxValue;
            for (int i = 0; i < values.Count; i++)
            {
                double unit;
                switch (units[i].Value)
                {
                    case "w": unit = 604800; break;
                    case "d": unit = 86400; break;
                    case "h": unit = 3600; break;
                    case "m": unit = 60; break;
                    default: unit = 1; break;
                }
                seconds += int.Parse(values[i].Value, CultureInfo.InvariantCulture) * unit;
                precision = Math.Min(precision, unit);
            }
            // "3h 24m" covers an entire minute. Use its midpoint until an exact
            // reset timestamp is available, so it does not display 3h 23m immediately.
            bucket.ResetUtc = seenUtc.AddSeconds(seconds + precision / 2);
            bucket.ResetApproximate = true;
        }

        private static void Save(QuotaSnapshot result, string name, string window, QuotaBucket bucket)
        {
            var group = result.FindGroup(name);
            if (group == null)
            {
                group = new QuotaGroup { Name = name };
                result.Groups.Add(group);
            }
            if (window == Weekly) group.Weekly = bucket;
            else group.FiveHour = bucket;
        }
    }
}

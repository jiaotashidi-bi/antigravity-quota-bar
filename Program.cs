using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace GeminiQuota
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool first;
            using (var instance = new Mutex(true, @"Local\Antigravity.GeminiQuotaBar", out first))
            {
                if (!first) return;
                Native.EnableDpi();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new MonitorContext()) Application.Run(app);
            }
        }
    }

    internal sealed class LayoutSample
    {
        public IntPtr Host;
        public Rectangle Anchor;
        public Rectangle Window;
        public QuotaSnapshot Usage;
        public string ConversationId;
        public bool ContextChecked;
        public ContextReading Context;
    }

    internal sealed class MonitorContext : ApplicationContext
    {
        private readonly QuotaBar bar = new QuotaBar();
        private readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        private Task<QuotaSnapshot> quotaRead;
        private Task<LayoutSample> layoutRead;
        private DateTime nextLayout, nextRefresh, nextHostCheck, requestStarted, lastMenuReading;
        private readonly DateTime startupGrace = DateTime.UtcNow.AddSeconds(6);
        private IntPtr lastForeground, activeHost;
        private Rectangle anchor, lastWindow;
        private QuotaSnapshot current, latestCli;
        private string conversationId;
        private ContextReading context;
        private DateTime nextContextCheck;
        private bool failed, hasHost;

        public MonitorContext()
        {
            tick.Interval = 200;
            tick.Tick += delegate { Update(); };
            tick.Start();
        }

        private static bool HasApp()
        {
            var apps = Process.GetProcessesByName("Antigravity");
            try
            {
                using (var self = Process.GetCurrentProcess())
                    foreach (var app in apps)
                    {
                        try { if (app.SessionId == self.SessionId && !app.HasExited) return true; }
                        catch (InvalidOperationException) { }
                    }
                return false;
            }
            finally { foreach (var app in apps) app.Dispose(); }
        }

        private static LayoutSample Observe(IntPtr host, string previousId, DateTime nextCheck)
        {
            var sample = new LayoutSample { Host = host, Window = Native.Bounds(host) };
            try
            {
                var root = AutomationElement.FromHandle(host);
                bool menuOpen;
                sample.Anchor = Composer.Find(root, out menuOpen, out sample.ConversationId);
                if (menuOpen) sample.Usage = NativeUsage.Read(root);
                if (!sample.Anchor.IsEmpty && sample.ConversationId != null &&
                    (sample.ConversationId != previousId || DateTime.UtcNow >= nextCheck))
                {
                    sample.ContextChecked = true;
                    sample.Context = ContextReader.Read(sample.ConversationId);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            return sample;
        }

        private void Update()
        {
            var now = DateTime.UtcNow;
            var foreground = Native.GetForegroundWindow();
            if (foreground != lastForeground)
            {
                lastForeground = foreground;
                activeHost = Native.IsAntigravity(foreground) ? foreground : IntPtr.Zero;
                nextLayout = DateTime.MinValue;
                anchor = Rectangle.Empty;
                bar.Hide();
            }
            if (now >= nextHostCheck)
            {
                hasHost = activeHost != IntPtr.Zero || HasApp();
                nextHostCheck = now.AddSeconds(2);
                if (!hasHost && now >= startupGrace) { ExitThread(); return; }
            }
            if (quotaRead != null && quotaRead.IsCompleted)
            {
                var value = quotaRead.Status == TaskStatus.RanToCompletion ? quotaRead.Result : null;
                if (value != null) latestCli = value;
                if (requestStarted > lastMenuReading)
                {
                    if (value != null) current = value;
                    failed = value == null;
                }
                else if (value != null && current != null)
                    current = MergeMenu(current, value, null);
                quotaRead = null;
            }
            if ((hasHost || activeHost != IntPtr.Zero) && quotaRead == null && now >= nextRefresh)
            {
                nextRefresh = now.AddMinutes(1);
                requestStarted = now;
                quotaRead = Task.Run((Func<QuotaSnapshot>)CliQuota.Read);
            }
            Rectangle window = activeHost == IntPtr.Zero ? Rectangle.Empty : Native.Bounds(activeHost);
            if (window != lastWindow) { anchor = Rectangle.Empty; nextLayout = DateTime.MinValue; lastWindow = window; }
            if (layoutRead != null && layoutRead.IsCompleted)
            {
                var sample = layoutRead.Status == TaskStatus.RanToCompletion ? layoutRead.Result : null;
                if (sample != null && sample.Host == activeHost && sample.Window == window)
                {
                    anchor = sample.Anchor;
                    if (!anchor.IsEmpty)
                    {
                        if (conversationId != sample.ConversationId) context = null;
                        conversationId = sample.ConversationId;
                        if (sample.ContextChecked)
                        {
                            context = sample.Context;
                            nextContextCheck = now.AddSeconds(3);
                        }
                    }
                    if (sample.Usage != null)
                    {
                        lastMenuReading = sample.Usage.SeenUtc;
                        current = MergeMenu(sample.Usage, latestCli, current);
                        failed = false;
                    }
                }
                layoutRead = null;
            }
            if (activeHost == IntPtr.Zero || Native.IsIconic(activeHost)) { bar.Hide(); return; }
            if (layoutRead == null && now >= nextLayout)
            {
                IntPtr host = activeHost;
                string previousId = conversationId;
                DateTime nextCheck = nextContextCheck;
                layoutRead = Task.Run(() => Observe(host, previousId, nextCheck));
                nextLayout = now.AddMilliseconds(300);
            }
            if (anchor.IsEmpty) bar.Hide();
            else bar.Present(activeHost, anchor, current, failed, quotaRead != null, context, conversationId);
        }

        // The menu supplies the percentages on screen. The CLI supplies precise reset
        // timestamps, including some deadlines the compact menu does not display.
        internal static QuotaSnapshot MergeMenu(QuotaSnapshot menu, QuotaSnapshot cli, QuotaSnapshot previous)
        {
            var now = menu.SeenUtc;
            if (cli != null && now - cli.SeenUtc > TimeSpan.FromMinutes(3)) cli = null;
            var merged = new QuotaSnapshot { SeenUtc = now };
            var baseline = cli ?? previous;
            // Chromium may remove several sections while the menu is closing. Keep
            // recently seen windows in their original order without renewing their age.
            if (baseline != null)
            {
                foreach (var old in baseline.Groups)
                {
                    var row = menu.FindGroup(old.Name);
                    var exact = cli == null ? null : old;
                    var prior = previous == null ? null : previous.FindGroup(old.Name);
                    var group = new QuotaGroup { Name = old.Name };
                    group.Weekly = Combine(row == null ? null : row.Weekly, exact == null ? null : exact.Weekly,
                        prior == null ? null : prior.Weekly, previous, cli, now);
                    group.FiveHour = Combine(row == null ? null : row.FiveHour, exact == null ? null : exact.FiveHour,
                        prior == null ? null : prior.FiveHour, previous, cli, now);
                    if (group.Weekly != null || group.FiveHour != null) merged.Groups.Add(group);
                }
            }
            foreach (var row in menu.Groups)
            {
                if (merged.FindGroup(row.Name) != null) continue;
                if (row.Weekly != null && row.Weekly.SeenUtc == default(DateTime)) row.Weekly.SeenUtc = now;
                if (row.FiveHour != null && row.FiveHour.SeenUtc == default(DateTime)) row.FiveHour.SeenUtc = now;
                merged.Groups.Add(row);
            }
            return merged;
        }

        private static QuotaBucket Combine(QuotaBucket menu, QuotaBucket exact, QuotaBucket prior,
            QuotaSnapshot previous, QuotaSnapshot cli, DateTime now)
        {
            if (menu == null)
            {
                var old = cli != null ? exact : prior;
                if (old == null) return null;
                if (old.SeenUtc == default(DateTime)) old.SeenUtc = (cli ?? previous).SeenUtc;
                return now - old.SeenUtc <= TimeSpan.FromMinutes(3) ? old : null;
            }
            if (menu.SeenUtc == default(DateTime)) menu.SeenUtc = now;
            var reset = menu.ResetUtc;
            if (exact != null && exact.ResetUtc.HasValue && exact.ResetUtc.Value > now &&
                ((!reset.HasValue && exact.Percent == menu.Percent) || SameDeadline(reset, exact.ResetUtc, now)))
            {
                menu.ResetUtc = exact.ResetUtc;
                menu.ResetApproximate = false;
            }
            else if (menu.ResetApproximate && prior != null && prior.ResetApproximate &&
                SameDeadline(reset, prior.ResetUtc, now))
                menu.ResetUtc = prior.ResetUtc;
            return menu;
        }

        private static bool SameDeadline(DateTime? estimate, DateTime? other, DateTime now)
        {
            if (!estimate.HasValue || !other.HasValue || other.Value <= now) return false;
            // View Usage reports days and hours, or hours and minutes, rounded down.
            var tolerance = estimate.Value - now >= TimeSpan.FromDays(1) ? TimeSpan.FromMinutes(31) : TimeSpan.FromSeconds(55);
            return (estimate.Value - other.Value).Duration() <= tolerance;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tick.Dispose();
                bar.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace GeminiQuota
{
    public static class CliQuota
    {
        private static readonly object Sync = new object();
        private static string verifiedPath;
        private static DateTime verifiedWrite;
        private static long verifiedLength;
        private static readonly Uri QuotaService = new Uri("https://daily-cloudcode-pa.googleapis.com/");

        public static QuotaSnapshot Read()
        {
            // A refresh already in progress will deliver its own result.
            if (!System.Threading.Monitor.TryEnter(Sync)) return null;
            try
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] installs = {
                    Path.Combine(local, @"agy\bin\agy.exe"),
                    Path.Combine(local, @"GeminiQuota\tools\agy.exe"),
                    Path.Combine(local, @"Microsoft\WinGet\Packages\Google.AntigravityCLI_Microsoft.Winget.Source_8wekyb3d8bbwe\agy.exe")
                };
                foreach (string path in installs)
                {
                    if (!File.Exists(path)) continue;
                    // Prevent replacement between the fingerprint, signature, version and query checks.
                    using (FileStream locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (!Verify(path, locked.Length, elapsed)) continue;
                        string json = Run(path, "-p \"/usage\" --output-format json", elapsed, null);
                        if (json == null) return null;
                        var snapshot = Parse(json);
                        if (snapshot == null) Diagnose("no readable quota groups in the CLI response");
                        return snapshot;
                    }
                }
            }
            catch (ArgumentException) { Diagnose("invalid CLI response or process arguments"); }
            catch (Exception)
            {
                Diagnose("CLI read failed");
            }
            finally { System.Threading.Monitor.Exit(Sync); }
            return null;
        }

        private static bool Verify(string path, long length, Stopwatch elapsed)
        {
            DateTime write = File.GetLastWriteTimeUtc(path);
            if (path == verifiedPath && write == verifiedWrite && length == verifiedLength) return true;
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            const string check = "-NoLogo -NoProfile -NonInteractive -Command \"" +
                "Import-Module (Join-Path $PSHOME 'Modules\\Microsoft.PowerShell.Security\\Microsoft.PowerShell.Security.psd1'); " +
                "$s=Get-AuthenticodeSignature -LiteralPath $env:GEMINI_QUOTA_SIGNED_FILE; " +
                "if ($s.Status -eq 'Valid' -and $s.SignerCertificate.GetNameInfo('SimpleName',$false) -eq 'Google LLC') { 'verified' }\"";
            string signature = Run(powershell, check, elapsed, path);
            if (signature == null || signature.Trim() != "verified") return false;
            string output = Run(path, "--version", elapsed, null);
            Version version;
            if (output == null || !Regex.IsMatch(output.Trim(), @"^\d+\.\d+\.\d+$") ||
                !Version.TryParse(output.Trim(), out version) || version < new Version(1, 1, 11)) return false;
            verifiedPath = path;
            verifiedWrite = write;
            verifiedLength = length;
            return true;
        }

        private static void Diagnose(string message)
        {
            // A sidecar supervisor may collect stderr. Keep URLs, process output and identity out of it.
            try { Console.Error.WriteLine("GeminiQuota: " + message); }
            catch (IOException) { }
        }

        private static void UseWindowsProxyIfNeeded(ProcessStartInfo info)
        {
            var env = info.EnvironmentVariables;
            if (!string.IsNullOrEmpty(env["HTTPS_PROXY"])) return;
            // Go's HTTPS transport uses HTTPS_PROXY. Preserve any explicit parent settings.
            if (!string.IsNullOrEmpty(env["ALL_PROXY"]))
            {
                env["HTTPS_PROXY"] = env["ALL_PROXY"];
                return;
            }
            if (!string.IsNullOrEmpty(env["HTTP_PROXY"])) return;
            var policy = WebRequest.GetSystemWebProxy();
            if (policy == null || policy.IsBypassed(QuotaService)) return;
            Uri proxy = policy.GetProxy(QuotaService);
            if (proxy != null && proxy.IsAbsoluteUri && proxy != QuotaService)
                env["HTTPS_PROXY"] = proxy.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }

        private static string Run(string exe, string args, Stopwatch elapsed, string signedFile)
        {
            var info = new ProcessStartInfo(exe, args) { WorkingDirectory = Path.GetTempPath() };
            info.EnvironmentVariables["CI"] = "true";
            if (signedFile != null)
            {
                info.EnvironmentVariables["GEMINI_QUOTA_SIGNED_FILE"] = signedFile;
                info.EnvironmentVariables["PSModulePath"] = Path.Combine(Path.GetDirectoryName(exe), "Modules");
            }
            bool usage = signedFile == null && args.StartsWith("-p ", StringComparison.Ordinal);
            if (usage) UseWindowsProxyIfNeeded(info);
            string result = QuotaCommand.Run(info, elapsed);
            if (result == null) Diagnose(usage ? "quota query failed or timed out" : "CLI verification failed or timed out");
            return result;
        }

        private static object Field(object item, string key)
        {
            var fields = item as Dictionary<string, object>;
            object result;
            return fields != null && fields.TryGetValue(key, out result) ? result : null;
        }

        private static QuotaSnapshot Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024) return null;
            object report = new JavaScriptSerializer().DeserializeObject(json);
            object command = Field(report, "command");
            if ((Field(command, "name") as string) != "usage" || (Field(report, "status") as string) != "SUCCESS") return null;
            object[] groups = Field(Field(command, "data"), "groups") as object[];
            if (groups == null) return null;
            var snapshot = new QuotaSnapshot { SeenUtc = DateTime.UtcNow };
            foreach (object group in groups)
            {
                object[] buckets = Field(group, "buckets") as object[];
                string name = Field(group, "name") as string ?? Field(group, "display_name") as string;
                if (buckets == null || string.IsNullOrWhiteSpace(name)) continue;
                var limits = new QuotaGroup { Name = name.Trim() };
                foreach (object bucket in buckets)
                {
                    string window = Field(bucket, "window") as string;
                    if (window == "weekly" && limits.Weekly == null) limits.Weekly = Bucket(bucket);
                    if (window == "5h" && limits.FiveHour == null) limits.FiveHour = Bucket(bucket);
                }
                if (limits.Weekly != null || limits.FiveHour != null) snapshot.Groups.Add(limits);
            }
            return snapshot.Groups.Count > 0 ? snapshot : null;
        }

        private static QuotaBucket Bucket(object data)
        {
            object raw = Field(data, "remaining_fraction");
            if (object.Equals(Field(data, "disabled"), true) || !(raw is int || raw is long || raw is decimal || raw is double)) return null;
            double fraction = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (!(fraction >= 0 && fraction <= 1)) return null;
            var bucket = new QuotaBucket { Percent = (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero) };
            DateTimeOffset reset;
            string stamp = Field(data, "reset_time") as string;
            // The CLI uses the Unix epoch for an unused window without a scheduled reset.
            if (stamp != null && Regex.IsMatch(stamp, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})$") &&
                DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out reset) &&
                reset > new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)) bucket.ResetUtc = reset.UtcDateTime;
            return bucket;
        }
    }
}

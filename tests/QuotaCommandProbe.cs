using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using GeminiQuota;

internal static class QuotaCommandProbe
{
    private const string Prefix = "{\"note\":\"\u4f59\u91cf\",\"tokens\":321,\"childError\":";

    private static ProcessStartInfo Self(string argument)
    {
        string path = Assembly.GetExecutingAssembly().Location;
        return new ProcessStartInfo(path, argument) {
            WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false, CreateNoWindow = true
        };
    }

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            foreach (bool restrict in new[] { false, true })
            {
                string output = QuotaCommand.Run(Self("probe"), Stopwatch.StartNew(), restrict);
                // ERROR_CHILD_PROCESS_BLOCKED is 367. The control must actually start its child.
                string expected = Prefix + (restrict ? "367}" : "0}");
                if (output != expected)
                {
                    Console.Error.WriteLine("restrictChildren={0}: expected {1}, got {2}", restrict, expected, output ?? "<null>");
                    return 1;
                }
            }
            return 0;
        }
        if (args[0] == "leaf") return 0;
        if (args[0] != "probe") return 2;

        int error = 0;
        try
        {
            using (var leaf = Process.Start(Self("leaf")))
            {
                if (!leaf.WaitForExit(5000) || leaf.ExitCode != 0) return 3;
            }
        }
        catch (Win32Exception ex) { error = ex.NativeErrorCode; }
        byte[] bytes = Encoding.UTF8.GetBytes(Prefix + error + "}");
        using (Stream stdout = Console.OpenStandardOutput()) stdout.Write(bytes, 0, bytes.Length);
        return 0;
    }
}

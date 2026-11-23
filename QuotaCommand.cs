using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GeminiQuota
{
    // The official CLI can start console helpers of its own. Give the entire query
    // a separate Win32 desktop; the visible bar stays on the user's input desktop.
    internal static class QuotaCommand
    {
        private const int WaitTimeout = 258;
        private const int TimeoutMilliseconds = 30000;
        private const uint CreateNoWindow = 0x08000000, UnicodeEnvironment = 0x400;
        private const uint UseStdHandles = 0x100, UseShowWindow = 1;

        internal static string Run(ProcessStartInfo info, Stopwatch elapsed)
        {
            if (elapsed.ElapsedMilliseconds >= TimeoutMilliseconds) return null;
            string name = "GeminiQuota-" + Guid.NewGuid().ToString("N");
            using (var desktop = OpenQueryDesktop(name))
            using (var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable))
            using (var childOutput = pipe.ClientSafePipeHandle)
            {
                var inherit = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = 1 };
                // The CLI is noninteractive; stderr can include account information.
                using (var nul = CreateFileW(@"\\.\NUL", 0xC0000000, 3, ref inherit, 3, 0, IntPtr.Zero))
                {
                    if (nul.IsInvalid) throw new Win32Exception();
                    var start = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = name,
                        Flags = UseStdHandles | UseShowWindow, Show = 0, Input = nul.DangerousGetHandle(),
                        Output = childOutput.DangerousGetHandle(), Error = nul.DangerousGetHandle() };
                    var command = new StringBuilder("\"" + info.FileName + "\" " + info.Arguments);
                    ProcessInfo process;
                    if (!CreateProcessW(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                        CreateNoWindow | UnicodeEnvironment, EnvironmentBlock(info.EnvironmentVariables),
                        info.WorkingDirectory, ref start, out process)) throw new Win32Exception();
                    using (var handle = new SafeWaitHandle(process.Process, true))
                    {
                        CloseHandle(process.Thread);
                        pipe.DisposeLocalCopyOfClientHandle();
                        try { return ReadOutput(pipe, handle, elapsed); }
                        finally
                        {
                            if (WaitForSingleObject(handle, 0) == WaitTimeout) TerminateProcess(handle, 1);
                        }
                    }
                }
            }
        }

        private static DesktopHandle OpenQueryDesktop(string name)
        {
            const uint genericAll = 0x10000000;
            var desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, genericAll, IntPtr.Zero);
            if (!desktop.IsInvalid) return desktop;
            var error = new Win32Exception();
            desktop.Dispose();
            throw error;
        }

        private static string EnvironmentBlock(StringDictionary env)
        {
            var keys = new string[env.Count];
            env.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.OrdinalIgnoreCase);
            var block = new StringBuilder();
            foreach (string key in keys) block.Append(key).Append('=').Append(env[key]).Append('\0');
            return block.Append('\0').ToString();
        }

        private static string ReadOutput(AnonymousPipeServerStream pipe, SafeWaitHandle process, Stopwatch elapsed)
        {
            var buffer = new byte[8192];
            bool exited = false;
            using (var output = new MemoryStream())
            {
                while (elapsed.ElapsedMilliseconds < TimeoutMilliseconds)
                {
                    // A helper can retain its parent's pipe after the CLI has exited. Read
                    // only available bytes so an inherited handle cannot strand a reader.
                    uint available;
                    if (!PeekNamedPipe(pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 109) throw new Win32Exception(error); // ERROR_BROKEN_PIPE
                        available = 0;
                    }
                    if (available > 0)
                    {
                        int read = pipe.Read(buffer, 0, (int)Math.Min(available, (uint)buffer.Length));
                        output.Write(buffer, 0, read);
                        continue;
                    }
                    if (exited)
                    {
                        uint code;
                        if (!GetExitCodeProcess(process, out code)) throw new Win32Exception();
                        return code == 0 ? Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length).TrimStart('\uFEFF') : null;
                    }
                    uint result = WaitForSingleObject(process, 20);
                    if (result != 0 && result != WaitTimeout) throw new Win32Exception();
                    exited = result == 0;
                }
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved;
            public string Desktop;
            public IntPtr Title;
            public uint X, Y, Width, Height, Columns, Rows, Fill, Flags;
            public short Show, ReservedBytes;
            public IntPtr Reserved2, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { public IntPtr Process, Thread; public uint Pid, Tid; }
        private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private DesktopHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseDesktop(handle); }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern DesktopHandle CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string exe, StringBuilder args, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags,
            string environment, string directory, ref StartupInfo start, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint length, IntPtr read, out uint available, IntPtr left);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}

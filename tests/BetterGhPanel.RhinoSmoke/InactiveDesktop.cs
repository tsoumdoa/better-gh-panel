using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Rhino's hidden host cannot reliably capture a viewport. Run the few pixel
// fixtures on a private, non-input Windows desktop with a normal Rhino window.
// A kill-on-close job ensures that a coordinator timeout also kills Rhino.
internal static class InactiveDesktop
{
    internal const string EnvironmentName = "PANEL_SMOKE_DESKTOP_NAME";
    private const string DesktopPrefix = "PanelSmoke_";
    private const uint GenericAll = 0x10000000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint KillOnJobClose = 0x00002000;
    private const uint HandleFlagInherit = 1;
    private const int UseStandardHandles = 0x100;

    internal static int Run(string[] arguments)
    {
        string desktopName = DesktopPrefix + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktop(desktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw Error("Could not create an inactive Windows desktop");
        IntPtr job = IntPtr.Zero;
        ProcessInformation process = default;
        bool assigned = false;
        string? previousDesktop = Environment.GetEnvironmentVariable(EnvironmentName);
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw Error("Could not create the fixture process job");
            var limits = new JobExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
                throw Error("Could not configure fixture process cleanup");

            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture executable path is unavailable.");
            var command = new StringBuilder(Quote(executable));
            foreach (string argument in arguments) command.Append(' ').Append(Quote(argument));
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(), Desktop = desktopName, Flags = UseStandardHandles,
                StandardInput = InheritableStandardHandle(-10), StandardOutput = InheritableStandardHandle(-11),
                StandardError = InheritableStandardHandle(-12)
            };
            Environment.SetEnvironmentVariable(EnvironmentName, desktopName);
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended | CreateNoWindow,
                IntPtr.Zero, Environment.CurrentDirectory, ref startup, out process))
                throw Error("Could not start the isolated fixture");
            if (!AssignProcessToJobObject(job, process.Process)) throw Error("Could not attach the fixture to its cleanup job");
            assigned = true;
            Console.WriteLine("Inactive desktop fixture process: " + process.ProcessId);
            Console.Out.Flush();
            if (ResumeThread(process.Thread) == uint.MaxValue) throw Error("Could not start the fixture thread");
            if (WaitForSingleObject(process.Process, uint.MaxValue) != 0) throw Error("Could not wait for the isolated fixture");
            if (!GetExitCodeProcess(process.Process, out uint exitCode)) throw Error("Could not read the fixture exit code");
            return unchecked((int)exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentName, previousDesktop);
            // A suspended process that could not be assigned to the job needs
            // explicit cleanup. Assigned processes die when the job closes.
            if (process.Process != IntPtr.Zero && !assigned)
            {
                TerminateProcess(process.Process, 1);
                WaitForSingleObject(process.Process, 5000);
            }
            if (job != IntPtr.Zero) CloseHandle(job);
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (!CloseDesktop(desktop)) throw Error("Could not close the inactive desktop");
        }
    }

    internal static bool IsCurrent(string name)
    {
        if (!name.StartsWith(DesktopPrefix, StringComparison.Ordinal)) return false;
        IntPtr desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == IntPtr.Zero) return false;
        GetUserObjectInformation(desktop, 2, null, 0, out uint needed);
        if (needed == 0 || needed > 4096) return false;
        var actual = new StringBuilder((int)(needed / sizeof(char)) + 1);
        return GetUserObjectInformation(desktop, 2, actual, needed, out _) &&
            string.Equals(actual.ToString(), name, StringComparison.OrdinalIgnoreCase);
    }

    private static IntPtr InheritableStandardHandle(int identifier)
    {
        IntPtr handle = GetStdHandle(identifier);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1) && !SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
            throw Error("Could not inherit a fixture output handle");
        return handle;
    }

    // Follow the Windows argv quoting rules, including trailing backslashes.
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal short ShowWindow, Reserved2Size;
        internal IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        internal JobBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder? info, uint length, out uint needed);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int identifier);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}

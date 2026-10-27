using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

// A separate non-Rhino process reads the actual HGLOBAL, bypassing managed
// clipboard deserialization and any in-process WPF data-object shortcut.
internal static class RawClipboardProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    internal static int RunChild()
    {
        bool opened = false;
        try
        {
            uint format = RegisterClipboardFormat("Rhino 8 3DM Geometry");
            if (format == 0) throw new InvalidOperationException("Could not register the Rhino clipboard format.");
            for (int attempt = 0; attempt < 20 && !opened; attempt++)
            {
                opened = OpenClipboard(IntPtr.Zero);
                if (!opened) Thread.Sleep(50);
            }
            if (!opened) throw new InvalidOperationException("Could not open the clipboard for the raw probe.");
            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Rhino clipboard data is unavailable.");
            ulong size = GlobalSize(handle).ToUInt64();
            if (size == 0 || size > 16UL * 1024 * 1024) throw new InvalidOperationException("Raw clipboard size is invalid.");
            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero) throw new InvalidOperationException("Could not lock the raw clipboard data.");
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                Console.WriteLine(bytes.Length + ":" + Convert.ToHexString(SHA256.HashData(bytes)));
            }
            finally { GlobalUnlock(handle); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally { if (opened) CloseClipboard(); }
    }

    internal static async Task<string> ReadInChildAsync()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture executable is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("--clipboard-raw-probe");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Raw clipboard probe did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { child.Kill(true); } catch (Exception) { }
            throw new TimeoutException("Raw clipboard probe did not exit.");
        }
        string actual = (await output).Trim(), reason = (await error).Trim();
        if (child.ExitCode != 0) throw new InvalidOperationException("Raw clipboard probe failed: " + reason);
        return actual;
    }
}

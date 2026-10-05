using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jet.BrowserHost;

/// <summary>
/// Preview 停止服務時只結束外層的 <c>dotnet run</c>，子程序會留著占用連接埠。
/// 這裡找出啟動本程序的父程序；父程序結束時通知主機一併停止。
/// </summary>
internal static class ParentProcessWatch
{
    public static Task? TryWatch()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(current.Handle, 0, ref info, Marshal.SizeOf(info), out _) != 0)
            {
                return null;
            }

            var parent = Process.GetProcessById(info.InheritedFromUniqueProcessId.ToInt32());
            // 父程序若早已結束、編號被重用，新程序的啟動時間會晚於本程序；這時不監看。
            if (parent.StartTime > current.StartTime)
            {
                parent.Dispose();
                return null;
            }

            return parent.WaitForExitAsync().ContinueWith(_ => parent.Dispose(), TaskScheduler.Default);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}

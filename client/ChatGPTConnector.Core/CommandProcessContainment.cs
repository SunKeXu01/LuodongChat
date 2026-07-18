using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChatGPTConnector.Core;

internal static class CommandProcessContainment
{
    internal sealed class Handle(IDisposable resource, string level) : IDisposable
    {
        public string Level { get; } = level;
        public void Dispose() => resource.Dispose();
    }

    public static Handle Attach(Process process, bool restrictUi = true)
    {
        if (!OperatingSystem.IsWindows()) return new Handle(NoopDisposable.Instance, "process_tree");

        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建命令进程容器。");
        var limits = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose | NativeMethods.JobObjectLimitActiveProcess,
                ActiveProcessLimit = 64,
            },
        };
        if (!NativeMethods.SetInformationJobObject(
                job, NativeMethods.JobObjectInfoType.ExtendedLimitInformation, ref limits,
                (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "无法配置命令进程容器。");
        }
        if (!NativeMethods.IsProcessInJob(process.Handle, IntPtr.Zero, out var alreadyInJob))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "无法检查命令进程容器状态。");
        }
        if (!alreadyInJob && restrictUi)
        {
            var uiRestrictions = NativeMethods.JobObjectUiLimitAll;
            if (!NativeMethods.SetInformationJobObjectUi(
                    job, NativeMethods.JobObjectInfoType.BasicUiRestrictions, ref uiRestrictions, sizeof(uint)))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "无法配置命令桌面隔离规则。");
            }
        }
        if (!NativeMethods.AssignProcessToJobObject(job, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            try { process.Kill(entireProcessTree: true); } catch { }
            job.Dispose();
            throw new Win32Exception(error, "无法将命令放入受控进程容器，操作已停止。");
        }
        return new Handle(job, alreadyInJob ? "windows_job_nested"
            : restrictUi ? "windows_job_ui_restricted" : "windows_job_managed_application");
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private static class NativeMethods
    {
        internal const uint JobObjectLimitActiveProcess = 0x00000008;
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
        internal const uint JobObjectUiLimitAll = 0x000000FF;

        internal enum JobObjectInfoType { BasicUiRestrictions = 4, ExtendedLimitInformation = 9 }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle job, JobObjectInfoType informationClass,
            ref JobObjectExtendedLimitInformation information, uint informationLength);

        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObjectUi(
            SafeFileHandle job, JobObjectInfoType informationClass,
            ref uint information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    }
}

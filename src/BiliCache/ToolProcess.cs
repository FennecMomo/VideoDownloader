using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BiliCache;

internal sealed class DownloadProcessException(int exitCode, string diagnostic)
    : InvalidOperationException(DownloadService.FriendlyError(diagnostic))
{
    public int ExitCode { get; } = exitCode;
    public string Diagnostic { get; } = diagnostic;
}

internal static class ToolProcess
{
    private static readonly TimeSpan PipeDrainTimeout = TimeSpan.FromSeconds(2);

    public static Task RunAsync(string executable, IEnumerable<string> arguments, Action<string> onLine, CancellationToken cancellationToken) =>
        Task.Run(() => RunCoreAsync(executable, arguments, onLine, cancellationToken), cancellationToken);

    private static async Task RunCoreAsync(string executable, IEnumerable<string> arguments, Action<string> onLine, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using var job = new ProcessJob();
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("无法启动缓存组件。");

        using var stdout = process.StandardOutput;
        using var stderr = process.StandardError;
        using var readCancellation = new CancellationTokenSource();
        var errors = new Queue<string>();
        bool attached = false;
        Task output = Task.CompletedTask;
        Task error = Task.CompletedTask;
        try
        {
            attached = job.Attach(process);
            output = ReadLinesAsync(stdout, onLine, readCancellation.Token);
            error = ReadLinesAsync(stderr, line =>
            {
                if (errors.Count >= 12) errors.Dequeue();
                errors.Enqueue(line);
            }, readCancellation.Token);

            Task exit = process.WaitForExitAsync(cancellationToken);
            var pending = new List<Task> { exit, output, error };
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
                if (completed != exit) continue;

                // A descendant can keep an inherited pipe open after the tool has exited.
                if (attached) await job.StopAsync().ConfigureAwait(false);
                await Task.WhenAll(pending).WaitAsync(PipeDrainTimeout, cancellationToken).ConfigureAwait(false);
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new DownloadProcessException(process.ExitCode, string.Join(Environment.NewLine, errors));
        }
        finally
        {
            try
            {
                if (attached) await job.StopAsync().ConfigureAwait(false);
                else if (!process.HasExited)
                {
                    // A running tool that failed job assignment must still be stopped.
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    catch (Win32Exception) when (process.HasExited) { }
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                readCancellation.Cancel();
                Task readers = ObserveReaderAsync(Task.WhenAll(output, error));
                if (!readers.IsCompleted)
                    await Task.WhenAny(readers, Task.Delay(PipeDrainTimeout)).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onLine(line);
        }
    }

    private static async Task ObserveReaderAsync(Task reader)
    {
        // A reader may still be unwinding after the other pipe or process has failed.
        try { await reader.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private sealed class ProcessJob : IDisposable
    {
        private const uint KillOnJobClose = 0x00002000;
        private const int BasicAccountingInformation = 1;
        private const int ExtendedLimitInformation = 9;
        private readonly SafeFileHandle handle;

        public ProcessJob()
        {
            handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

            var limits = new JobLimits { Basic = new BasicLimits { Flags = KillOnJobClose } };
            if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
        }

        public bool Attach(Process process)
        {
            if (AssignProcessToJobObject(handle, process.SafeHandle)) return true;
            int error = Marshal.GetLastWin32Error();
            if (process.HasExited) return false;
            throw new Win32Exception(error, "无法管理缓存组件进程。");
        }

        public async Task StopAsync()
        {
            if (ActiveProcesses() == 0) return;
            if (!TerminateJobObject(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());

            // Retrying while a previous tool can still write into the task directory is unsafe.
            while (ActiveProcesses() != 0) await Task.Delay(25).ConfigureAwait(false);
        }

        private uint ActiveProcesses()
        {
            if (!QueryInformationJobObject(handle, BasicAccountingInformation, out JobAccounting accounting,
                    (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return accounting.ActiveProcesses;
        }

        public void Dispose() => handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTime, PerJobUserTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations;
            public ulong ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobAccounting
        {
            public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits information, uint length);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobAccounting information, uint length, IntPtr returnLength);
    }
}

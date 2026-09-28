using System.Diagnostics;
using System.IO;
using System.Text;
using AV1Studio.Models;
using AV1Studio.Native;
using AV1Studio.Util;

namespace AV1Studio.Services;

/// <summary>
/// Runs an external tool as an independent child process:
/// * no shell, arguments quoted individually (safe escaping, no injection)
/// * started suspended with its priority class, put into a Job Object, then resumed: the whole process tree
///   (ab-av1 and every FFmpeg / encoder process it starts) runs with the chosen priority and affinity from its
///   first instruction, and is killed if AV1 Studio exits
/// * stdout/stderr drained continuously on background threads, so a tool never waits on a full pipe
/// </summary>
public sealed class ChildProcess : IDisposable
{
    private readonly Process _process;
    private readonly JobObject? _job;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stdoutDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stderrDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CommandLineText { get; }
    public JobObject? Job => _job;
    public bool WasKilled { get; private set; }

    private readonly Native.SuspendedProcess? _native;

    private ChildProcess(Process p, JobObject? job, string cmd, Native.SuspendedProcess? native = null)
    {
        _process = p;
        _job = job;
        _native = native;
        CommandLineText = cmd;
    }

    private static bool _nativeUnavailable;

    /// <summary>Preferred start: suspended → job → resume (see class summary).</summary>
    private static ChildProcess? TryStartSuspended(string exe, IReadOnlyList<string> args, Action<string>? onStdout,
        Action<string>? onStderr, string workingDirectory, IDictionary<string, string>? environment, ResourcePlan? resources, JobObject? job)
    {
        if (_nativeUnavailable || job is null) return null;
        Native.SuspendedProcess sp;
        try
        {
            sp = Native.SuspendedProcess.Start(exe, CommandLine.Format(exe, args), workingDirectory, environment,
                resources?.Priority, h => job.Assign(h));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 or 5 or 193 or 267)
        {
            throw; // file/folder not found, access denied, not a valid program: a real error, not a platform limitation
        }
        catch (Exception ex)
        {
            _nativeUnavailable = true;
            Log.Warn($"Starting processes suspended is not available ({ex.Message}); using the standard method.");
            return null;
        }

        var cp = new ChildProcess(sp.Process, job, CommandLine.Format(exe, args), sp);
        sp.Process.Exited += (_, _) => cp.OnExited();
        if (sp.Process.HasExited) cp.OnExited();
        Pump(sp.StandardOutput, onStdout, cp._stdoutDone);
        Pump(sp.StandardError, onStderr, cp._stderrDone);
        return cp;
    }

    /// <summary>Reads one pipe line by line on a dedicated background thread until the tool closes it.</summary>
    private static void Pump(Stream stream, Action<string>? onLine, TaskCompletionSource done)
    {
        var t = new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 16384);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    try { onLine?.Invoke(line); }
                    catch (Exception ex) { Log.FileOnly($"Output handler error: {ex.Message}"); }
                }
            }
            catch (IOException) { /* pipe closed */ }
            catch (ObjectDisposedException) { }
            finally { done.TrySetResult(); }
        }) { IsBackground = true, Name = "tool output" };
        t.Start();
    }

    private void OnExited()
    {
        int code;
        try { code = _process.ExitCode; } catch { code = -1; }
        _exited.TrySetResult(code);
    }

    public static ChildProcess Start(
        string exe,
        IReadOnlyList<string> args,
        Action<string>? onStdout,
        Action<string>? onStderr,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        ResourcePlan? resources = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? AppPaths.Temp,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        ProcessPriorityClass? prioClass = resources?.Priority;

        JobObject? job = null;
        try { job = new JobObject(prioClass, resources?.AffinityMask); }
        catch (Exception ex) { Log.Warn($"Job object unavailable ({ex.Message}); child tree cleanup may be incomplete"); }

        var started = TryStartSuspended(exe, args, onStdout, onStderr, psi.WorkingDirectory, environment, resources, job);
        if (started != null) return started;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var cp = new ChildProcess(p, job, CommandLine.Format(exe, args));

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { cp._stdoutDone.TrySetResult(); return; }
            onStdout?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { cp._stderrDone.TrySetResult(); return; }
            onStderr?.Invoke(e.Data);
        };
        p.Exited += (_, _) =>
        {
            int code;
            try { code = p.ExitCode; } catch { code = -1; }
            cp._exited.TrySetResult(code);
        };

        p.Start();
        try { job?.Assign(p); } catch (Exception ex) { Log.Warn($"Could not assign process to job: {ex.Message}"); }
        if (prioClass is ProcessPriorityClass pc)
        {
            try { p.PriorityClass = pc; } catch { /* already exited */ }
        }
        if (resources?.AffinityMask is ulong mask)
        {
            try { p.ProcessorAffinity = (IntPtr)(long)mask; } catch { /* job limit already applies */ }
        }
        p.StandardInput.Close(); // tools must never wait for console input
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return cp;
    }

    /// <summary>Wait for exit and for all output to be drained. Cancelling kills the process tree.</summary>
    public async Task<int> WaitAsync(CancellationToken ct = default)
    {
        using (ct.Register(Kill))
        {
            int code = await _exited.Task.ConfigureAwait(false);
            // Output events can arrive slightly after Exited; wait (bounded) for EOF on both pipes.
            await Task.WhenAny(Task.WhenAll(_stdoutDone.Task, _stderrDone.Task), Task.Delay(5000)).ConfigureAwait(false);
            return code;
        }
    }

    public void Kill()
    {
        WasKilled = true;
        try { _job?.Terminate(); } catch { }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        _job?.Dispose();
        if (_native != null) _native.Dispose();
        else _process.Dispose();
    }

    /// <summary>Convenience: run to completion and capture all output.</summary>
    public static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(
        string exe, IReadOnlyList<string> args, CancellationToken ct = default, TimeSpan? timeout = null,
        IDictionary<string, string>? env = null)
    {
        var so = new StringBuilder();
        var se = new StringBuilder();
        using var cp = Start(exe, args, l => { lock (so) so.AppendLine(l); }, l => { lock (se) se.AppendLine(l); },
            environment: env);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        int code = await cp.WaitAsync(cts.Token).ConfigureAwait(false);
        if (cp.WasKilled && !ct.IsCancellationRequested) throw new TimeoutException($"{Path.GetFileName(exe)} timed out");
        ct.ThrowIfCancellationRequested();
        lock (so) lock (se) return (code, so.ToString(), se.ToString());
    }
}

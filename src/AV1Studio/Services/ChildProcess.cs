using System.Diagnostics;
using System.IO;
using System.Text;
using AV1Studio.Models;
using AV1Studio.Native;
using AV1Studio.Util;

namespace AV1Studio.Services;

/// <summary>
/// Runs an external tool as an independent child process:
/// * no shell (UseShellExecute=false), arguments passed via ArgumentList (safe escaping, no injection)
/// * stdout/stderr consumed line-by-line asynchronously (event driven, no polling)
/// * whole process tree bound to a Job Object (priority, kill-on-close, CPU/RAM accounting)
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

    private ChildProcess(Process p, JobObject? job, string cmd)
    {
        _process = p;
        _job = job;
        CommandLineText = cmd;
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
        _process.Dispose();
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

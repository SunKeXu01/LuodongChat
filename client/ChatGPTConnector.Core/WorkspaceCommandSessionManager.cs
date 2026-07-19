using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ChatGPTConnector.Core;

internal sealed record WorkspaceCommandSessionResult(
    int SessionId,
    string Status,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool OutputTruncated,
    string ProcessContainment,
    bool TimedOut = false);

/// <summary>
/// Owns command processes for one AI response. The shape deliberately follows Codex CLI's
/// unified exec model: short commands complete inline, while longer commands yield a session
/// id that can be polled, written to, or terminated without starting a second shell.
/// </summary>
internal sealed class WorkspaceCommandSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, CommandSession> _sessions = new();
    private int _nextSessionId;
    private int _disposed;

    public async Task<WorkspaceCommandSessionResult> StartAsync(
        ProcessStartInfo startInfo,
        int timeoutSeconds,
        int yieldTimeMs,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextSessionId);
        var session = CommandSession.Start(id, startInfo, timeoutSeconds);
        if (!_sessions.TryAdd(id, session))
        {
            await session.DisposeAsync();
            throw new InvalidOperationException("无法登记命令会话。");
        }

        var result = await session.WaitAndReadAsync(yieldTimeMs, cancellationToken);
        if (result.Status != "running") await RemoveAndDisposeAsync(id, session);
        return result;
    }

    public async Task<WorkspaceCommandSessionResult> WriteAsync(
        int sessionId,
        string chars,
        int yieldTimeMs,
        bool terminate,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException("命令会话不存在、已经结束，或不属于本次对话。");

        if (terminate) session.Terminate();
        else if (chars.Length > 0) await session.WriteAsync(chars, cancellationToken);

        var result = await session.WaitAndReadAsync(yieldTimeMs, cancellationToken);
        if (result.Status != "running") await RemoveAndDisposeAsync(sessionId, session);
        return result;
    }

    private async Task RemoveAndDisposeAsync(int id, CommandSession session)
    {
        _sessions.TryRemove(new KeyValuePair<int, CommandSession>(id, session));
        await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var sessions = _sessions.ToArray();
        _sessions.Clear();
        foreach (var pair in sessions) pair.Value.Terminate();
        foreach (var pair in sessions) await pair.Value.DisposeAsync();
    }

    private sealed class CommandSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CommandProcessContainment.Handle _containment;
        private readonly BoundedOutputBuffer _stdout = new();
        private readonly BoundedOutputBuffer _stderr = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _completion;
        private int _timedOut;
        private int _terminated;
        private int _disposed;

        private CommandSession(int id, Process process, CommandProcessContainment.Handle containment, int timeoutSeconds)
        {
            Id = id;
            _process = process;
            _containment = containment;
            _completion = CompleteAsync(timeoutSeconds);
        }

        public int Id { get; }

        public static CommandSession Start(int id, ProcessStartInfo startInfo, int timeoutSeconds)
        {
            startInfo.RedirectStandardInput = true;
            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动命令进程。");
            }

            try
            {
                var containment = CommandProcessContainment.Attach(process);
                return new CommandSession(id, process, containment, timeoutSeconds);
            }
            catch
            {
                TryKillProcessTree(process);
                process.Dispose();
                throw;
            }
        }

        public async Task WriteAsync(string chars, CancellationToken cancellationToken)
        {
            if (_process.HasExited) throw new InvalidOperationException("命令已经结束，无法继续写入输入。");
            await _process.StandardInput.WriteAsync(chars.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }

        public void Terminate()
        {
            Interlocked.Exchange(ref _terminated, 1);
            TryKillProcessTree(_process);
        }

        public async Task<WorkspaceCommandSessionResult> WaitAndReadAsync(int yieldTimeMs, CancellationToken cancellationToken)
        {
            var delay = Task.Delay(yieldTimeMs, cancellationToken);
            await Task.WhenAny(_completion, delay);
            cancellationToken.ThrowIfCancellationRequested();
            if (_completion.IsCompleted) await _completion;

            var stdout = _stdout.Drain();
            var stderr = _stderr.Drain();
            var running = !_completion.IsCompleted;
            return new WorkspaceCommandSessionResult(
                Id,
                running ? "running" : Volatile.Read(ref _timedOut) != 0 ? "timed_out"
                    : Volatile.Read(ref _terminated) != 0 ? "terminated" : "completed",
                running ? null : SafeExitCode(_process),
                stdout.Text,
                stderr.Text,
                stdout.Truncated || stderr.Truncated,
                _containment.Level,
                Volatile.Read(ref _timedOut) != 0);
        }

        private async Task CompleteAsync(int timeoutSeconds)
        {
            var stdoutPump = PumpAsync(_process.StandardOutput, _stdout, _lifetime.Token);
            var stderrPump = PumpAsync(_process.StandardError, _stderr, _lifetime.Token);
            var exited = _process.WaitForExitAsync(_lifetime.Token);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), timeoutCancellation.Token);
            try
            {
                if (await Task.WhenAny(exited, timeout) == timeout)
                {
                    Interlocked.Exchange(ref _timedOut, 1);
                    TryKillProcessTree(_process);
                }
                else timeoutCancellation.Cancel();
                await _process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdoutPump, stderrPump);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
        }

        private static async Task PumpAsync(StreamReader reader, BoundedOutputBuffer output, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) return;
                output.Append(buffer.AsSpan(0, read));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            TryKillProcessTree(_process);
            try { await _completion.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
            _lifetime.Cancel();
            _lifetime.Dispose();
            try { _process.StandardInput.Dispose(); } catch { }
            _containment.Dispose();
            _process.Dispose();
        }

        private static int? SafeExitCode(Process process)
        {
            try { return process.HasExited ? process.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    private sealed class BoundedOutputBuffer
    {
        private const int Capacity = 96_000;
        private readonly Lock _gate = new();
        private readonly StringBuilder _pending = new();
        private bool _truncated;

        public void Append(ReadOnlySpan<char> value)
        {
            lock (_gate)
            {
                if (value.Length >= Capacity)
                {
                    _pending.Clear();
                    _pending.Append(value[^Capacity..]);
                    _truncated = true;
                    return;
                }
                var overflow = _pending.Length + value.Length - Capacity;
                if (overflow > 0)
                {
                    _pending.Remove(0, overflow);
                    _truncated = true;
                }
                _pending.Append(value);
            }
        }

        public (string Text, bool Truncated) Drain()
        {
            lock (_gate)
            {
                var result = (_pending.ToString(), _truncated);
                _pending.Clear();
                _truncated = false;
                return result;
            }
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

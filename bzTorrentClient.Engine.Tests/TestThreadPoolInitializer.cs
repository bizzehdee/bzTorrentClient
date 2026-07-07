using System.Runtime.CompilerServices;

namespace bzTorrentClient.Engine.Tests;

internal static class TestThreadPoolInitializer
{
    /// <summary>
    /// These tests mix blocking work - real-torrent piece verification (parallel SHA-1 hashing)
    /// and, in the integration tests, real peer/socket loops that park on Thread.Sleep - with
    /// async continuations and real async socket I/O. The thread pool only grows from its small
    /// default a couple of threads at a time, so on a low-core CI the blocking work starves the
    /// continuations and the whole run crawls (and can look like a hang).
    ///
    /// Crucially the completion-port (I/O) minimum is bumped too, not just the worker minimum:
    /// on Windows, async socket completions run on I/O-pool threads, and if those are starved
    /// the loopback receives in the integration tests never complete, so the peer loops wait
    /// forever - a freeze seen only on Windows (Linux has no separate I/O pool). Give both a
    /// high floor up front so nothing starves, regardless of core count or OS.
    /// </summary>
    [ModuleInitializer]
    internal static void EnsureAmpleThreadPool()
    {
        ThreadPool.GetMinThreads(out var minWorker, out var minCompletionPort);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 32), Math.Max(minCompletionPort, 32));
    }
}

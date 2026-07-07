using System.Runtime.CompilerServices;

namespace bzTorrentClient.Engine.Tests;

internal static class TestThreadPoolInitializer
{
    /// <summary>
    /// These tests mix blocking work - real-torrent piece verification (parallel SHA-1 hashing)
    /// and, in the integration tests, real peer/socket loops that park on Thread.Sleep - with
    /// async continuations. The thread pool only grows from its small default a couple of
    /// threads at a time, so on a low-core CI the blocking work starves the continuations and
    /// the whole run crawls (and can look like a hang). Give the pool a high floor up front so
    /// both have room, regardless of core count.
    /// </summary>
    [ModuleInitializer]
    internal static void EnsureAmpleThreadPool()
    {
        ThreadPool.GetMinThreads(out var minWorker, out var minCompletionPort);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 32), minCompletionPort);
    }
}

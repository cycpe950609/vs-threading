﻿namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
#if DESKTOP || NETCOREAPP2_0
    using System.Configuration;
#endif
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    internal static class TestUtilities
    {
        /// <summary>
        /// A value indicating whether the library is operating in .NET 4.5 mode.
        /// </summary>
#if NET45
        internal static readonly bool IsNet45Mode = ConfigurationManager.AppSettings["Microsoft.VisualStudio.Threading.NET45Mode"] == "true";
#else
        internal static readonly bool IsNet45Mode = false;
#endif

        internal static Task SetAsync(this TaskCompletionSource<object> tcs)
        {
            return Task.Run(() => tcs.TrySetResult(null));
        }

        /// <summary>
        /// Runs an asynchronous task synchronously, using just the current thread to execute continuations.
        /// </summary>
        internal static void Run(Func<Task> func)
        {
            if (func == null)
            {
                throw new ArgumentNullException(nameof(func));
            }

            var prevCtx = SynchronizationContext.Current;
            try
            {
                var syncCtx = SingleThreadedSynchronizationContext.New();
                SynchronizationContext.SetSynchronizationContext(syncCtx);

                var t = func();
                if (t == null)
                {
                    throw new InvalidOperationException();
                }

                var frame = SingleThreadedSynchronizationContext.NewFrame();
                t.ContinueWith(_ => { frame.Continue = false; }, TaskScheduler.Default);
                SingleThreadedSynchronizationContext.PushFrame(syncCtx, frame);

                t.GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
        }

        /// <summary>
        /// Executes the specified function on multiple threads simultaneously.
        /// </summary>
        /// <typeparam name="T">The type of the value returned by the specified function.</typeparam>
        /// <param name="action">The function to invoke concurrently.</param>
        /// <param name="concurrency">The level of concurrency.</param>
        internal static T[] ConcurrencyTest<T>(Func<T> action, int concurrency = -1)
        {
            Requires.NotNull(action, nameof(action));
            if (concurrency == -1)
            {
                concurrency = Environment.ProcessorCount;
            }

            Skip.If(Environment.ProcessorCount < concurrency, $"The test machine does not have enough CPU cores to exercise a concurrency level of {concurrency}");

            // We use a barrier to guarantee that all threads are fully ready to
            // execute the provided function at precisely the same time.
            // The barrier will unblock all of them together.
            using (var barrier = new Barrier(concurrency))
            {
                var tasks = new Task<T>[concurrency];
                for (int i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = Task.Run(delegate
                    {
                        barrier.SignalAndWait();
                        return action();
                    });
                }

                Task.WaitAll(tasks);
                return tasks.Select(t => t.Result).ToArray();
            }
        }

        internal static DebugAssertionRevert DisableAssertionDialog()
        {
#if DESKTOP
            var listener = Debug.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
            if (listener != null)
            {
                listener.AssertUiEnabled = false;
            }
#endif

            return default(DebugAssertionRevert);
        }

        internal static void CompleteSynchronously(this JoinableTaskFactory factory, JoinableTaskCollection collection, Task task)
        {
            Requires.NotNull(factory, nameof(factory));
            Requires.NotNull(collection, nameof(collection));
            Requires.NotNull(task, nameof(task));

            factory.Run(async delegate
            {
                using (collection.Join())
                {
                    await task;
                }
            });
        }

        /// <summary>
        /// Forces an awaitable to yield, setting signals after the continuation has been pended and when the continuation has begun execution.
        /// </summary>
        /// <param name="baseAwaiter">The awaiter to extend.</param>
        /// <param name="yieldingSignal">The signal to set after the continuation has been pended.</param>
        /// <param name="resumingSignal">The signal to set when the continuation has been invoked.</param>
        /// <returns>A new awaitable.</returns>
        internal static YieldAndNotifyAwaitable YieldAndNotify(this INotifyCompletion baseAwaiter, AsyncManualResetEvent yieldingSignal = null, AsyncManualResetEvent resumingSignal = null)
        {
            Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

            return new YieldAndNotifyAwaitable(baseAwaiter, yieldingSignal, resumingSignal);
        }

        /// <summary>
        /// Flood the threadpool with requests that will just block the threads
        /// until the returned value is disposed of.
        /// </summary>
        /// <returns>A value to dispose of to unblock the threadpool.</returns>
        /// <remarks>
        /// This can provide a unique technique for influencing execution order
        /// of synchronous code vs. async code.
        /// </remarks>
        internal static IDisposable StarveThreadpool()
        {
#if DESKTOP || NETCOREAPP2_0
            ThreadPool.GetMaxThreads(out int workerThreads, out int completionPortThreads);
#else
            int workerThreads = 1023;
#endif
            var disposalTokenSource = new CancellationTokenSource();
            var unblockThreadpool = new ManualResetEventSlim();
            for (int i = 0; i < workerThreads; i++)
            {
                Task.Run(
                    () => unblockThreadpool.Wait(disposalTokenSource.Token),
                    disposalTokenSource.Token);
            }

            return new DisposalAction(disposalTokenSource.Cancel);
        }

        internal struct YieldAndNotifyAwaitable
        {
            private readonly INotifyCompletion baseAwaiter;
            private readonly AsyncManualResetEvent yieldingSignal;
            private readonly AsyncManualResetEvent resumingSignal;

            internal YieldAndNotifyAwaitable(INotifyCompletion baseAwaiter, AsyncManualResetEvent yieldingSignal, AsyncManualResetEvent resumingSignal)
            {
                Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

                this.baseAwaiter = baseAwaiter;
                this.yieldingSignal = yieldingSignal;
                this.resumingSignal = resumingSignal;
            }

            public YieldAndNotifyAwaiter GetAwaiter()
            {
                return new YieldAndNotifyAwaiter(this.baseAwaiter, this.yieldingSignal, this.resumingSignal);
            }
        }

        internal struct YieldAndNotifyAwaiter : INotifyCompletion
        {
            private readonly INotifyCompletion baseAwaiter;
            private readonly AsyncManualResetEvent yieldingSignal;
            private readonly AsyncManualResetEvent resumingSignal;

            internal YieldAndNotifyAwaiter(INotifyCompletion baseAwaiter, AsyncManualResetEvent yieldingSignal, AsyncManualResetEvent resumingSignal)
            {
                Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

                this.baseAwaiter = baseAwaiter;
                this.yieldingSignal = yieldingSignal;
                this.resumingSignal = resumingSignal;
            }

            public bool IsCompleted
            {
                get { return false; }
            }

            public void OnCompleted(Action continuation)
            {
                var that = this;
                this.baseAwaiter.OnCompleted(delegate
                {
                    if (that.resumingSignal != null)
                    {
                        that.resumingSignal.Set();
                    }

                    continuation();
                });
                if (this.yieldingSignal != null)
                {
                    this.yieldingSignal.Set();
                }
            }

            public void GetResult()
            {
            }
        }

        internal struct DebugAssertionRevert : IDisposable
        {
            public void Dispose()
            {
#if DESKTOP
                var listener = Debug.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
                if (listener != null)
                {
                    listener.AssertUiEnabled = true;
                }
#endif
            }
        }

        private class DisposalAction : IDisposable
        {
            private readonly Action disposeAction;

            internal DisposalAction(Action disposeAction)
            {
                this.disposeAction = disposeAction;
            }

            public void Dispose() => this.disposeAction();
        }
    }
}

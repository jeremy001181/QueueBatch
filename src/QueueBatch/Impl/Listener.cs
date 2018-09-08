﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Listeners;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;
using QueueBatch.Impl.Queues;

namespace QueueBatch.Impl
{
    class Listener : IListener
    {
        readonly RandomizedExponentialBackoffStrategy backoff;
        readonly ITriggeredFunctionExecutor executor;
        readonly int maxRetries;
        readonly QueueFunctionLogic queue;
        readonly TimeSpan visibilityTimeout;
        readonly ILoggerFactory loggerFactory;
        readonly Task<IRetrievedMessages>[] gets;

        Task runner;
        CancellationTokenSource tokenSource;
        static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(10.0);

        public Listener(ITriggeredFunctionExecutor executor, QueueFunctionLogic queue,
            TimeSpan maxBackoff, int maxRetries, TimeSpan visibilityTimeout, int parallelGets, ILoggerFactory loggerFactory)
        {
            this.executor = executor;
            this.queue = queue;
            this.maxRetries = maxRetries;
            this.visibilityTimeout = visibilityTimeout;
            this.loggerFactory = loggerFactory;
            gets = new Task<IRetrievedMessages>[parallelGets];
            backoff = new RandomizedExponentialBackoffStrategy(TimeSpan.FromMilliseconds(100), maxBackoff);
        }

        public Task StartAsync(CancellationToken ct)
        {
            tokenSource = new CancellationTokenSource();
            runner = Task.Run(Process, ct);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Cancel();
            return runner;
        }

        public void Dispose()
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public void Cancel()
        {
            tokenSource.Cancel();
        }

        async Task Process()
        {
            var logger = loggerFactory.CreateLogger(LogCategories.CreateTriggerCategory("BatchedQueue"));

            var ct = tokenSource.Token;
            while (tokenSource.IsCancellationRequested == false)
            {
                try
                {
                    for (var i = 0; i < gets.Length; i++)
                    {
                        gets[i] = queue.GetMessages(VisibilityTimeout, ct);
                    }

                    var results = await Task.WhenAll(gets).ConfigureAwait(false);

                    using (new ComposedDisposable(results))
                    {
                        var messages = results.SelectMany(msgs => msgs.Messages).ToArray();

                        if (messages.Length > 0)
                        {
                            var batch = new MessageBatch(messages);
                            var data = new TriggeredFunctionData {TriggerValue = batch};
                            var result = await executor.TryExecuteAsync(data, CancellationToken.None).ConfigureAwait(false);

                            try
                            {
                                if (result.Succeeded)
                                {
                                    await batch.Complete(queue, maxRetries, visibilityTimeout, CancellationToken.None);
                                }
                                else
                                {
                                    await batch.RetryAll(queue, maxRetries, visibilityTimeout, CancellationToken.None);
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogError("Exception occured when completing batch", ex);
                                await Delay(false, ct).ConfigureAwait(false);
                            }

                            await Delay(result.Succeeded, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            logger.LogDebug("No messages received");
                            await Delay(false, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex.InnerException != null &&
                                           ex.InnerException.GetType() == typeof(TaskCanceledException))
                {
                }
                catch (TaskCanceledException)
                {
                }
            }
        }

        Task Delay(bool executionSucceeded, CancellationToken ct) => Task.Delay(backoff.GetNextDelay(executionSucceeded), ct);
    }
}
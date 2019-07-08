﻿using System;
using System.Linq;
using System.Threading;
using System.Transactions;
using Hangfire.SqlServer;
using Hangfire.Storage;
using System.Data;
using Microsoft.Azure.ServiceBus;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus.Core;
using System.Text;
using System.Data.Common;
using Hangfire.Logging;

namespace Hangfire.Azure.ServiceBusQueue
{
    internal class ServiceBusQueueJobQueue : IPersistentJobQueue
    {
        private static readonly TimeSpan MinSyncReceiveTimeout = TimeSpan.FromTicks(1);

        private readonly ServiceBusManager _manager;
        private readonly ServiceBusQueueOptions _options;
        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        public ServiceBusQueueJobQueue(ServiceBusManager manager, ServiceBusQueueOptions options)
        {
            _manager = manager ?? throw new ArgumentNullException("manager");
            _options = options ?? throw new ArgumentNullException("options");
        }

        public IFetchedJob Dequeue(string[] queues, CancellationToken cancellationToken)
        {
            var job = Task.Run(async () =>
            {
                var queueIndex = 0;

                var clients = await Task.WhenAll(queues.Select(queue => _manager.GetClientAsync(queue)));

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (queryclient, messageReceiver) = clients[queueIndex];

                    try
                    {
                        var isLastQueue = queueIndex == queues.Length - 1;

                        var message = await (isLastQueue
                            ? messageReceiver.ReceiveAsync(_manager.Options.LoopReceiveTimeout) // Last queue
                            : messageReceiver.ReceiveAsync(MinSyncReceiveTimeout));

                        if (message != null)
                        {
                            Logger.InfoFormat($"Dequeue one message from queue {queryclient.QueueName} with size {message.Size}");
                            return new ServiceBusQueueFetchedJob(messageReceiver, message, _options.LockRenewalDelay);
                        }
                    }
                    catch (ServiceBusTimeoutException)
                    {
                    }
                    catch (MessagingEntityNotFoundException ex)
                    {
                        var errorMessage = string.Format(
                            "Queue {0} could not be found. Either create the queue manually, " +
                            "or grant the Manage permission and set ServiceBusQueueOptions.CheckAndCreateQueues to true",
                            queryclient.Path);

                        throw new UnauthorizedAccessException(errorMessage, ex);
                    }

                    queueIndex = (queueIndex + 1) % queues.Length;
                    await Task.Delay(100);
                } while (true);
            }).GetAwaiter().GetResult();

            return job;
        }

        public void Enqueue(DbConnection connection, DbTransaction transaction, string queue, string jobId)
        {
            // Because we are within a TransactionScope at this point the below
            // call would not work (Local transactions are not supported with other resource managers/DTC
            // exception is thrown) without suppression
            using (new TransactionScope(TransactionScopeOption.Suppress))
            {
                Task.Run(async () =>
                {
                    var (queryclient, messageReceiver) = await _manager.GetClientAsync(queue);

                    var message = new Message(Encoding.UTF8.GetBytes(jobId));
                    await _manager.Options.RetryPolicy.Execute(() => queryclient.SendAsync(message));
                }).Wait();
            }
        }
    }
}
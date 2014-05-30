﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless.Extensions;
using Exceptionless.Logging;
using Exceptionless.Models;
using Exceptionless.Storage;
using Exceptionless.Submission;

namespace Exceptionless.Queue {
    public class DefaultEventQueue : IEventQueue {
        private readonly IExceptionlessLog _log;
        private readonly ExceptionlessConfiguration _config;
        private readonly ISubmissionClient _client;
        private readonly IFileStorage _storage;
        private readonly IJsonSerializer _serializer;
        private Timer _queueTimer;
        private bool _processingQueue;
        private readonly TimeSpan _processQueueInterval = TimeSpan.FromSeconds(10);
        private DateTime? _suspendProcessingUntil;
        private DateTime? _discardQueuedItemsUntil;

        public DefaultEventQueue(ExceptionlessConfiguration config, IExceptionlessLog log, ISubmissionClient client, IFileStorage fileStorage, IJsonSerializer serializer, TimeSpan? processQueueInterval = null, TimeSpan? queueStartDelay = null) {
            _log = log;
            _config = config;
            _client = client;
            _storage = fileStorage;
            _serializer = serializer;
            if (processQueueInterval.HasValue)
                _processQueueInterval = processQueueInterval.Value;

            _queueTimer = new Timer(OnProcessQueue, null, queueStartDelay ?? TimeSpan.FromSeconds(10), _processQueueInterval);
        }

        public Task EnqueueAsync(Event ev) {
            if (AreQueuedItemsDiscarded) {
                _log.Info(typeof(ExceptionlessClient), "Queue items are currently being discarded. The event will not be queued.");
                return TaskEx.FromResult(0);
            }

            return _storage.SaveFileAsync(String.Concat("q\\", Guid.NewGuid().ToString("N"), ".0.json"), _serializer.Serialize(ev));
        }

        public async Task ProcessAsync(TimeSpan? delay = null) {
            if (delay.HasValue)
                await TaskEx.Delay(delay.Value);
            
            if (IsQueueProcessingSuspended)
                return;
            
            _log.Info(typeof(DefaultEventQueue), "Processing queue...");
            if (!_config.Enabled) {
                _log.Info(typeof(DefaultEventQueue), "Configuration is disabled. The queue will not be processed.");
                return;
            }

            if (_processingQueue) {
                _log.Info(typeof(DefaultEventQueue), "The queue is already being processed.");
                return;
            }

            _processingQueue = true;

            try {
                var batch = await _storage.GetEventBatchAsync(_serializer);
                if (!batch.Any()) {
                    _log.Info(typeof(DefaultEventQueue), "There are no events in the queue to process.");
                    return;
                }

                var response = await _client.SubmitAsync(batch.Select(b => b.Item2), _config);
                if (response.Success) {
                    await _storage.DeleteBatchAsync(batch);
                } else {
                    _log.Info(typeof(DefaultEventQueue), String.Concat("An error occurred while submitting events: ", response.Message));
                    await _storage.ReleaseBatchAsync(batch);
                }
            } finally {
                _processingQueue = false;
            }

            // TODO: Check to see if the configuration needs to be updated.
        }

        private void OnProcessQueue(object state) {
            if (!_processingQueue)
                ProcessAsync().Wait();
        }

        public void SuspendProcessing(TimeSpan? duration = null, bool discardQueuedItems = false, bool clearQueue = false) {
            if (!duration.HasValue)
                duration = TimeSpan.FromMinutes(5);

            _log.Info(typeof(ExceptionlessClient), String.Format("Suspending processing for: {0}.", duration.Value));
            _suspendProcessingUntil = DateTime.Now.Add(duration.Value);
            _queueTimer.Change(duration.Value, _processQueueInterval);

            if (discardQueuedItems)
                _discardQueuedItemsUntil = DateTime.Now.Add(duration.Value);

            if (!clearQueue)
                return;

            // Account is over the limit and we want to ensure that the sample size being sent in will contain newer errors.
            try {
                _storage.DeleteOldQueueFilesAsync(DateTime.Now);
            } catch (Exception) { }
        }

        private bool IsQueueProcessingSuspended {
            get { return _suspendProcessingUntil.HasValue && _suspendProcessingUntil.Value > DateTime.Now; }
        }

        private bool AreQueuedItemsDiscarded {
            get { return _discardQueuedItemsUntil.HasValue && _discardQueuedItemsUntil.Value > DateTime.Now; }
        }

        public void Dispose() {
            if (_queueTimer == null)
                return;

            _queueTimer.Dispose();
            _queueTimer = null;
        }
    }
}
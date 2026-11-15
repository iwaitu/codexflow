// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;

namespace CodexFlow.Services.Background
{
    /// <summary>
    /// Background host that continuously drains pending outbox events through <see cref="IOutboxProjector"/>.
    /// </summary>
    public class OutboxProjectorHostedService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OutboxProjectorHostedService> _logger;
        private readonly IOutboxSignal _outboxSignal;

        /// <summary>
        /// Initializes the outbox projector hosted service.
        /// </summary>
        /// <param name="serviceProvider">Service provider used to create scoped projector instances.</param>
        /// <param name="logger">Logger used for projection loop diagnostics.</param>
        /// <param name="outboxSignal">Signal used to wake the projector when new outbox events arrive.</param>
        public OutboxProjectorHostedService(
            IServiceProvider serviceProvider, 
            ILogger<OutboxProjectorHostedService> logger,
            IOutboxSignal outboxSignal)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _outboxSignal = outboxSignal;
        }

        /// <summary>
        /// Runs the outbox projection loop with signal-aware backoff when the queue is idle.
        /// </summary>
        /// <param name="stoppingToken">Cancellation token that stops the background loop.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var minDelay = TimeSpan.FromMilliseconds(50);
            var maxDelay = TimeSpan.FromSeconds(5);
            var currentDelay = minDelay;

            while (!stoppingToken.IsCancellationRequested)
            {
                int processedCount = 0;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var projector = scope.ServiceProvider.GetRequiredService<IOutboxProjector>();
                    processedCount = await projector.ProjectPendingAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in OutboxProjectorHostedService");
                }

                if (processedCount > 0)
                {
                    // If we had work, don't wait long for the next batch
                    currentDelay = minDelay;
                    await Task.Delay(currentDelay, stoppingToken);
                }
                else
                {
                    // If idle, wait for a signal or a longer timeout
                    currentDelay = TimeSpan.FromMilliseconds(Math.Min(currentDelay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
                    await _outboxSignal.WaitAsync(currentDelay, stoppingToken);
                }
            }
        }
    }
}

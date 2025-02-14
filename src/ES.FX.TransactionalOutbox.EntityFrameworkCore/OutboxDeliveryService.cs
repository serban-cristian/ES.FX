﻿using System.Diagnostics;
using System.Text.Json;
using ES.FX.TransactionalOutbox.EntityFrameworkCore.Delivery;
using ES.FX.TransactionalOutbox.EntityFrameworkCore.Entities;
using ES.FX.TransactionalOutbox.EntityFrameworkCore.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ES.FX.TransactionalOutbox.EntityFrameworkCore;

/// <summary>
///     Hosted service that delivers outbox messages
/// </summary>
/// <typeparam name="TDbContext"> The <see cref="TDbContext" /> from which to process messages </typeparam>
/// <param name="logger"> The <see cref="ILogger{TCategoryName}" /> </param>
/// <param name="serviceProvider"> The <see cref="IServiceProvider" /> </param>
public class OutboxDeliveryService<TDbContext>(
    ILogger<OutboxDeliveryService<TDbContext>> logger,
    IServiceProvider serviceProvider)
    : BackgroundService
    where TDbContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogTrace("Starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            //Always sleep unless there are more outboxes to process
            var sleep = true;

            //Default sleep interval. Gets updated by the options. This is used if options are not available due to exceptions
            var sleepInterval = TimeSpan.FromSeconds(10);
            try
            {
                //Create a new scope for each iteration to avoid memory leaks
                await using var scope = serviceProvider.CreateAsyncScope();
                var options = scope.ServiceProvider
                    .GetRequiredService<IOptionsMonitor<OutboxDeliveryOptions<TDbContext>>>()
                    .CurrentValue;
                var messageTypes = scope.ServiceProvider.GetServices<IOutboxMessageType>().ToArray();
                var messageHandler = scope.ServiceProvider.GetRequiredService<IOutboxMessageHandler>();
                if (!await messageHandler.IsReadyAsync())
                {
                    logger.LogTrace("The message handler is not ready yet.");
                    continue;
                }

                if (options.DeliveryServiceEnabled == false) continue;
                if (options.ExclusiveOutboxProvider is null)
                    throw new NotSupportedException($"{nameof(options.ExclusiveOutboxProvider)} cannot be null." +
                                                    "This should be set by the underlying database provider implementation.");

                sleepInterval = options.PollingInterval;

                await using var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,
                    new CancellationTokenSource(options.DeliveryTimeout).Token);

                await using var transaction = await dbContext.Database
                    .BeginTransactionAsync(options.TransactionIsolationLevel, timeout.Token)
                    .ConfigureAwait(false);

                try
                {
                    var outbox = await options.ExclusiveOutboxProvider
                        .GetNextExclusiveOutboxWithoutDelay(dbContext, timeout.Token)
                        .ConfigureAwait(false);

                    if (outbox is null)
                    {
                        logger.LogTrace("No outbox available");
                        continue;
                    }

                    //Set the lock for providers that do not support native locks. This updates the RowVersion
                    outbox.Lock = Guid.NewGuid();
                    outbox.DeliveryDelayedUntil = null;

                    dbContext.Update((object)outbox);
                    await dbContext.SaveChangesAsync(timeout.Token).ConfigureAwait(false);

                    logger.LogTrace("Processing outbox {OutboxId}", outbox.Id);

                    var messages = await dbContext.Set<OutboxMessage>()
                        .Where(x => x.OutboxId == outbox.Id)
                        .OrderBy(x => x.Id)
                        .Take(options.BatchSize)
                        .AsTracking()
                        .ToListAsync(timeout.Token)
                        .ConfigureAwait(false);


                    foreach (var message in messages)
                    {
                        if (DateTimeOffset.UtcNow > message.DeliveryNotAfter)
                        {
                            logger.LogTrace("Outbox message {outboxId}/{messageId} has expired", message.OutboxId,
                                message.Id);
                            dbContext.Remove((object)message);
                            continue;
                        }

                        if (DateTimeOffset.UtcNow < message.DeliveryNotBefore)
                        {
                            logger.LogTrace(
                                "Outbox message {outboxId}/{messageId} is not ready to be delivered until {notBefore}. Delaying outbox.",
                                message.OutboxId,
                                message.Id, message.DeliveryNotBefore);

                            //Delay the entire outbox to preserve the order of messages
                            outbox.DeliveryDelayedUntil = message.DeliveryNotBefore;

                            //Proceed to the next available outbox, since this one is delayed
                            break;
                        }

                        message.DeliveryFirstAttemptedAt ??= DateTimeOffset.UtcNow;
                        message.DeliveryLastAttemptedAt = DateTimeOffset.UtcNow;
                        message.DeliveryAttempts++;


                        if (await DeliverMessage(message, messageHandler, messageTypes, timeout.Token)
                                .ConfigureAwait(false))
                        {
                            dbContext.Remove(message);
                        }
                        else
                        {
                            //If the message has reached the maximum number of attempts, mark it as faulted
                            if (message.DeliveryAttempts >= (message.DeliveryMaxAttempts ?? 1))
                            {
                                logger.LogError("Outbox/message {outboxId}/{messageId} delivery has permanently failed",
                                    message.OutboxId, message.Id);
                                dbContext.Remove((object)message);

                                dbContext.Set<OutboxMessageFault>().Add(new OutboxMessageFault
                                {
                                    Id = message.Id,
                                    OutboxId = message.OutboxId,
                                    AddedAt = message.AddedAt,
                                    Headers = message.Headers,
                                    Payload = message.Payload,
                                    PayloadType = message.PayloadType,
                                    DeliveryAttempts = message.DeliveryAttempts,
                                    DeliveryFirstAttemptedAt = message.DeliveryFirstAttemptedAt,
                                    DeliveryLastAttemptedAt = message.DeliveryLastAttemptedAt,
                                    DeliveryLastAttemptError = message.DeliveryLastAttemptError,
                                    DeliveryNotBefore = message.DeliveryNotBefore,
                                    DeliveryNotAfter = message.DeliveryNotAfter,
                                    FaultedAt = DateTimeOffset.UtcNow
                                });
                            }
                            // If the message has a delay, delay the entire outbox
                            else if (message.DeliveryAttemptDelay > 0)
                            {
                                var waitTime = message.DeliveryAttemptDelay *
                                               (message.DeliveryAttemptDelayIsExponential
                                                   ? (int)Math.Pow(2, message.DeliveryAttempts - 1)
                                                   : 1);
                                outbox.DeliveryDelayedUntil = DateTimeOffset.UtcNow.AddSeconds(waitTime);

                                logger.LogWarning(
                                    "Delaying delivery of outbox {outboxId} for {delay} due to message delivery failure",
                                    message.OutboxId,
                                    outbox.DeliveryDelayedUntil.Value.Subtract(DateTimeOffset.UtcNow));

                                //Proceed to the next available outbox, since this one is delayed
                                break;
                            }
                        }
                    }


                    if (messages.Count == 0)
                    {
                        logger.LogTrace("Removing empty outbox {outboxId}", outbox.Id);
                        dbContext.Remove((object)outbox);
                    }

                    await dbContext.SaveChangesAsync(timeout.Token).ConfigureAwait(false);
                    await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
                    sleep = false;
                }
                catch (OperationCanceledException)
                {
                    //Ignored
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Exception occured while processing outbox");
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception innerException)
                    {
                        logger.LogWarning(innerException, "Transaction rollback failed");
                    }
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Exception occured while acquiring an outbox");
            }
            finally
            {
                if (sleep) await Sleep(sleepInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> DeliverMessage(OutboxMessage message,
        IOutboxMessageHandler messageHandler,
        IOutboxMessageType[] messageTypes,
        CancellationToken cancellationToken)
    {
        var deliverMessageActivity = new Activity("OutboxMessageDelivery");
        try
        {
            logger.LogTrace("Delivering outbox/message:{outboxId}/{messageId} - Attempt {attempt}/{maxAttempts}",
                message.OutboxId, message.Id, message.DeliveryAttempts, message.DeliveryMaxAttempts ?? 1);

            var headers =
                JsonSerializer.Deserialize<Dictionary<string, string?>>(message.Headers) ?? [];

            //Set the parent activity id if available for distributed tracing
            var parentActivityId =
                headers.GetValueOrDefault(OutboxMessageHeaders.Diagnostics.ActivityId);
            if (!string.IsNullOrWhiteSpace(parentActivityId))
                deliverMessageActivity.SetParentId(parentActivityId);

            deliverMessageActivity.Start();

            //Determines the payload type of the message. Order of precedence: MessageType attribute, PayloadType, PayloadOriginalType
            var payloadType = messageTypes.FirstOrDefault(x =>
                                  x.PayloadType.Equals(message.PayloadType,
                                      StringComparison.InvariantCultureIgnoreCase))?.MessageType ??
                              Type.GetType(message.PayloadType) ??
                              Type.GetType(
                                  headers.TryGetValue(OutboxMessageHeaders.Message.PayloadOriginalType, out var hint)
                                      ? hint ?? string.Empty
                                      : string.Empty) ??
                              throw new NotSupportedException("Could not determine the Type of the message");


            var payload = JsonSerializer.Deserialize(message.Payload, payloadType);
            if (payload is null)
                throw new NotSupportedException("Could not deserialize the message payload");

            var success = await messageHandler
                .HandleAsync(new OutboxMessageHandlerContext(payloadType, payload), cancellationToken)
                .ConfigureAwait(false);

            if (!success)
                logger.LogWarning(
                    "Outbox/message {outboxId}/{messageId} could not be delivered. Handler reported failure.",
                    message.OutboxId, message.Id);
            return success;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Exception occured while delivering {outboxId}/{messageId}", message.OutboxId,
                message.Id);
            message.DeliveryLastAttemptError = exception.ToString().Take(4000).ToString();
            return false;
        }
        finally
        {
            deliverMessageActivity.Stop();
        }
    }


    private async Task Sleep(TimeSpan delay, CancellationToken cancellationToken)
    {
        logger.LogTrace("Sleeping for {delay}", delay);
        OutboxDeliverySignal.RenewChannel<TDbContext>();

        using var delayTokenCts = new CancellationTokenSource(delay);
        using var sleepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delayTokenCts.Token);
        try
        {
            await OutboxDeliverySignal.GetChannel<TDbContext>().Reader.ReadAsync(sleepCts.Token).ConfigureAwait(false);
            logger.LogTrace("Sleep interrupted by signal");
        }
        catch (OperationCanceledException)
        {
            // Ignored. This is expected due to the delay token
        }
    }
}
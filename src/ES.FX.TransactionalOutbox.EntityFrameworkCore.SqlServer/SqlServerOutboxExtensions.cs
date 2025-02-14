﻿using ES.FX.TransactionalOutbox.EntityFrameworkCore.Delivery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ES.FX.TransactionalOutbox.EntityFrameworkCore.SqlServer;

public static class SqlServerOutboxExtensions
{
    /// <summary>
    ///     Adds the outbox delivery service to the service collection. The <see cref="TMessageHandler" /> will be used to
    ///     deliver the messages.
    /// </summary>
    /// <typeparam name="TDbContext">The type of <see cref="DbContext" /> for which to process messages</typeparam>
    /// <typeparam name="TMessageHandler"> The type of <see cref="IOutboxMessageHandler" /> used to delivery messages </typeparam>
    /// <param name="services">The <see cref="IServiceCollection" /> on which to register the required services</param>
    /// <param name="configureOptions">
    ///     The options used to configure the <see cref="OutboxDeliveryService{TDbContext}" />
    /// </param>
    public static IServiceCollection AddOutboxDeliveryService<TDbContext, TMessageHandler>(
        this IServiceCollection services,
        Action<OutboxDeliveryOptions<TDbContext>>? configureOptions = null
    )
        where TDbContext : DbContext, IOutboxDbContext
        where TMessageHandler : class, IOutboxMessageHandler
    {
        services.AddOptions<OutboxDeliveryOptions<TDbContext>>().Configure(options =>
        {
            options.ExclusiveOutboxProvider = new SqlServerExclusiveOutboxProvider<TDbContext>();
            configureOptions?.Invoke(options);
        });

        services.AddHostedService<OutboxDeliveryService<TDbContext>>();
        services.AddScoped<IOutboxMessageHandler, TMessageHandler>();
        return services;
    }
}
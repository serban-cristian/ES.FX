﻿using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ES.FX.Shared.Seq.Tests.Fixtures;

public sealed class SeqContainerFixture : IAsyncLifetime
{
    public const string Registry = "datalust";
    public const string Image = "seq";
    public const string Tag = "latest";
    public SeqSUTWebApplicationFactory? WebApplicationFactory;
    public IContainer? Container { get; private set; }

    public async Task InitializeAsync()
    {
        Container = new ContainerBuilder()
            .WithName($"{nameof(SeqContainerFixture)}-{Guid.NewGuid()}")
            .WithImage($"{Registry}/{Image}:{Tag}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPath("/")))
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithPortBinding(80, true)
            .Build();
        await Container.StartAsync();

        WebApplicationFactory = new SeqSUTWebApplicationFactory(GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (Container is not null) await Container.DisposeAsync();
        if (WebApplicationFactory is not null) WebApplicationFactory.Dispose();
    }

    public string GetConnectionString() => $"http://{Container?.Hostname}:{Container?.GetMappedPublicPort(80)}";
}
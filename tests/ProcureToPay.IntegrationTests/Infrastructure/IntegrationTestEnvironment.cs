using Testcontainers.LocalStack;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Infrastructure;

public sealed class IntegrationTestEnvironment : IAsyncDisposable
{
    public MsSqlContainer SqlServer { get; } = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("ProcureToPay_test_2026!")
        .Build();

    public LocalStackContainer LocalStack { get; } = new LocalStackBuilder("localstack/localstack:4.15.0")
        .Build();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(
            SqlServer.StartAsync(cancellationToken),
            LocalStack.StartAsync(cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            SqlServer.DisposeAsync().AsTask(),
            LocalStack.DisposeAsync().AsTask());
    }
}

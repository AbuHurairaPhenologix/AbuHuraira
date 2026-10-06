using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace AnomalyDetection.IntegrationTests.Infrastructure;

/// <summary>
/// Real PostgreSQL and OpenSearch containers, started once per test run and shared by all factories
/// (each factory uses its own database and index so tests stay isolated).
/// </summary>
public static class SharedContainers
{
    private static readonly Lazy<Task<(PostgreSqlContainer Postgres, IContainer OpenSearch)>> Started = new(StartAsync);

    public static Task<(PostgreSqlContainer Postgres, IContainer OpenSearch)> GetAsync() => Started.Value;

    public static string OpenSearchUrl(IContainer openSearch) => $"http://{openSearch.Hostname}:{openSearch.GetMappedPublicPort(9200)}";

    private static async Task<(PostgreSqlContainer, IContainer)> StartAsync()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("anomaly_it")
            .WithUsername("it_user")
            .WithPassword($"it-{Guid.NewGuid():N}")
            .Build();

        var openSearch = new ContainerBuilder("opensearchproject/opensearch:2.19.2")
            .WithEnvironment("discovery.type", "single-node")
            .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
            .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
            .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
            .WithPortBinding(9200, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/_cluster/health")))
            .Build();

        await Task.WhenAll(postgres.StartAsync(), openSearch.StartAsync());
        return (postgres, openSearch);
    }
}

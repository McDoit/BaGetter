using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using BaGetter.Core;
using BaGetter.Database.DynamoDb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BaGetter;

public static class DynamoDbApplicationExtensions
{
    /// <summary>
    /// Registers the DynamoDB-backed <see cref="IPackageDatabase"/> and <see cref="ISearchService"/>
    /// with the BaGetter application. Activated when <c>Database:Type</c> is set to <c>"AwsDynamoDb"</c>.
    /// A hosted service (<see cref="DynamoDbTableInitializer"/>) automatically creates the DynamoDB
    /// table and <c>SearchIndex</c> GSI if they do not already exist.
    /// </summary>
    public static BaGetterApplication AddDynamoDbDatabase(this BaGetterApplication app)
    {
        app.Services.AddBaGetterOptions<DynamoDbDatabaseOptions>(nameof(BaGetterOptions.Database));

        // Register the DynamoDB client as a singleton.
        app.Services.AddSingleton<IAmazonDynamoDB>(provider =>
        {
            var opts = provider.GetRequiredService<IOptions<DynamoDbDatabaseOptions>>().Value;

            AmazonDynamoDBConfig config;

            if (!string.IsNullOrEmpty(opts.ServiceUrl))
            {
                config = new AmazonDynamoDBConfig { ServiceURL = opts.ServiceUrl };
            }
            else if (!string.IsNullOrEmpty(opts.Region))
            {
                config = new AmazonDynamoDBConfig
                {
                    RegionEndpoint = RegionEndpoint.GetBySystemName(opts.Region)
                };
            }
            else
            {
                config = new AmazonDynamoDBConfig();
            }

            return new AmazonDynamoDBClient(FallbackCredentialsFactory.GetCredentials(), config);
        });

        app.Services.AddTransient<DynamoDbPackageDatabase>();
        app.Services.AddTransient<DynamoDbSearchService>();

        app.Services.TryAddTransient<IPackageDatabase>(
            provider => provider.GetRequiredService<DynamoDbPackageDatabase>());

        app.Services.TryAddTransient<ISearchService>(
            provider => provider.GetRequiredService<DynamoDbSearchService>());

        // Provider registrations: activated by configuration.
        app.Services.AddProvider<IPackageDatabase>((provider, config) =>
        {
            if (!config.HasDatabaseType("AwsDynamoDb")) return null;
            return provider.GetRequiredService<DynamoDbPackageDatabase>();
        });

        app.Services.AddProvider<ISearchService>((provider, config) =>
        {
            if (!config.HasSearchType("AwsDynamoDb")) return null;
            return provider.GetRequiredService<DynamoDbSearchService>();
        });

        app.Services.AddProvider<ISearchIndexer>((provider, config) =>
        {
            if (!config.HasSearchType("AwsDynamoDb")) return null;
            return provider.GetRequiredService<NullSearchIndexer>();
        });

        // Auto-create the DynamoDB table on startup only when DynamoDB is configured as the database.
        app.Services.AddSingleton<IHostedService>(provider =>
        {
            var config = provider.GetRequiredService<IConfiguration>();
            if (!config.HasDatabaseType("AwsDynamoDb"))
            {
                return new NoOpHostedService();
            }

            return ActivatorUtilities.CreateInstance<DynamoDbTableInitializer>(provider);
        });

        return app;
    }

    public static BaGetterApplication AddDynamoDbDatabase(
        this BaGetterApplication app,
        Action<DynamoDbDatabaseOptions> configure)
    {
        app.AddDynamoDbDatabase();
        app.Services.Configure(configure);
        return app;
    }

    private sealed class NoOpHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

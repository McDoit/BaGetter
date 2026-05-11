using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using BaGetter.Core;
using BaGetter.Database.DynamoDb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NuGet.Versioning;
using Xunit;

namespace BaGetter.Tests;

public class DynamoDbHardeningTests
{
    [Fact]
    public void ValidateBaGetterOptions_AcceptsAwsDynamoDbTypes()
    {
        var validator = new ValidateBaGetterOptions();
        var options = new BaGetterOptions
        {
            Database = new DatabaseOptions { Type = "AwsDynamoDb", ConnectionString = "not-used" },
            Search = new SearchOptions { Type = "AwsDynamoDb" },
            Storage = new StorageOptions { Type = "FileSystem" },
            Mirror = new MirrorOptions { Enabled = false, PackageSource = new Uri("https://api.nuget.org/v3/index.json") },
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void DefaultSqliteConfiguration_DoesNotResolveDynamoDbClient()
    {
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BaGetterTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempPath);

        using var host = Program
            .CreateHostBuilder(Array.Empty<string>())
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    { "Database:Type", "Sqlite" },
                    { "Database:ConnectionString", $"Data Source={System.IO.Path.Combine(tempPath, "BaGetter.db")}" },
                    { "Storage:Type", "FileSystem" },
                    { "Storage:Path", tempPath },
                    { "Search:Type", "Database" },
                    { "Mirror:Enabled", "false" },
                    { "Mirror:PackageSource", "https://api.nuget.org/v3/index.json" },
                });
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                    throw new InvalidOperationException("IAmazonDynamoDB should not be resolved for Sqlite config."));
            })
            .Build();

        // Resolving hosted services will execute their factories. This should not resolve IAmazonDynamoDB.
        var hostedServices = host.Services.GetServices<IHostedService>();

        Assert.NotNull(hostedServices);
    }

    [Fact]
    public async Task TableInitializer_ThrowsWhenTableNeverBecomesActive()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var describeCalls = 0;

        client.Setup(x => x.DescribeTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) =>
            {
                describeCalls++;
                if (describeCalls == 1)
                {
                    throw new ResourceNotFoundException("missing");
                }

                return Task.FromResult(new DescribeTableResponse
                {
                    Table = new TableDescription
                    {
                        TableStatus = TableStatus.CREATING,
                        GlobalSecondaryIndexes =
                        [
                            new GlobalSecondaryIndexDescription
                            {
                                IndexName = "SearchIndex",
                                IndexStatus = IndexStatus.CREATING
                            }
                        ]
                    }
                });
            });

        client.Setup(x => x.CreateTableAsync(It.IsAny<CreateTableRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTableResponse());

        var initializer = new DynamoDbTableInitializer(
            client.Object,
            Options.Create(new DynamoDbDatabaseOptions { TableName = "bagetter-packages" }),
            NullLogger<DynamoDbTableInitializer>.Instance,
            maxWaitAttempts: 1,
            waitIntervalSeconds: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddDownloadAsync_DoesNotUpdateMetaWhenVersionIsMissing()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var updateCalls = 0;

        client.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<UpdateItemRequest, CancellationToken>((_, _) =>
            {
                updateCalls++;
                throw new ConditionalCheckFailedException("missing");
            });

        var database = new DynamoDbPackageDatabase(
            client.Object,
            new TestOptionsSnapshot<DynamoDbDatabaseOptions>(new DynamoDbDatabaseOptions { TableName = "bagetter-packages" }));

        await database.AddDownloadAsync("Package.A", NuGetVersion.Parse("1.2.3"), CancellationToken.None);

        Assert.Equal(1, updateCalls);
    }

    [Fact]
    public async Task FindDependentsAsync_ReturnsEmptyAndSkipsDynamoDbScan()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var responseBuilder = new Mock<ISearchResponseBuilder>(MockBehavior.Strict);

        responseBuilder
            .Setup(x => x.BuildDependents(It.Is<IReadOnlyList<PackageDependent>>(d => d.Count == 0)))
            .Returns(new DependentsResponse
            {
                TotalHits = 0,
                Data = []
            });

        var search = new DynamoDbSearchService(
            client.Object,
            new TestOptionsSnapshot<DynamoDbDatabaseOptions>(new DynamoDbDatabaseOptions { TableName = "bagetter-packages" }),
            responseBuilder.Object);

        var result = await search.FindDependentsAsync("Package.A", CancellationToken.None);

        Assert.Equal(0, result.TotalHits);
        client.Verify(x => x.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        responseBuilder.VerifyAll();
    }

    private sealed class TestOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class, new()
    {
        public TestOptionsSnapshot(T value)
        {
            Value = value;
        }

        public T Value { get; }
        public T Get(string name) => Value;
    }
}

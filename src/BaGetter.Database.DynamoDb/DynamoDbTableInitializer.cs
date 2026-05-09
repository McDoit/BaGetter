using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaGetter.Database.DynamoDb;

/// <summary>
/// Hosted service that creates the DynamoDB table (and the <c>SearchIndex</c> GSI)
/// on application startup if they do not already exist.
/// </summary>
public class DynamoDbTableInitializer : IHostedService
{
    private const int MaxWaitAttempts = 30;
    private const int WaitIntervalSeconds = 2;

    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDbDatabaseOptions _options;
    private readonly ILogger<DynamoDbTableInitializer> _logger;

    public DynamoDbTableInitializer(
        IAmazonDynamoDB client,
        IOptions<DynamoDbDatabaseOptions> options,
        ILogger<DynamoDbTableInitializer> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.DescribeTableAsync(_options.TableName, cancellationToken);
            _logger.LogInformation("DynamoDB table '{TableName}' already exists.", _options.TableName);
        }
        catch (ResourceNotFoundException)
        {
            _logger.LogInformation("Creating DynamoDB table '{TableName}'.", _options.TableName);
            await CreateTableAsync(cancellationToken);
            _logger.LogInformation("DynamoDB table '{TableName}' created successfully.", _options.TableName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CreateTableAsync(CancellationToken cancellationToken)
    {
        var request = new CreateTableRequest
        {
            TableName = _options.TableName,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new() { AttributeName = "PK",              AttributeType = ScalarAttributeType.S },
                new() { AttributeName = "SK",              AttributeType = ScalarAttributeType.S },
                new() { AttributeName = "SearchPartition", AttributeType = ScalarAttributeType.S },
                new() { AttributeName = "LowerId",         AttributeType = ScalarAttributeType.S }
            },
            KeySchema = new List<KeySchemaElement>
            {
                new() { AttributeName = "PK", KeyType = KeyType.HASH  },
                new() { AttributeName = "SK", KeyType = KeyType.RANGE }
            },
            GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
            {
                new()
                {
                    IndexName = "SearchIndex",
                    KeySchema = new List<KeySchemaElement>
                    {
                        new() { AttributeName = "SearchPartition", KeyType = KeyType.HASH  },
                        new() { AttributeName = "LowerId",         KeyType = KeyType.RANGE }
                    },
                    Projection = new Projection { ProjectionType = ProjectionType.ALL }
                }
            }
        };

        await _client.CreateTableAsync(request, cancellationToken);

        // Wait until the table (and GSI) becomes ACTIVE before returning.
        var waitAttempts = 0;
        while (waitAttempts++ < MaxWaitAttempts)
        {
            await Task.Delay(TimeSpan.FromSeconds(WaitIntervalSeconds), cancellationToken);

            var desc = await _client.DescribeTableAsync(_options.TableName, cancellationToken);
            var tableActive = desc.Table.TableStatus == TableStatus.ACTIVE;
            var gsiActive = desc.Table.GlobalSecondaryIndexes
                .TrueForAll(g => g.IndexStatus == IndexStatus.ACTIVE);

            if (tableActive && gsiActive)
                return;
        }

        _logger.LogWarning(
            "DynamoDB table '{TableName}' did not reach ACTIVE status within the expected time.",
            _options.TableName);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using BaGetter.Core;
using BaGetter.Protocol.Models;
using Microsoft.Extensions.Options;

namespace BaGetter.Database.DynamoDb;

/// <summary>
/// <see cref="ISearchService"/> backed by Amazon DynamoDB using a Global Secondary Index (GSI).
/// </summary>
/// <remarks>
/// This is a v1 implementation intended for small-to-medium private NuGet feeds.
/// It uses a GSI named <c>SearchIndex</c> with a constant partition key <c>SearchPartition = "PKG"</c>
/// and sort key <c>LowerId</c>. A DynamoDB <c>Query</c> with <c>begins_with(LowerId, :term)</c> is
/// used for prefix search, followed by client-side filtering for prerelease / SemVer2.
/// <para>
/// <b>Limitation:</b> All packages share a single GSI partition (<c>"PKG"</c>), which DynamoDB
/// limits to 10 GB of index data. This is acceptable for private feeds but may need to be revisited
/// for very large public registries.
/// </para>
/// </remarks>
public class DynamoDbSearchService : ISearchService
{
    private const string SearchPartitionValue = "PKG";
    private const string SearchIndexName = "SearchIndex";
    private const string VersionSkPrefix = "VERSION#";

    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;
    private readonly ISearchResponseBuilder _responseBuilder;

    public DynamoDbSearchService(
        IAmazonDynamoDB client,
        IOptionsSnapshot<DynamoDbDatabaseOptions> options,
        ISearchResponseBuilder responseBuilder)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(options);
        _tableName = options.Value.TableName;
        _responseBuilder = responseBuilder ?? throw new ArgumentNullException(nameof(responseBuilder));
    }

    /// <inheritdoc/>
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var packages = await QueryPackagesAsync(request.Query, cancellationToken);

        // Client-side filtering
        var filtered = packages
            .Where(p => p.Listed)
            .Where(p => request.IncludePrerelease || !p.IsPrerelease)
            .Where(p => request.IncludeSemVer2 || p.SemVerLevel != SemVerLevel.SemVer2)
            .Where(p => string.IsNullOrEmpty(request.PackageType)
                || p.PackageTypes.Any(t => string.Equals(t.Name, request.PackageType, StringComparison.OrdinalIgnoreCase)));

        var grouped = filtered
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PackageRegistration(g.Key, g.ToList()))
            .OrderBy(r => r.PackageId, StringComparer.OrdinalIgnoreCase)
            .Skip(request.Skip)
            .Take(request.Take)
            .ToList();

        return _responseBuilder.BuildSearch(grouped);
    }

    /// <inheritdoc/>
    public async Task<AutocompleteResponse> AutocompleteAsync(AutocompleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var packages = await QueryPackagesAsync(request.Query, cancellationToken);

        var ids = packages
            .Where(p => p.Listed)
            .Where(p => request.IncludePrerelease || !p.IsPrerelease)
            .Where(p => request.IncludeSemVer2 || p.SemVerLevel != SemVerLevel.SemVer2)
            .Where(p => string.IsNullOrEmpty(request.PackageType)
                || p.PackageTypes.Any(t => string.Equals(t.Name, request.PackageType, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(p => p.Downloads))
            .Skip(request.Skip)
            .Take(request.Take)
            .Select(g => g.Key)
            .ToList();

        return _responseBuilder.BuildAutocomplete(ids);
    }

    /// <inheritdoc/>
    public async Task<AutocompleteResponse> ListPackageVersionsAsync(VersionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Query directly by PK for all versions of a specific package.
        var queryRequest = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :skPrefix)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#pk", "PK" },
                { "#sk", "SK" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue($"PACKAGE#{request.PackageId.ToLowerInvariant()}") },
                { ":skPrefix", new AttributeValue(VersionSkPrefix) }
            }
        };

        var packages = await ExecuteQueryAsync(queryRequest, cancellationToken);

        var versions = packages
            .Where(p => p.Listed)
            .Where(p => request.IncludePrerelease || !p.IsPrerelease)
            .Where(p => request.IncludeSemVer2 || p.SemVerLevel != SemVerLevel.SemVer2)
            .Select(p => p.NormalizedVersionString)
            .ToList();

        return _responseBuilder.BuildAutocomplete(versions);
    }

    /// <inheritdoc/>
    public Task<DependentsResponse> FindDependentsAsync(string packageId, CancellationToken cancellationToken)
    {
        // Dependents lookup is intentionally unsupported for DynamoDB v1.
        // Implementing this with a full table scan is prohibitively expensive at scale.
        // Return an empty set so callers get a safe, bounded response.
        return Task.FromResult(_responseBuilder.BuildDependents([]));
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private async Task<List<Package>> QueryPackagesAsync(string query, CancellationToken cancellationToken)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = SearchIndexName,
            KeyConditionExpression = "SearchPartition = :sp",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":sp", new AttributeValue(SearchPartitionValue) }
            }
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.ToLowerInvariant();
            request.KeyConditionExpression = "SearchPartition = :sp AND begins_with(LowerId, :term)";
            request.ExpressionAttributeValues[":term"] = new AttributeValue(term);
        }

        return await ExecuteQueryAsync(request, cancellationToken);
    }

    private async Task<List<Package>> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        var packages = new List<Package>();
        Dictionary<string, AttributeValue> lastKey = null;

        do
        {
            request.ExclusiveStartKey = lastKey;
            var response = await _client.QueryAsync(request, cancellationToken);

            foreach (var item in response.Items)
            {
                // Skip META items that may appear in projections
                if (!item.TryGetValue("SK", out var sk) || !sk.S.StartsWith(VersionSkPrefix, StringComparison.Ordinal))
                    continue;

                packages.Add(DynamoDbPackageDatabase.FromItemInternal(item));
            }

            lastKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
        }
        while (lastKey != null);

        return packages;
    }

}

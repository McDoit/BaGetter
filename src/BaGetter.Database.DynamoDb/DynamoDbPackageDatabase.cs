using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using BaGetter.Core;
using Microsoft.Extensions.Options;
using NuGet.Versioning;

namespace BaGetter.Database.DynamoDb;

/// <summary>
/// <see cref="IPackageDatabase"/> backed by Amazon DynamoDB using a single-table design.
/// </summary>
/// <remarks>
/// Table schema (composite key):
/// <list type="table">
///   <item><term>PK</term><description><c>PACKAGE#&lt;lowerId&gt;</c></description></item>
///   <item><term>SK</term><description><c>VERSION#&lt;normalizedVersion&gt;</c> for version items; <c>META</c> for download counters</description></item>
/// </list>
/// All write operations are safe under parallel Lambda concurrency:
/// <see cref="AddAsync"/> uses a conditional <c>PutItem</c> (<c>attribute_not_exists</c>),
/// and <see cref="AddDownloadAsync"/> uses DynamoDB's atomic <c>ADD</c> counter.
/// </remarks>
public class DynamoDbPackageDatabase : IPackageDatabase
{
    private const string Pk = "PK";
    private const string Sk = "SK";
    private const string MetaSk = "META";
    private const string VersionSkPrefix = "VERSION#";
    private const string SearchPartitionValue = "PKG";

    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;

    public DynamoDbPackageDatabase(IAmazonDynamoDB client, IOptionsSnapshot<DynamoDbDatabaseOptions> options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(options);
        _tableName = options.Value.TableName;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Uses a conditional <c>PutItem</c> (<c>attribute_not_exists(PK) AND attribute_not_exists(SK)</c>)
    /// so that two concurrent Lambda invocations publishing the same <c>id@version</c> cannot both
    /// succeed — exactly one returns <see cref="PackageAddResult.Success"/> and any concurrent
    /// duplicate receives <see cref="PackageAddResult.PackageAlreadyExists"/>.
    /// </remarks>
    public async Task<PackageAddResult> AddAsync(Package package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);

        var put = new PutItemRequest
        {
            TableName = _tableName,
            Item = ToItem(package),
            ConditionExpression = "attribute_not_exists(#pk) AND attribute_not_exists(#sk)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#pk", Pk },
                { "#sk", Sk }
            }
        };

        try
        {
            await _client.PutItemAsync(put, cancellationToken);
            return PackageAddResult.Success;
        }
        catch (ConditionalCheckFailedException)
        {
            return PackageAddResult.PackageAlreadyExists;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :skPrefix)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#pk", Pk },
                { "#sk", Sk }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue(PackagePk(id)) },
                { ":skPrefix", new AttributeValue(VersionSkPrefix) }
            },
            ProjectionExpression = "#pk",
            Limit = 1
        };

        var response = await _client.QueryAsync(request, cancellationToken);
        return response.Count > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(version);

        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = MakeKey(id, version),
            ProjectionExpression = "#pk",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#pk", Pk } }
        }, cancellationToken);

        return response.IsItemSet;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Package>> FindAsync(string id, bool includeUnlisted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :skPrefix)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#pk", Pk },
                { "#sk", Sk }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pk", new AttributeValue(PackagePk(id)) },
                { ":skPrefix", new AttributeValue(VersionSkPrefix) }
            }
        };

        var packages = new List<Package>();
        Dictionary<string, AttributeValue> lastEvaluatedKey = null;

        do
        {
            request.ExclusiveStartKey = lastEvaluatedKey;
            var response = await _client.QueryAsync(request, cancellationToken);

            foreach (var item in response.Items)
            {
                var pkg = FromItem(item);
                if (includeUnlisted || pkg.Listed)
                    packages.Add(pkg);
            }

            lastEvaluatedKey = response.LastEvaluatedKey?.Count > 0
                ? response.LastEvaluatedKey
                : null;
        }
        while (lastEvaluatedKey != null);

        return packages.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<Package> FindOrNullAsync(
        string id,
        NuGetVersion version,
        bool includeUnlisted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(version);

        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = MakeKey(id, version)
        }, cancellationToken);

        if (!response.IsItemSet)
            return null;

        var pkg = FromItem(response.Item);
        return (includeUnlisted || pkg.Listed) ? pkg : null;
    }

    /// <inheritdoc/>
    public async Task<bool> UnlistPackageAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        return await SetListedAsync(id, version, listed: false, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> RelistPackageAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        return await SetListedAsync(id, version, listed: true, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Uses DynamoDB's atomic <c>ADD</c> expression on both the version item and the
    /// per-id <c>META</c> item, so no locking is required under parallel Lambda concurrency.
    /// </remarks>
    public async Task AddDownloadAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(version);

        // Increment download counter on the version item.
        await _client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = MakeKey(id, version),
            UpdateExpression = "ADD Downloads :one",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":one", new AttributeValue { N = "1" } }
            }
        }, cancellationToken);

        // Also maintain a per-id total on the META item (upsert).
        await _client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { Pk, new AttributeValue(PackagePk(id)) },
                { Sk, new AttributeValue(MetaSk) }
            },
            UpdateExpression = "ADD TotalDownloads :one",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":one", new AttributeValue { N = "1" } }
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> HardDeletePackageAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(version);

        // Only delete if the item actually exists (ConditionExpression prevents spurious success).
        try
        {
            await _client.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = MakeKey(id, version),
                ConditionExpression = "attribute_exists(#pk)",
                ExpressionAttributeNames = new Dictionary<string, string> { { "#pk", Pk } }
            }, cancellationToken);

            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string PackagePk(string id) => $"PACKAGE#{id.ToLowerInvariant()}";

    private static string VersionSk(NuGetVersion version) =>
        $"{VersionSkPrefix}{version.ToNormalizedString().ToLowerInvariant()}";

    private static Dictionary<string, AttributeValue> MakeKey(string id, NuGetVersion version) =>
        new()
        {
            { Pk, new AttributeValue(PackagePk(id)) },
            { Sk, new AttributeValue(VersionSk(version)) }
        };

    private static Dictionary<string, AttributeValue> ToItem(Package p)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { Pk, new AttributeValue(PackagePk(p.Id)) },
            { Sk, new AttributeValue(VersionSk(p.Version)) },
            // GSI projection: all items share a constant search partition value.
            { "SearchPartition", new AttributeValue(SearchPartitionValue) },
            { "LowerId", new AttributeValue(p.Id.ToLowerInvariant()) },
            { "Id", new AttributeValue(p.Id) },
            { "NormalizedVersion", new AttributeValue(p.NormalizedVersionString) },
            { "OriginalVersion", new AttributeValue(p.OriginalVersionString ?? p.NormalizedVersionString) },
            { "Description", new AttributeValue(p.Description ?? string.Empty) },
            { "Downloads", new AttributeValue { N = p.Downloads.ToString() } },
            { "HasReadme", new AttributeValue { BOOL = p.HasReadme } },
            { "HasEmbeddedIcon", new AttributeValue { BOOL = p.HasEmbeddedIcon } },
            { "IsPrerelease", new AttributeValue { BOOL = p.IsPrerelease } },
            { "Listed", new AttributeValue { BOOL = p.Listed } },
            { "Published", new AttributeValue(p.Published.ToString("O")) },
            { "RequireLicenseAcceptance", new AttributeValue { BOOL = p.RequireLicenseAcceptance } },
            { "SemVerLevel", new AttributeValue { N = ((int)p.SemVerLevel).ToString() } },
            { "Summary", new AttributeValue(p.Summary ?? string.Empty) },
            { "Title", new AttributeValue(p.Title ?? string.Empty) },
            { "IconUrl", new AttributeValue(p.IconUrlString) },
            { "LicenseUrl", new AttributeValue(p.LicenseUrlString) },
            { "ProjectUrl", new AttributeValue(p.ProjectUrlString) },
            { "RepositoryUrl", new AttributeValue(p.RepositoryUrlString) },
            { "RepositoryType", new AttributeValue(p.RepositoryType ?? string.Empty) },
            // DynamoDB does not allow empty string sets (SS type), so we use a single empty
            // string as a sentinel value when there are no authors or tags. These are
            // filtered back out in FromItem via the Where(s => !string.IsNullOrEmpty) call.
            { "Authors", new AttributeValue { SS = p.Authors?.Length > 0 ? new List<string>(p.Authors) : new List<string> { string.Empty } } },
            { "Tags", new AttributeValue { SS = p.Tags?.Length > 0 ? new List<string>(p.Tags) : new List<string> { string.Empty } } },
            {
                "Dependencies",
                new AttributeValue(JsonSerializer.Serialize(
                    p.Dependencies?.Select(d => new { d.Id, d.VersionRange, d.TargetFramework }) ?? []))
            },
            {
                "PackageTypes",
                new AttributeValue(JsonSerializer.Serialize(
                    p.PackageTypes?.Select(t => new { t.Name, t.Version }) ?? []))
            },
            {
                "TargetFrameworks",
                new AttributeValue(JsonSerializer.Serialize(
                    p.TargetFrameworks?.Select(f => f.Moniker) ?? []))
            }
        };

        if (!string.IsNullOrEmpty(p.Language))
            item["Language"] = new AttributeValue(p.Language);

        if (!string.IsNullOrEmpty(p.MinClientVersion))
            item["MinClientVersion"] = new AttributeValue(p.MinClientVersion);

        if (!string.IsNullOrEmpty(p.CachedFrom))
            item["CachedFrom"] = new AttributeValue(p.CachedFrom);

        if (!string.IsNullOrEmpty(p.ReleaseNotes))
            item["ReleaseNotes"] = new AttributeValue(p.ReleaseNotes);

        return item;
    }

    internal static Package FromItemInternal(Dictionary<string, AttributeValue> item) => FromItem(item);

    private static Package FromItem(Dictionary<string, AttributeValue> item)
    {
        var deps = JsonSerializer.Deserialize<List<JsonElement>>(
            item.TryGetValue("Dependencies", out var d) ? d.S : "[]");

        var pkgTypes = JsonSerializer.Deserialize<List<JsonElement>>(
            item.TryGetValue("PackageTypes", out var pt) ? pt.S : "[]");

        var frameworks = JsonSerializer.Deserialize<List<string>>(
            item.TryGetValue("TargetFrameworks", out var tf) ? tf.S : "[]");

        var authors = item.TryGetValue("Authors", out var a) ? a.SS.Where(s => !string.IsNullOrEmpty(s)).ToArray() : [];
        var tags = item.TryGetValue("Tags", out var t2) ? t2.SS.Where(s => !string.IsNullOrEmpty(s)).ToArray() : [];

        var pkg = new Package
        {
            Id = item.TryGetValue("Id", out var id) ? id.S : string.Empty,
            NormalizedVersionString = item.TryGetValue("NormalizedVersion", out var nv) ? nv.S : string.Empty,
            OriginalVersionString = item.TryGetValue("OriginalVersion", out var ov) ? ov.S : null,
            Description = item.TryGetValue("Description", out var desc) ? desc.S : string.Empty,
            Downloads = item.TryGetValue("Downloads", out var dl) ? long.Parse(dl.N) : 0,
            HasReadme = item.TryGetValue("HasReadme", out var hr) && hr.BOOL,
            HasEmbeddedIcon = item.TryGetValue("HasEmbeddedIcon", out var hei) && hei.BOOL,
            IsPrerelease = item.TryGetValue("IsPrerelease", out var ip) && ip.BOOL,
            Listed = !item.TryGetValue("Listed", out var listed) || listed.BOOL,
            Published = item.TryGetValue("Published", out var pub)
                ? DateTime.Parse(pub.S, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTime.UtcNow,
            RequireLicenseAcceptance = item.TryGetValue("RequireLicenseAcceptance", out var rla) && rla.BOOL,
            SemVerLevel = item.TryGetValue("SemVerLevel", out var svl)
                ? (SemVerLevel)int.Parse(svl.N)
                : SemVerLevel.Unknown,
            Summary = item.TryGetValue("Summary", out var sum) ? sum.S : string.Empty,
            Title = item.TryGetValue("Title", out var title) ? title.S : string.Empty,
            IconUrl = item.TryGetValue("IconUrl", out var icon) && !string.IsNullOrEmpty(icon.S) ? new Uri(icon.S) : null,
            LicenseUrl = item.TryGetValue("LicenseUrl", out var lic) && !string.IsNullOrEmpty(lic.S) ? new Uri(lic.S) : null,
            ProjectUrl = item.TryGetValue("ProjectUrl", out var proj) && !string.IsNullOrEmpty(proj.S) ? new Uri(proj.S) : null,
            RepositoryUrl = item.TryGetValue("RepositoryUrl", out var repo) && !string.IsNullOrEmpty(repo.S) ? new Uri(repo.S) : null,
            RepositoryType = item.TryGetValue("RepositoryType", out var repoType) ? repoType.S : null,
            Language = item.TryGetValue("Language", out var lang) ? lang.S : null,
            MinClientVersion = item.TryGetValue("MinClientVersion", out var mcv) ? mcv.S : null,
            CachedFrom = item.TryGetValue("CachedFrom", out var cf) ? cf.S : null,
            ReleaseNotes = item.TryGetValue("ReleaseNotes", out var rn) ? rn.S : null,
            Authors = authors,
            Tags = tags,
            Dependencies = deps?
                .Select(e => new PackageDependency
                {
                    Id = e.TryGetProperty("Id", out var did) ? did.GetString() : null,
                    VersionRange = e.TryGetProperty("VersionRange", out var vr) ? vr.GetString() : null,
                    TargetFramework = e.TryGetProperty("TargetFramework", out var dtf) ? dtf.GetString() : null
                })
                .ToList() ?? [],
            PackageTypes = pkgTypes?
                .Select(e => new PackageType
                {
                    Name = e.TryGetProperty("Name", out var ptn) ? ptn.GetString() : null,
                    Version = e.TryGetProperty("Version", out var ptv) ? ptv.GetString() : null
                })
                .ToList() ?? [],
            TargetFrameworks = frameworks?
                .Select(m => new TargetFramework { Moniker = m })
                .ToList() ?? []
        };

        return pkg;
    }

    private async Task<bool> SetListedAsync(string id, NuGetVersion version, bool listed, CancellationToken cancellationToken)
    {
        try
        {
            await _client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = MakeKey(id, version),
                UpdateExpression = "SET Listed = :v",
                ConditionExpression = "attribute_exists(#pk)",
                ExpressionAttributeNames = new Dictionary<string, string> { { "#pk", Pk } },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":v", new AttributeValue { BOOL = listed } }
                }
            }, cancellationToken);

            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }
}

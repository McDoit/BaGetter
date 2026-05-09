namespace BaGetter.Database.DynamoDb;

/// <summary>
/// Options for the DynamoDB package database provider.
/// Bind from the <c>Database</c> configuration section.
/// </summary>
public class DynamoDbDatabaseOptions
{
    /// <summary>
    /// The DynamoDB table name used to store package metadata.
    /// Defaults to <c>"bagetter-packages"</c>.
    /// </summary>
    public string TableName { get; set; } = "bagetter-packages";

    /// <summary>
    /// Optional DynamoDB service URL, e.g. <c>http://localhost:8000</c> for DynamoDB Local.
    /// When not set the SDK uses the standard regional endpoint.
    /// </summary>
    public string ServiceUrl { get; set; }

    /// <summary>
    /// Optional AWS region name, e.g. <c>"eu-north-1"</c>.
    /// When not set the SDK falls back to the <c>AWS_REGION</c> environment variable
    /// or the instance metadata service.
    /// </summary>
    public string Region { get; set; }
}

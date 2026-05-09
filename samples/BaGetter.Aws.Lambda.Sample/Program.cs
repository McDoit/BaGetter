using System;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using BaGetter;
using BaGetter.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

var builder = WebApplication.CreateBuilder(args);

// Pre-configure AWS defaults that can be overridden via environment variables or appsettings.
builder.Configuration.AddInMemoryCollection(
[
    new("Storage:Type",   "AwsS3"),
    new("Database:Type",  "AwsDynamoDb"),
    new("Search:Type",    "AwsDynamoDb"),
]);

// When running inside Lambda, register the Lambda hosting integration.
// The env var AWS_LAMBDA_FUNCTION_NAME is set automatically by the Lambda runtime.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME")))
{
    builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
}

// Register BaGetter services — S3 storage, DynamoDB database, and DynamoDB search.
builder.Services.AddBaGetterWebApplication(bagetter =>
{
    bagetter.AddAwsS3Storage();
    bagetter.AddDynamoDbDatabase();
});

var app = builder.Build();

if (builder.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Add BaGetter's NuGet endpoints.
new BaGetterEndpointBuilder().MapEndpoints(app);

// Run EF migrations if configured (no-op for DynamoDB; table is created by DynamoDbTableInitializer).
await app.RunMigrationsAsync();

await app.RunAsync();

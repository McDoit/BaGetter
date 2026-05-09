# BaGetter AWS Lambda Sample

This sample shows how to run **BaGetter** as an **AWS Lambda** function backed by
**Amazon S3** (blob storage) and **Amazon DynamoDB** (package metadata + search).

---

## Required AWS Resources

### S3 Bucket

Create a bucket in the region of your choice and update `appsettings.json` (or set the
`Storage__Bucket` and `Storage__Region` environment variables):

```json
{
  "Storage": { "Type": "AwsS3", "Bucket": "my-nuget-packages", "Region": "eu-north-1" }
}
```

### DynamoDB Table

The sample **auto-creates the table and GSI on startup** via `DynamoDbTableInitializer`.
No manual table creation is required. If you prefer to create it manually:

| Property          | Value                       |
|-------------------|-----------------------------|
| Table name        | `bagetter-packages`         |
| Billing mode      | PAY_PER_REQUEST             |
| Partition key     | `PK` (String)               |
| Sort key          | `SK` (String)               |
| GSI name          | `SearchIndex`               |
| GSI partition key | `SearchPartition` (String)  |
| GSI sort key      | `LowerId` (String)          |

---

## IAM Permissions

The Lambda execution role needs the following permissions:

**S3**
```json
{
  "Effect": "Allow",
  "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
  "Resource": "arn:aws:s3:::my-nuget-packages/*"
}
```

**DynamoDB**
```json
{
  "Effect": "Allow",
  "Action": [
    "dynamodb:GetItem",
    "dynamodb:PutItem",
    "dynamodb:UpdateItem",
    "dynamodb:DeleteItem",
    "dynamodb:Query",
    "dynamodb:Scan",
    "dynamodb:DescribeTable",
    "dynamodb:CreateTable"
  ],
  "Resource": [
    "arn:aws:dynamodb:*:*:table/bagetter-packages",
    "arn:aws:dynamodb:*:*:table/bagetter-packages/index/SearchIndex"
  ]
}
```

---

## How to Publish

### Prerequisites

```bash
dotnet tool install -g Amazon.Lambda.Tools
```

### Deploy

```bash
cd samples/BaGetter.Aws.Lambda.Sample

dotnet lambda deploy-function BaGetterNuGet \
  --function-runtime dotnet9 \
  --function-architecture arm64 \
  --function-memory-size 512 \
  --function-timeout 30 \
  --environment-variables "Storage__Bucket=my-nuget-packages;Storage__Region=eu-north-1;Database__Region=eu-north-1"
```

Enable **SnapStart** on the Lambda function to reduce cold-start latency (from ~1 s to ~300 ms):

```bash
aws lambda put-function-code-signing-config ...  # (optional)
aws lambda put-function-concurrency ...           # (optional warm pool)
aws lambda update-function-configuration \
  --function-name BaGetterNuGet \
  --snap-start ApplyOn=PublishedVersions
aws lambda publish-version --function-name BaGetterNuGet
```

---

## How to Expose

### Lambda Function URL (simplest)

```bash
aws lambda create-function-url-config \
  --function-name BaGetterNuGet \
  --auth-type NONE
```

Point your NuGet client at the Function URL:

```
https://<function-url-id>.lambda-url.<region>.on.aws/v3/index.json
```

### API Gateway HTTP API

Create an HTTP API in the AWS Console or with the AWS CLI and configure it with a
`$default` stage pointing at the Lambda integration. BaGetter works with **HTTP API**
(payload format version 2.0, which is what `LambdaEventSource.HttpApi` expects).

---

## API Key & Secrets

The sample reads `ApiKey` from `appsettings.json` or environment variables for simplicity.
In production, load the API key from **AWS Secrets Manager** or **SSM Parameter Store**
using the `Amazon.Extensions.Configuration.SystemsManager` package:

```csharp
builder.Configuration.AddSystemsManager("/bagetter/");
```

See the [AWS .NET Configuration Extensions](https://github.com/aws/aws-dotnet-extensions-configuration)
for more information.

---

## Running Locally (Kestrel)

```bash
cd samples/BaGetter.Aws.Lambda.Sample
dotnet run
```

When `AWS_LAMBDA_FUNCTION_NAME` is **not** set, the app starts as a normal Kestrel
server on `http://localhost:5000`.  
Set the required env vars or edit `appsettings.json` to point at a real S3 bucket / DynamoDB
table (or use [DynamoDB Local](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/DynamoDBLocal.html)
with `Database__ServiceUrl=http://localhost:8000`).

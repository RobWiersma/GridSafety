using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.Lambda;
using Amazon.S3;
using GridSafety.Api;
using GridSafety.Core;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// In Lambda this swaps Kestrel for the API Gateway HTTP API (payload v2) adapter.
// Outside Lambda it's a no-op, so `dotnet run` gives you a normal local web server.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();
builder.Services.Configure<ApiOptions>(config.GetSection("GridSafety"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICircuitCatalog>(StaticCircuitCatalog.Instance);

if ((config["GridSafety:Storage"] ?? "DynamoDb").Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IGridRepository, InMemoryGridRepository>();
}
else
{
    builder.Services.AddSingleton<IAmazonDynamoDB>(_ =>
        config["GridSafety:DynamoDbServiceUrl"] is { Length: > 0 } url
            ? new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = url })
            : new AmazonDynamoDBClient());
    builder.Services.AddSingleton<IGridRepository>(sp =>
        new DynamoGridRepository(sp.GetRequiredService<IAmazonDynamoDB>(), config["GridSafety:TableName"]!));
    if (config.GetValue<bool>("GridSafety:CreateTableOnStartup"))
    {
        builder.Services.AddHostedService<LocalTableBootstrapper>();
    }
}

// Only used by the dev ingest endpoint. In AWS, drones publish to IoT Core and the
// DroneIngest Lambda uses an EventBridge publisher instead.
builder.Services.AddSingleton<InMemoryEventPublisher>();
builder.Services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventPublisher>());
builder.Services.AddSingleton<DroneIngestService>();

// On-demand demo flights (the console's "Demo flight" button). In AWS the API invokes the DemoFlight
// Lambda, which publishes through IoT Core; locally the flight runs in-process. Nothing runs on a schedule.
if (config["GridSafety:DemoFunctionName"] is { Length: > 0 } demoFunction)
    builder.Services.AddSingleton<IDemoFlightLauncher>(_ => new LambdaDemoFlightLauncher(new AmazonLambdaClient(), demoFunction));
else if (config.GetValue<bool>("GridSafety:AllowDevIngest"))
    builder.Services.AddSingleton<IDemoFlightLauncher, InProcessDemoFlightLauncher>();
else
    builder.Services.AddSingleton<IDemoFlightLauncher>(DisabledDemoFlightLauncher.Instance);

// Created on first use: only the report endpoint needs S3.
builder.Services.AddSingleton(new S3Presigner(() => new AmazonS3Client()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (config.GetValue<bool>("GridSafety:AllowDevUser"))
{
    // Local development only: impersonate a user with X-Dev-User / X-Dev-Role headers instead of Cognito.
    app.UseMiddleware<DevUserMiddleware>();
}

app.MapGridSafetyApi(devIngest: config.GetValue<bool>("GridSafety:AllowDevIngest"));
app.Run();

public partial class Program;

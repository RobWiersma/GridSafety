using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.Lambda;
using Amazon.S3;
using Amazon.SimpleNotificationService;
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
    builder.Services.AddSingleton<IVisitStore, InMemoryVisitStore>();
    builder.Services.AddSingleton<IResourceStore, InMemoryResourceStore>();
    builder.Services.AddSingleton<IAlertSettingsStore, InMemoryAlertSettingsStore>();
    builder.Services.AddSingleton<ISimulationSettingsStore, InMemorySimulationSettingsStore>();
    builder.Services.AddSingleton<IAerialStore, InMemoryAerialStore>();
}
else
{
    builder.Services.AddSingleton<IAmazonDynamoDB>(_ =>
        config["GridSafety:DynamoDbServiceUrl"] is { Length: > 0 } url
            ? new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = url })
            : new AmazonDynamoDBClient());
    builder.Services.AddSingleton<IGridRepository>(sp =>
        new DynamoGridRepository(sp.GetRequiredService<IAmazonDynamoDB>(), config["GridSafety:TableName"]!));
    builder.Services.AddSingleton<IVisitStore>(sp =>
        new DynamoVisitStore(sp.GetRequiredService<IAmazonDynamoDB>(), config["GridSafety:TableName"]!));
    builder.Services.AddSingleton<IResourceStore>(sp =>
        new DynamoResourceStore(sp.GetRequiredService<IAmazonDynamoDB>(), config["GridSafety:TableName"]!));
    builder.Services.AddSingleton<IAlertSettingsStore>(sp =>
        new DynamoAlertSettingsStore(sp.GetRequiredService<IAmazonDynamoDB>(), config["GridSafety:TableName"]!));
    builder.Services.AddSingleton<ISimulationSettingsStore>(sp =>
        new DynamoSimulationSettingsStore(sp.GetRequiredService<IAmazonDynamoDB>(), config["GridSafety:TableName"]!));
    builder.Services.AddSingleton<IAerialStore>(sp =>
        new DynamoAerialStore(sp.GetRequiredService<IAmazonDynamoDB>(), config["GridSafety:TableName"]!));
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

// Aerial photos of structures for finding captures (public-domain NAIP imagery, fetched once per structure and cached).
// In AWS a Lambda fetches in the background; locally it runs in-process. "GridSafety:AerialImagery": "Off" disables it.
builder.Services.AddHttpClient<IAerialSource, UsgsNaipSource>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(80);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("GridSafety-demo/1.0");
});
if (config["GridSafety:AerialFunctionName"] is { Length: > 0 } aerialFunction)
    builder.Services.AddSingleton<IAerialLauncher>(_ => new LambdaAerialLauncher(new AmazonLambdaClient(), aerialFunction));
else if (!string.Equals(config["GridSafety:AerialImagery"], "Off", StringComparison.OrdinalIgnoreCase) && config.GetValue<bool>("GridSafety:AllowDevIngest"))
    builder.Services.AddSingleton<IAerialLauncher, InProcessAerialLauncher>();
else
    builder.Services.AddSingleton<IAerialLauncher>(DisabledAerialLauncher.Instance);

// Road routes for simulated field crews (OSRM over OpenStreetMap roads), fetched once when a work order is assigned.
// "GridSafety:Routing": "Off" makes trucks drive in straight lines; "GridSafety:RoutingUrl" points at another OSRM server.
if (string.Equals(config["GridSafety:Routing"], "Off", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IRouteSource>(StraightLineRouteSource.Instance);
else
    builder.Services.AddHttpClient<IRouteSource, OsrmRouteSource>(c =>
    {
        c.BaseAddress = new Uri(config["GridSafety:RoutingUrl"] is { Length: > 0 } u ? u : OsrmRouteSource.DefaultBaseUrl);
        c.Timeout = TimeSpan.FromSeconds(5); // the API Lambda has 15 s in all
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GridSafety-demo/1.0");
    });

// Email alert recipients are SNS email subscriptions in AWS; locally nothing is emailed.
if (config["GridSafety:AlertTopicArn"] is { Length: > 0 } alertTopic)
    builder.Services.AddSingleton<IAlertRecipients>(_ => new SnsAlertRecipients(new AmazonSimpleNotificationServiceClient(), alertTopic));
else
    builder.Services.AddSingleton<IAlertRecipients, LocalAlertRecipients>();

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

using AutoMmo.Worker;
using AutoMmo.Worker.Config;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new WorkerConfig());
builder.Services.AddSingleton<IMetricFactory>(Metrics.DefaultFactory);
builder.Services.AddSingleton<MmoMetrics>();
builder.Services.AddHostedService<BackgroundWorker>();

var app = builder.Build();

// initialize metrics
_ = app.Services.GetRequiredService<MmoMetrics>();
app.MapGet("/enable", (WorkerConfig config) => config.IsEnabled = true);
app.MapGet("/disable", (WorkerConfig config) => config.IsEnabled = false);
app.MapMetrics();

app.Run();
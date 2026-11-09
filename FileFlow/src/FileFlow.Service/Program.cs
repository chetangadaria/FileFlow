using FileFlow.Core;
using FileFlow.Service;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "FileFlowService");
builder.Services.AddSingleton<ConfigurationStore>();
builder.Services.AddSingleton<HealthStore>();
builder.Services.AddSingleton<OneShotStore>();
builder.Services.AddSingleton<DailyJournal>();
builder.Services.AddSingleton<TransferEngine>();
builder.Services.AddSingleton<SnmpV2Traps>();
builder.Services.AddHostedService<FileFlowWorker>();
await builder.Build().RunAsync();

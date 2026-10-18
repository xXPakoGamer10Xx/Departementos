using Habita.Infrastructure;
using Habita.Worker;

var builder = Host.CreateApplicationBuilder(args);
// Same data layer and services as the API (same connection string, SMTP and VAPID settings); no HTTP server.
builder.Services.AddInfrastructure(builder.Configuration, withIdentityServices: false); // nobody signs in here
builder.Services.AddHostedService<Worker>();
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(); // structured logs in production, readable ones while developing

var host = builder.Build();
host.Run();

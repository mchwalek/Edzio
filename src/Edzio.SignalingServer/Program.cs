using Edzio.SignalingServer.Hubs;
using Edzio.SignalingServer.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddSingleton<IPairingCodeService, PairingCodeService>();

var app = builder.Build();

app.MapHub<SignalingHub>("/signaling");
app.MapGet("/health", () => "ok");

app.Run();

public partial class Program { }

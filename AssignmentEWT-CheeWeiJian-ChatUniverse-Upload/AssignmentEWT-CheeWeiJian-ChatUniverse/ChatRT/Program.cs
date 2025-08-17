var builder = WebApplication.CreateBuilder(args);
// Set maximum receive message size (default 32KB)
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = null;
});

var app = builder.Build();
app.UseFileServer();
app.MapHub<ChatHub>("/hub");

// 添加用户名检查API
app.MapGet("/api/users/check", (string name) =>
{
    var exists = ChatHub.IsUserOnline(name);
    return Results.Json(new { exists });
});

app.Run();

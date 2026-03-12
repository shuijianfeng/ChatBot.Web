using ChatBot.Controllers;
using ChatBot.Models;
using ChatBot.Web.Services;
using Microsoft.AspNetCore.ResponseCompression;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);


// 绑定 ChatModels 配置
builder.Services.Configure<ChatModelSettings>(builder.Configuration.GetSection("ChatModels"));
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ChatModelConfig>();

builder.Services.AddScoped<IChatService, ChatService>();

// Skills 文件夹加载服务
builder.Services.AddSingleton<SkillLoaderService>();

// MCP 客户端管理器
builder.Services.Configure<McpSettings>(builder.Configuration.GetSection("McpSettings"));
builder.Services.AddSingleton<IMcpClientManager, McpClientManager>();

// PostgreSQL 连接池化（NpgsqlDataSource，Npgsql 7+ 推荐）
var pgConnectionString = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? "Host=localhost;Database=cloudserver;Username=postgres;Password=1234;Port=5432";
builder.Services.AddSingleton(NpgsqlDataSource.Create(pgConnectionString));

builder.Services.AddScoped<JinaSearch>();
builder.Services.AddScoped<OpenWeather>();
builder.Services.AddScoped<ChatSessionRepository>();

// 流式消息缓存服务（用于断线重连）
builder.Services.AddSingleton<StreamCacheService>();

// 响应压缩（Brotli + Gzip）
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});


var app = builder.Build();


if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseResponseCompression();
app.UseHttpsRedirection();
app.MapStaticAssets();
app.UseRouting();
app.MapControllers();
app.MapDefaultControllerRoute();
app.Run();

using ChatBot.Controllers;
using ChatBot.Models;
using ChatBot.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

var builder = WebApplication.CreateBuilder(args);


// 绑定 ChatModels 配置
builder.Services.Configure<ChatModelSettings>(builder.Configuration.GetSection("ChatModels"));
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ChatModelConfig>();

builder.Services.AddScoped<IChatService, ChatService>();

// MCP 客户端管理器
builder.Services.Configure<McpSettings>(builder.Configuration.GetSection("McpSettings"));
builder.Services.AddSingleton<IMcpClientManager, McpClientManager>();

builder.Services.AddScoped<OpenAIService>();
builder.Services.AddScoped<JinaSearch>();
builder.Services.AddScoped<OpenWeather>();
builder.Services.AddScoped<ChatSessionRepository>();


var app = builder.Build();


if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
//app.MapStaticAssets();
app.UseRouting();
app.MapControllers();
app.MapDefaultControllerRoute();
app.Run();

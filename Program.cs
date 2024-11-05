using ChatBot.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

// 获取当前工作目录
var currentDirectory = Directory.GetCurrentDirectory();

// 定义密钥存储目录路径
var keysDirectory = Path.Combine(currentDirectory, "keys");

// 确保密钥存储目录存在
if (!Directory.Exists(keysDirectory))
{
    Directory.CreateDirectory(keysDirectory);
}

// 配置数据保护，持久化密钥到当前路径下的 "keys" 目录，并使用机器级别 DPAPI 加密密钥
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .ProtectKeysWithDpapi(protectToLocalMachine: true) // 使用机器级别加密
    .SetApplicationName("ChatBot.Web");

builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IChatService, QianWenChatService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapDefaultControllerRoute();
app.Run();

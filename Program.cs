using ChatBot.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

// ��ȡ��ǰ����Ŀ¼
var currentDirectory = Directory.GetCurrentDirectory();

// ������Կ�洢Ŀ¼·��
var keysDirectory = Path.Combine(currentDirectory, "keys");

// ȷ����Կ�洢Ŀ¼����
if (!Directory.Exists(keysDirectory))
{
    Directory.CreateDirectory(keysDirectory);
}

// �������ݱ������־û���Կ����ǰ·���µ� "keys" Ŀ¼����ʹ�û������� DPAPI ������Կ
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .ProtectKeysWithDpapi(protectToLocalMachine: true) // ʹ�û����������
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

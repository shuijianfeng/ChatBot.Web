using ChatBot.Models;
using ChatBot.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

var builder = WebApplication.CreateBuilder(args);


// ∞Û∂® ChatModels ≈‰÷√
builder.Services.Configure<ChatModelSettings>(builder.Configuration.GetSection("ChatModels"));
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IChatService,ChatService> ();
builder.Services.AddScoped<CustomFontResolver>();

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

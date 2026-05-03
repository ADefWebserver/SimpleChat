using Radzen;
using SimpleChat.Components;
using SimpleChat.Models;
using SimpleChat.Services.AI;

namespace SimpleChat;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // Optional writable overlay for production (created by AIConfigurationService at save time).
        var userOverlay = Path.Combine(builder.Environment.ContentRootPath, "appsettings.User.json");
        builder.Configuration.AddJsonFile(userOverlay, optional: true, reloadOnChange: true);

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddRadzenComponents();

        // AI options + services
        builder.Services.AddOptions<AIOptions>()
            .Bind(builder.Configuration.GetSection(AIOptions.SectionName));

        builder.Services.AddSingleton<AIConfigurationService>();
        builder.Services.AddSingleton<ChatClientFactory>();
        builder.Services.AddHttpClient();
        builder.Services.AddHttpClient<AIModelService>();
        builder.Services.AddScoped<ChatService>();

        var app = builder.Build();

        app.MapDefaultEndpoints();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Run();
    }
}

using Microsoft.EntityFrameworkCore;
using WbsApp.Data;
using WbsApp.Infrastructure.Startup;
using WbsApp.Infrastructure.Logging;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.ModelBindingMessageProvider.SetAttemptedValueIsInvalidAccessor((value, field) => $"{field}の入力形式を確認してください。");
    options.ModelBindingMessageProvider.SetValueIsInvalidAccessor(value => "入力値が正しくありません。入力内容を確認してください。");
    options.ModelBindingMessageProvider.SetUnknownValueIsInvalidAccessor(field => $"{field}の入力形式を確認してください。");
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WbsApp.Infrastructure.Clock.AppClock>();
builder.Services.AddSingleton<ILoggerProvider, DailyFileLoggerProvider>();
builder.Services.AddSingleton<DatabaseLocation>();
builder.Services.AddDbContext<AppDbContext>((services, options) =>
    options.UseSqlite(services.GetRequiredService<DatabaseLocation>().ConnectionString));
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<WbsApp.Services.ProjectService>();
builder.Services.AddScoped<WbsApp.Services.TaskService>();

var app = builder.Build();

try
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>()
        .InitializeAsync(app.Lifetime.ApplicationStopping);
}
catch (Exception exception)
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WbsApp.Startup")
        .LogCritical(exception, "起動を中止しました。エラーID: {ErrorId}", Guid.NewGuid().ToString("N"));
    throw;
}

app.UseExceptionHandler("/Home/Error");
app.UseStatusCodePagesWithReExecute("/Home/Status/{0}");
app.UseStaticFiles();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

public partial class Program;

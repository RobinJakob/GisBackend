var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<GisBackendApi.Services.GisProcessingService>();

// --- NEU: Background Service registrieren ---
builder.Services.AddHostedService<GisBackendApi.Services.GisBackgroundService>();
// ------------------------------------------

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();
app.Run();
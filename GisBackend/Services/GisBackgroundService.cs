using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System;

namespace GisBackendApi.Services
{
    public class GisBackgroundService : BackgroundService
    {
        // Wir nutzen hier IServiceProvider, um Scopes zu erzeugen, falls nötig, 
        // oder direkt den Singleton Service.
        private readonly GisProcessingService _gisService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<GisBackgroundService> _logger;

        public GisBackgroundService(
            GisProcessingService gisService,
            IWebHostEnvironment env,
            ILogger<GisBackgroundService> logger)
        {
            _gisService = gisService;
            _env = env;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Kurze Wartezeit beim Start, damit der Server erst sauber hochfahren kann,
            // bevor wir die CPU belasten.
            await Task.Delay(2000, stoppingToken);

            _logger.LogInformation("🌳 GIS Background Service gestartet. Prüfe auf neue Daten...");

            // Pfade ermitteln
            string dataPath = Path.Combine(_env.ContentRootPath, "Data");
            string webRootPath = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");

            try
            {
                // Die Berechnung anstoßen
                // Da _gisService ein Singleton ist, können wir das direkt aufrufen.
                var generatedFiles = await _gisService.GenerateStaticFiles(dataPath, webRootPath);

                _logger.LogInformation($"✅ GIS Generierung abgeschlossen. {generatedFiles.Count} Layer aktualisiert.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Kritischer Fehler bei der GIS-Hintergrundverarbeitung.");
            }
        }
    }
}
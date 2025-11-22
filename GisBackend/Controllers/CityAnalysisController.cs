using Microsoft.AspNetCore.Mvc;
using GisBackendApi.Services;
using GisBackendApi.Models;
using System;

namespace GisBackendApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CityAnalysisController : ControllerBase
    {
        private readonly GisProcessingService _gisService;
        private readonly IWebHostEnvironment _env;

        public CityAnalysisController(GisProcessingService gisService, IWebHostEnvironment env)
        {
            _gisService = gisService;
            _env = env;
        }

        // DER Universal-Endpunkt für alle Layer
        // Aufruf z.B.: GET /api/CityAnalysis/layer/Building
        // Oder: GET /api/CityAnalysis/layer/PotentialPlanting (dauert länger)
        [HttpGet("layer/{categoryName}")]
        public IActionResult GetLayer(string categoryName)
        {
            // String zu Enum konvertieren (Case-Insensitive)
            if (!Enum.TryParse<ZoneCategory>(categoryName, true, out var category))
            {
                return BadRequest($"Ungültige Kategorie: {categoryName}. Verfügbar: Building, FireLane, Infrastructure, ExistingTree, TreeProtectionZone, Forest, Grave, SportField, Restricted, SemiSealed, PotentialPlanting, PublicSpace");
            }

            string dataPath = Path.Combine(_env.ContentRootPath, "Data");

            try
            {
                var zones = _gisService.GetLayerByCategory(dataPath, category);
                return Ok(ToGeoJson(zones));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpGet("heatmap")]
        public IActionResult GetHeatmap()
        {
            string dataPath = Path.Combine(_env.ContentRootPath, "Data");
            var heatmap = _gisService.GetHeatmap(dataPath);

            return Ok(new
            {
                type = "FeatureCollection",
                features = heatmap.Select(h => new
                {
                    type = "Feature",
                    properties = new { score = h.Score, treeCount = h.TreeCount, color = h.ColorHex },
                    geometry = h.Geometry
                })
            });
        }

        private object ToGeoJson(List<AnalyzedZone> zones)
        {
            return new
            {
                type = "FeatureCollection",
                features = zones.Select(z => new
                {
                    type = "Feature",
                    properties = z.GetProperties(),
                    geometry = z.Geometry
                })
            };
        }
    }
}
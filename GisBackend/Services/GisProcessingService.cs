using GisBackendApi.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Union;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using ProjNet.IO.CoordinateSystems;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization; // WICHTIG für JsonNumberHandling
using System.IO.Compression;
using System.Threading.Tasks;
using System;

namespace GisBackendApi.Services
{
    public class GisProcessingService
    {
        private readonly GeometryFactory _geometryFactory;
        private readonly ICoordinateTransformation _utmToWgs84;
        private readonly CoordinateTransformationFactory _ctFactory;
        private readonly CoordinateSystemFactory _csFactory;

        private bool _isDataLoaded = false;
        private readonly object _lock = new object();

        private List<AnalyzedZone> _allZonesCache = new List<AnalyzedZone>();
        private List<AnalyzedZone> _potentialGreenCache = null;
        private List<HeatmapCell> _heatmapCache = null;

        private Dictionary<ZoneCategory, List<Geometry>> _geometriesByCategory = new Dictionary<ZoneCategory, List<Geometry>>();
        private List<Geometry> _treeGeometries = new List<Geometry>();

        public GisProcessingService()
        {
            _geometryFactory = new GeometryFactory();
            _ctFactory = new CoordinateTransformationFactory();
            _csFactory = new CoordinateSystemFactory();

            // UTM 32N Definition
            string wktUtm32 = "PROJCS[\"ETRS89_UTM_Zone_32\",GEOGCS[\"GCS_ETRS89\",DATUM[\"D_ETRS89\",SPHEROID[\"GRS_1980\",6378137,298.257222101]],PRIMEM[\"Greenwich\",0],UNIT[\"Degree\",0.0174532925199432955]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"False_Easting\",500000],PARAMETER[\"False_Northing\",0],PARAMETER[\"Central_Meridian\",9],PARAMETER[\"Scale_Factor\",0.9996],PARAMETER[\"Latitude_Of_Origin\",0],UNIT[\"Meter\",1],AUTHORITY[\"EPSG\",25832]]";
            var sourceCs = _csFactory.CreateFromWkt(wktUtm32);
            var targetCs = GeographicCoordinateSystem.WGS84;
            _utmToWgs84 = _ctFactory.CreateFromCoordinateSystems(sourceCs, targetCs);
        }

        public async Task<List<string>> GenerateStaticFiles(string dataPath, string webRootPath)
        {
            var generatedFiles = new List<string>();
            string outputFolder = Path.Combine(webRootPath, "geojson");

            if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

            EnsureDataLoaded(dataPath);

            // --- FIX: Erlaube NaN und Infinity im JSON, um Abstürze zu verhindern ---
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            // ------------------------------------------------------------------------

            foreach (ZoneCategory category in Enum.GetValues(typeof(ZoneCategory)))
            {
                var zones = GetLayerByCategory(dataPath, category);
                if (zones == null || !zones.Any()) continue;

                var geoJson = new
                {
                    type = "FeatureCollection",
                    features = zones.Select(z => new
                    {
                        type = "Feature",
                        properties = z.GetProperties(),
                        geometry = z.Geometry
                    })
                };

                string fileName = $"layer_{category}.json";
                string filePath = Path.Combine(outputFolder, fileName);

                await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(geoJson, jsonOptions));
                generatedFiles.Add(fileName);
            }

            var heatmap = GetHeatmap(dataPath);
            if (heatmap != null && heatmap.Any())
            {
                var heatmapJson = new
                {
                    type = "FeatureCollection",
                    features = heatmap.Select(h => new
                    {
                        type = "Feature",
                        properties = new { score = h.Score, treeCount = h.TreeCount, color = h.ColorHex },
                        geometry = h.Geometry
                    })
                };

                string hmPath = Path.Combine(outputFolder, "layer_Heatmap.json");
                await File.WriteAllTextAsync(hmPath, JsonSerializer.Serialize(heatmapJson, jsonOptions));
                generatedFiles.Add("layer_Heatmap.json");
            }

            return generatedFiles;
        }

        public List<AnalyzedZone> GetLayerByCategory(string dataPath, ZoneCategory category)
        {
            EnsureDataLoaded(dataPath);
            if (category == ZoneCategory.PotentialPlanting) return GetPotentialGreenLayer();
            return _allZonesCache.Where(z => z.Category == category).ToList();
        }

        public List<HeatmapCell> GetHeatmap(string dataPath)
        {
            EnsureDataLoaded(dataPath);
            if (_heatmapCache == null)
            {
                lock (_lock)
                {
                    if (_heatmapCache == null && _treeGeometries.Any())
                    {
                        var bounds = _treeGeometries[0].EnvelopeInternal;
                        foreach (var t in _treeGeometries) bounds.ExpandToInclude(t.EnvelopeInternal);
                        _heatmapCache = GenerateHeatmap(_treeGeometries, bounds, 100.0);
                    }
                    else if (_heatmapCache == null) _heatmapCache = new List<HeatmapCell>();
                }
            }
            return _heatmapCache;
        }

        private void EnsureDataLoaded(string dataPath)
        {
            if (_isDataLoaded) return;
            lock (_lock)
            {
                if (_isDataLoaded) return;
                ExtractZips(dataPath);
                LoadAllShapefiles(dataPath);
                _isDataLoaded = true;
            }
        }

        private void LoadAllShapefiles(string dataPath)
        {
            string configPath = Path.Combine(dataPath, "layers.json");
            List<LayerConfig> layerConfigs = new List<LayerConfig>();
            if (File.Exists(configPath))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                try { layerConfigs = JsonSerializer.Deserialize<List<LayerConfig>>(File.ReadAllText(configPath), options); } catch { }
            }

            string[] shapeFiles = Directory.GetFiles(dataPath, "*.shp", SearchOption.AllDirectories);

            foreach (var file in shapeFiles)
            {
                var fileName = Path.GetFileName(file);
                var config = layerConfigs.FirstOrDefault(c => fileName.Contains(c.FilenamePattern, StringComparison.OrdinalIgnoreCase));

                if (config != null)
                {
                    try { ProcessFile(file, config); }
                    catch (Exception ex) { Console.WriteLine($"[Error] {fileName}: {ex.Message}"); }
                }
            }
        }

        private void ProcessFile(string path, LayerConfig config)
        {
            using (var reader = new ShapefileDataReader(path, _geometryFactory))
            {
                while (reader.Read())
                {
                    var geo = reader.Geometry;
                    if (geo == null || !geo.IsValid) { if (geo != null) geo = geo.Buffer(0); if (geo == null || !geo.IsValid) continue; }

                    if (!_geometriesByCategory.ContainsKey(config.Category)) _geometriesByCategory[config.Category] = new List<Geometry>();

                    double buffer = config.BufferMeters;

                    if (config.Type == "Tree")
                    {
                        _treeGeometries.Add(geo);
                        double krone = 3.0;
                        try { var k = reader["KRONE_DM"]; if (k != null && double.TryParse(k.ToString(), out double d)) krone = d; } catch { }
                        if (krone <= 0) krone = 3.0;

                        var physTree = geo.Buffer(krone / 2.0);
                        AddToCache(physTree, ZoneCategory.ExistingTree, "Bestandsbaum");

                        var protectZone = geo.Buffer((krone / 2.0) + 2.5);
                        AddToCache(protectZone, ZoneCategory.TreeProtectionZone, "Wurzelschutz");
                    }
                    else
                    {
                        var bufferedGeo = geo.Buffer(buffer);
                        AddToCache(bufferedGeo, config.Category, config.Description);
                    }
                }
            }
        }

        private void AddToCache(Geometry geo, ZoneCategory cat, string desc)
        {
            var transformed = TransformToWgs84(geo);
            // Wenn Transformation fehlgeschlagen ist (leere Geometrie), nicht cachen
            if (transformed == null || transformed.IsEmpty) return;

            _allZonesCache.Add(new AnalyzedZone
            {
                Geometry = transformed,
                Category = cat,
                Description = desc,
                ColorCode = GetColor(cat)
            });
            if (!_geometriesByCategory.ContainsKey(cat)) _geometriesByCategory[cat] = new List<Geometry>();
            _geometriesByCategory[cat].Add(geo);
        }

        private List<AnalyzedZone> GetPotentialGreenLayer()
        {
            if (_potentialGreenCache != null) return _potentialGreenCache;

            lock (_lock)
            {
                if (_potentialGreenCache != null) return _potentialGreenCache;

                var obstacleGeometries = new List<Geometry>();
                foreach (var kvp in _geometriesByCategory)
                {
                    if (kvp.Key != ZoneCategory.PotentialPlanting && kvp.Key != ZoneCategory.PublicSpace)
                    {
                        obstacleGeometries.AddRange(kvp.Value);
                    }
                }

                Geometry allBlockers = GeometryFactory.Default.CreateGeometryCollection(null);
                if (obstacleGeometries.Any()) allBlockers = UnaryUnionOp.Union(obstacleGeometries);

                var cityBounds = allBlockers.Envelope.Buffer(20);
                var potentialArea = cityBounds.Difference(allBlockers);

                var results = new List<AnalyzedZone>();
                if (!potentialArea.IsEmpty)
                {
                    for (int i = 0; i < potentialArea.NumGeometries; i++)
                    {
                        var areaPart = potentialArea.GetGeometryN(i);
                        if (areaPart.Area > 15.0)
                        {
                            var transformed = TransformToWgs84(areaPart);
                            if (transformed != null && !transformed.IsEmpty)
                            {
                                results.Add(new AnalyzedZone
                                {
                                    Geometry = transformed,
                                    Category = ZoneCategory.PotentialPlanting,
                                    Description = $"Möglicher Standort ({areaPart.Area:F0} m²)",
                                    ColorCode = "#00FF00"
                                });
                            }
                        }
                    }
                }
                _potentialGreenCache = results;
                return results;
            }
        }

        private void ExtractZips(string dataPath)
        {
            foreach (var zipPath in Directory.GetFiles(dataPath, "*.zip"))
            {
                try
                {
                    string targetDir = Path.Combine(dataPath, Path.GetFileNameWithoutExtension(zipPath));
                    if (!Directory.Exists(targetDir)) ZipFile.ExtractToDirectory(zipPath, targetDir);
                }
                catch { }
            }
        }

        // --- FIX: Robustere Transformation ---
        private Geometry TransformToWgs84(Geometry geom)
        {
            if (!geom.IsValid) geom = geom.Buffer(0);
            var res = geom.Copy();
            try
            {
                res.Apply(new MathTransformFilter(_utmToWgs84.MathTransform));
                // Prüfen ob Koordinaten valide sind (kein NaN)
                if (HasInvalidCoordinates(res)) return GeometryFactory.Default.CreateGeometryCollection(null);
                return res;
            }
            catch
            {
                return GeometryFactory.Default.CreateGeometryCollection(null);
            }
        }

        private bool HasInvalidCoordinates(Geometry g)
        {
            foreach (var c in g.Coordinates)
            {
                if (double.IsNaN(c.X) || double.IsNaN(c.Y) || double.IsInfinity(c.X) || double.IsInfinity(c.Y)) return true;
            }
            return false;
        }
        // -------------------------------------

        private string GetColor(ZoneCategory cat)
        {
            switch (cat)
            {
                case ZoneCategory.ExistingTree: return "#8B0000";
                case ZoneCategory.TreeProtectionZone: return "#FF4500";
                case ZoneCategory.Building: return "#696969";
                case ZoneCategory.Infrastructure: return "#2F4F4F";
                case ZoneCategory.FireLane: return "#A9A9A9";
                case ZoneCategory.Grave: return "#483D8B";
                case ZoneCategory.Forest: return "#006400";
                case ZoneCategory.SportField: return "#800000";
                case ZoneCategory.SemiSealed: return "#FFD700";
                case ZoneCategory.Restricted: return "#DAA520";
                case ZoneCategory.PotentialPlanting: return "#00FF00";
                default: return "#000000";
            }
        }

        public List<HeatmapCell> GenerateHeatmap(List<Geometry> trees, Envelope bounds, double gridSize)
        {
            var cells = new List<HeatmapCell>();
            var treeIndex = new STRtree<Geometry>();
            foreach (var t in trees) treeIndex.Insert(t.EnvelopeInternal, t);
            treeIndex.Build();

            for (double x = bounds.MinX; x < bounds.MaxX; x += gridSize)
            {
                for (double y = bounds.MinY; y < bounds.MaxY; y += gridSize)
                {
                    var cellPoly = _geometryFactory.CreatePolygon(new Coordinate[] {
                        new Coordinate(x, y), new Coordinate(x + gridSize, y),
                        new Coordinate(x + gridSize, y + gridSize), new Coordinate(x, y + gridSize),
                        new Coordinate(x, y)
                    });
                    var candidates = treeIndex.Query(cellPoly.EnvelopeInternal);
                    int count = 0;
                    foreach (Geometry candidate in candidates) if (cellPoly.Intersects(candidate)) count++;
                    double score = count > 0 ? (1.0 / (1.0 + count)) : 1.0;

                    // Transformation auch hier absichern
                    var transGeo = TransformToWgs84(cellPoly);
                    if (score > 0.1 && !transGeo.IsEmpty)
                    {
                        cells.Add(new HeatmapCell { Geometry = transGeo, Score = score, TreeCount = count, ColorHex = GetHeatmapColor(score) });
                    }
                }
            }
            return cells;
        }

        private string GetHeatmapColor(double score)
        {
            if (score > 0.8) return "#FF000080";
            if (score > 0.4) return "#FFA50080";
            return "#00FF0080";
        }

        private class MathTransformFilter : ICoordinateSequenceFilter
        {
            private readonly MathTransform _transform;
            public MathTransformFilter(MathTransform transform) => _transform = transform;
            public bool Done => false;
            public bool GeometryChanged => true;
            public void Filter(CoordinateSequence seq, int i)
            {
                var res = _transform.Transform(new[] { seq.GetX(i), seq.GetY(i) });
                seq.SetX(i, res[0]);
                seq.SetY(i, res[1]);
            }
        }
    }
}
using NetTopologySuite.Geometries;

namespace GisBackendApi.Models
{
    public enum ZoneCategory
    {
        // --- ROT (Gesperrt) ---
        Building,
        FireLane,
        Infrastructure,
        ExistingTree,
        TreeProtectionZone,
        Forest,
        Grave,
        SportField, // <--- Dies hat gefehlt!

        // --- GELB (Bedingt möglich) ---
        Restricted,
        SemiSealed,

        // --- GRÜN (Ideal) ---
        PotentialPlanting,
        PublicSpace
    }

    public class AnalyzedZone
    {
        public Geometry Geometry { get; set; }
        public ZoneCategory Category { get; set; }
        public string Description { get; set; }
        public string ColorCode { get; set; }

        public object GetProperties() => new
        {
            category = Category.ToString(),
            description = Description,
            color = ColorCode
        };
    }

    public class HeatmapCell
    {
        public Geometry Geometry { get; set; }
        public double Score { get; set; }
        public int TreeCount { get; set; }
        public string ColorHex { get; set; }
    }

    // Config-Klasse für JSON (falls noch nicht separat vorhanden)
    public class LayerConfig
    {
        public string FilenamePattern { get; set; }
        public string Type { get; set; } // "Tree", "Obstacle", "PotentialArea"
        public ZoneCategory Category { get; set; }
        public double BufferMeters { get; set; }
        public string Description { get; set; }
    }
}
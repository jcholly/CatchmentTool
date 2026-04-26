using System.Globalization;
using System.IO;
using System.Text;
using CatchmentTool2.Network;
using CatchmentTool2.Pipeline;

namespace CatchmentTool2.Output;

public static class GeoJsonWriter
{
    public static void Write(string path, PipelineResult result, IReadOnlyList<Structure> structures)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"FeatureCollection\",\"features\":[");
        bool first = true;
        var structById = structures.ToDictionary(s => s.Id);
        foreach (var c in result.Catchments)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"type\":\"Feature\",\"properties\":");
            sb.Append('{');
            sb.Append($"\"structure_id\":{Json(c.StructureId)},");
            sb.Append($"\"area\":{c.Geometry.Area.ToString("0.##", CultureInfo.InvariantCulture)},");
            sb.Append($"\"thinness\":{c.Geometry.ThinnessRatio.ToString("0.####", CultureInfo.InvariantCulture)},");
            double total = Math.Max(1, c.TopoAssignedCells + c.FallbackAssignedCells);
            sb.Append($"\"topo_pct\":{(c.TopoAssignedCells / total * 100).ToString("0.#", CultureInfo.InvariantCulture)},");
            if (structById.TryGetValue(c.StructureId, out var s))
                sb.Append($"\"kind\":{Json(s.Kind.ToString())}");
            else
                sb.Append("\"kind\":null");
            sb.Append('}');
            sb.Append(",\"geometry\":");
            WritePolygon(sb, c.Geometry.Vertices);
            sb.Append('}');
        }
        sb.Append("]}");
        File.WriteAllText(path, sb.ToString());
    }

    private static void WritePolygon(StringBuilder sb, IReadOnlyList<Vec2> ring)
    {
        sb.Append("{\"type\":\"Polygon\",\"coordinates\":[[");
        for (int i = 0; i < ring.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(ring[i].X.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(',').Append(ring[i].Y.ToString("0.###", CultureInfo.InvariantCulture)).Append(']');
        }
        // close ring
        if (ring.Count > 0)
            sb.Append(',').Append('[').Append(ring[0].X.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(',').Append(ring[0].Y.ToString("0.###", CultureInfo.InvariantCulture)).Append(']');
        sb.Append("]]}");
    }

    private static string Json(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

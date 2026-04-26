using CatchmentTool2.Network;
using CatchmentTool2.Pipeline;

namespace CatchmentTool2.Output;

public static class CatchmentRenderer
{
    private static readonly (byte r, byte g, byte b)[] Palette =
    {
        (66, 135, 245), (245, 90, 66), (66, 245, 138), (245, 200, 66),
        (155, 89, 245), (66, 245, 230), (245, 66, 168), (110, 245, 66),
        (245, 165, 66), (66, 96, 245), (170, 66, 245), (66, 245, 96),
        (245, 245, 66), (245, 66, 96), (66, 230, 245), (200, 245, 66),
    };

    public static void Render(string path, PipelineResult result, IReadOnlyList<Structure> structures,
        PipeNetwork? network, int width = 1400, int height = 1000)
    {
        var img = new PngImage(width, height, (245, 245, 240));

        // Determine world bounds from boundary
        var b = Bounds.Of(result.Boundary);
        if (b.Width <= 0 || b.Height <= 0) { img.Save(path); return; }
        int margin = 30;
        double sx = (width - 2 * margin) / b.Width;
        double sy = (height - 2 * margin) / b.Height;
        double s = Math.Min(sx, sy);
        // Center
        double offX = margin + ((width - 2 * margin) - b.Width * s) / 2.0 - b.MinX * s;
        double offY = margin + ((height - 2 * margin) - b.Height * s) / 2.0 - b.MinY * s;
        // Y is flipped (image y grows downward)
        (int x, int y) W2P(Vec2 v) => (
            (int)Math.Round(v.X * s + offX),
            (int)Math.Round(height - (v.Y * s + offY))
        );

        // Site boundary fill (light gray)
        var bRing = result.Boundary.Select(W2P).ToList();
        img.FillPolygon(bRing, 230, 230, 220);

        // Catchments — filled with semi-transparent palette colors, outlined dark
        int colorIdx = 0;
        foreach (var c in result.Catchments)
        {
            var col = Palette[colorIdx++ % Palette.Length];
            var ring = c.Geometry.Vertices.Select(W2P).ToList();
            img.FillPolygon(ring, col.r, col.g, col.b, alpha: 130);
            img.DrawPolyline(ring,
                (byte)Math.Max(0, col.r - 80),
                (byte)Math.Max(0, col.g - 80),
                (byte)Math.Max(0, col.b - 80), thickness: 2, closed: true);
        }

        // Site boundary outline (thicker, dark)
        img.DrawPolyline(bRing, 60, 60, 60, thickness: 2, closed: true);

        // Pipes (gray)
        if (network != null)
        {
            var byId = structures.ToDictionary(st => st.Id);
            foreach (var pipe in network.Pipes)
            {
                if (!byId.TryGetValue(pipe.StartStructureId, out var a)) continue;
                if (!byId.TryGetValue(pipe.EndStructureId, out var bb)) continue;
                var p1 = W2P(a.Location); var p2 = W2P(bb.Location);
                img.DrawLine(p1.x, p1.y, p2.x, p2.y, 90, 90, 90, thickness: 1);
            }
        }

        // Structures: ponds = larger blue, inlets = smaller red
        foreach (var st in structures)
        {
            var p = W2P(st.Location);
            if (st.Kind == StructureKind.Pond)
                img.FillDisk(p.x, p.y, 6, 25, 80, 200);
            else
                img.FillDisk(p.x, p.y, 3, 200, 50, 50);
        }

        img.Save(path);
    }
}

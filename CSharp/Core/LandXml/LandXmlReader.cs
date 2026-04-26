using System.Globalization;
using System.Xml.Linq;
using CatchmentTool2.Network;
using CatchmentTool2.Surface;

namespace CatchmentTool2.LandXml;

public sealed record LandXmlData(Tin Tin, IReadOnlyList<Structure> Structures,
    PipeNetwork? PipeNetwork);

public static class LandXmlReader
{
    public static LandXmlData Read(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.None);
        var ns = doc.Root!.Name.Namespace;

        // --- Surface (TIN) ---
        var pntList = new List<TinVertex>();
        var pntIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var triangles = new List<TinTriangle>();

        // Pick the surface with the most faces (cleanup script tries to leave one).
        var surfaces = doc.Descendants(ns + "Surface").ToList();
        var surface = surfaces
            .OrderByDescending(s => s.Descendants(ns + "F").Count())
            .FirstOrDefault();
        if (surface != null)
        {
            foreach (var p in surface.Descendants(ns + "P"))
            {
                var id = p.Attribute("id")?.Value ?? pntList.Count.ToString();
                var parts = (p.Value ?? "").Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                // LandXML order: Y(N) X(E) Z
                double y = ParseD(parts[0]);
                double x = ParseD(parts[1]);
                double z = ParseD(parts[2]);
                pntIndex[id] = pntList.Count;
                pntList.Add(new TinVertex(x, y, z));
            }
            foreach (var f in surface.Descendants(ns + "F"))
            {
                var parts = (f.Value ?? "").Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                if (pntIndex.TryGetValue(parts[0], out int a) &&
                    pntIndex.TryGetValue(parts[1], out int b) &&
                    pntIndex.TryGetValue(parts[2], out int c))
                {
                    triangles.Add(new TinTriangle(a, b, c));
                }
            }
        }

        // --- Pipe network ---
        var structures = new List<Structure>();
        var pipes = new List<Pipe>();
        // (structure name, refPipe) -> invert elev
        var structInverts = new Dictionary<(string, string), double>();

        foreach (var network in doc.Descendants(ns + "PipeNetwork"))
        {
            foreach (var s in network.Descendants(ns + "Struct"))
            {
                var name = s.Attribute("name")?.Value ?? Guid.NewGuid().ToString();
                double? rim = TryParseAttr(s, "elevRim");
                StructureKind kind = StructureKind.Inlet;
                var stype = s.Attribute("structType")?.Value?.ToLowerInvariant() ?? "";
                if (stype.Contains("outfall") || stype.Contains("pond") || stype.Contains("outlet"))
                    kind = StructureKind.Pond;

                Vec2? loc = null;
                var center = s.Element(ns + "Center") ?? s.Element(ns + "CntrLine");
                if (center != null)
                {
                    var parts = (center.Value ?? "").Split((char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        // LandXML: Y X (Z)
                        double y = ParseD(parts[0]);
                        double x = ParseD(parts[1]);
                        loc = new Vec2(x, y);
                    }
                }
                if (loc == null) continue;
                structures.Add(new Structure(name, loc.Value, rim, kind, false));

                foreach (var inv in s.Elements(ns + "Invert"))
                {
                    var refPipe = inv.Attribute("refPipe")?.Value;
                    var elevStr = inv.Attribute("elev")?.Value;
                    if (refPipe != null && elevStr != null &&
                        double.TryParse(elevStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var iv))
                        structInverts[(name, refPipe)] = iv;
                }
            }
            foreach (var p in network.Descendants(ns + "Pipe"))
            {
                var id = p.Attribute("name")?.Value ?? Guid.NewGuid().ToString();
                var refStart = p.Attribute("refStart")?.Value ?? "";
                var refEnd = p.Attribute("refEnd")?.Value ?? "";
                if (string.IsNullOrEmpty(refStart) || string.IsNullOrEmpty(refEnd)) continue;
                double? sInv = TryParseAttr(p, "elevStart");
                double? eInv = TryParseAttr(p, "elevEnd");
                if (sInv == null && structInverts.TryGetValue((refStart, id), out var si)) sInv = si;
                if (eInv == null && structInverts.TryGetValue((refEnd, id), out var ei)) eInv = ei;
                pipes.Add(new Pipe(id, refStart, refEnd, sInv, eInv));
            }
        }

        var tin = new Tin(pntList, triangles);
        var classified = AutoClassify(structures, pipes);
        var network2 = pipes.Count > 0 ? new PipeNetwork(classified, pipes) : null;
        return new LandXmlData(tin, classified, network2);
    }

    private static List<Structure> AutoClassify(List<Structure> structures, List<Pipe> pipes)
    {
        // Per CLAUDE.md Tier 1 #1: every selected outlet must have its own catchment.
        // The simulator's default policy is "select every structure" — the user gets one
        // catchment per inlet AND per pond. If the caller wants pond-only roll-up they
        // can override UserSelected externally before running the pipeline.
        if (pipes.Count == 0)
            return structures.Select(s => s with { Kind = StructureKind.Pond, UserSelected = true }).ToList();

        var validIds = structures.Select(s => s.Id).ToHashSet();
        var hasDownstream = new HashSet<string>();
        foreach (var p in pipes)
            if (validIds.Contains(p.StartStructureId) && validIds.Contains(p.EndStructureId))
                hasDownstream.Add(p.StartStructureId);

        return structures.Select(s =>
        {
            var kind = s.Kind == StructureKind.Pond ? StructureKind.Pond
                : (hasDownstream.Contains(s.Id) ? StructureKind.Inlet : StructureKind.Pond);
            return s with { Kind = kind, UserSelected = true };
        }).ToList();
    }

    private static double ParseD(string s) =>
        double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static double? TryParseAttr(XElement e, string name)
    {
        var s = e.Attribute(name)?.Value;
        if (s == null) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

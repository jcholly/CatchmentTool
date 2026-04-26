namespace CatchmentTool2.Network;

public enum StructureKind { Inlet, Junction, Pond }

public sealed record Structure(string Id, Vec2 Location, double? RimElevation, StructureKind Kind, bool UserSelected);

public sealed record Pipe(string Id, string StartStructureId, string EndStructureId,
    double? StartInvert, double? EndInvert);

/// <summary>
/// Pipe network with directed graph for upstream traversal.
/// Direction is from StartStructureId → EndStructureId; if start invert &lt; end invert,
/// the edge is reversed (water flows downhill).
/// </summary>
public sealed class PipeNetwork
{
    public IReadOnlyDictionary<string, Structure> Structures { get; }
    public IReadOnlyList<Pipe> Pipes { get; }

    private readonly Dictionary<string, List<string>> _upstreamOf;
    private readonly Dictionary<string, List<string>> _downstreamOf;

    public PipeNetwork(IEnumerable<Structure> structures, IEnumerable<Pipe> pipes)
    {
        Structures = structures.ToDictionary(s => s.Id);
        Pipes = pipes.ToList();
        _upstreamOf = Structures.ToDictionary(s => s.Key, _ => new List<string>());
        _downstreamOf = Structures.ToDictionary(s => s.Key, _ => new List<string>());
        foreach (var p in Pipes)
        {
            if (!Structures.ContainsKey(p.StartStructureId) || !Structures.ContainsKey(p.EndStructureId))
                continue;
            var (up, down) = ResolveDirection(p);
            _upstreamOf[down].Add(up);
            _downstreamOf[up].Add(down);
        }
    }

    private (string upstream, string downstream) ResolveDirection(Pipe p)
    {
        // Use inverts if both available; else trust connectivity convention (start = upstream).
        if (p.StartInvert is double si && p.EndInvert is double ei)
        {
            if (si > ei) return (p.StartStructureId, p.EndStructureId);
            if (si < ei) return (p.EndStructureId, p.StartStructureId);
        }
        return (p.StartStructureId, p.EndStructureId);
    }

    public IEnumerable<string> StructuresUpstreamOf(string id, bool includeSelf = true)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            if (!seen.Add(s)) continue;
            foreach (var up in _upstreamOf[s]) stack.Push(up);
        }
        if (!includeSelf) seen.Remove(id);
        return seen;
    }

    public IReadOnlyList<string> Upstream(string id) => _upstreamOf[id];
    public IReadOnlyList<string> Downstream(string id) => _downstreamOf[id];

    /// <summary>Auto-classify: structures with no downstream pipes are ponds.</summary>
    public static StructureKind ClassifyByConnectivity(string structureId,
        Dictionary<string, List<string>> downstream) =>
        downstream[structureId].Count == 0 ? StructureKind.Pond : StructureKind.Inlet;
}

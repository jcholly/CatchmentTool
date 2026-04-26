using CatchmentTool2.Network;

namespace CatchmentTool2.Pipeline;

/// <summary>
/// For each user-selected pond, walk the pipe graph upstream and union all upstream
/// per-structure labels into the pond's label. Result: each selected pond carries the
/// full surface contributing area of every inlet that drains to it via pipes.
///
/// NOTE: Selected non-pond structures keep their own labels. If a user selects both a
/// pond and an upstream inlet, the inlet keeps its own label and is NOT subsumed into
/// the pond — preserves "one catchment per selected outlet" while letting users choose
/// the granularity.
/// </summary>
public static class Phase6_PipeUnion
{
    public static int[] UnionUpstream(int[] labels, Phase4_Route.RouteResult routed,
        IReadOnlyList<Structure> structures, PipeNetwork? network)
    {
        if (network == null) return labels;
        var idToLabel = routed.LabelToStructureId.ToDictionary(kv => kv.Value, kv => kv.Key);
        var selectedIds = structures.Where(s => s.UserSelected).Select(s => s.Id).ToHashSet();

        // For each selected pond, find upstream structures (in the graph) and remap their labels
        var remap = new Dictionary<int, int>();
        foreach (var pondId in structures.Where(s => s.UserSelected && s.Kind == StructureKind.Pond)
                                          .Select(s => s.Id))
        {
            if (!idToLabel.TryGetValue(pondId, out int pondLabel)) continue;
            foreach (var upId in network.StructuresUpstreamOf(pondId, includeSelf: false))
            {
                if (selectedIds.Contains(upId)) continue; // user wants it as a separate catchment
                if (!idToLabel.TryGetValue(upId, out int upLabel)) continue;
                remap[upLabel] = pondLabel;
            }
        }
        if (remap.Count == 0) return labels;

        var result = new int[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            int lab = labels[i];
            result[i] = remap.TryGetValue(lab, out int r) ? r : lab;
        }
        return result;
    }
}

namespace CatchmentTool2.Geometry;

/// <summary>
/// Trace boundaries of a labeled raster. For each label, return one polygon
/// (the largest connected component) traced along cell-corner coordinates.
/// </summary>
public static class MarchingSquares
{
    /// <summary>
    /// labels: i*rows + j-flat array of int labels (0 = nodata). cols/rows = grid size.
    /// originX/Y, cellSize: world-space placement.
    /// Returns label → cell-corner polygon (no closing duplicate).
    /// </summary>
    public static Dictionary<int, List<Vec2>> Trace(int[] labels, int cols, int rows,
        double originX, double originY, double cellSize)
    {
        var result = new Dictionary<int, List<Vec2>>();
        var distinctLabels = new HashSet<int>();
        foreach (var l in labels) if (l > 0) distinctLabels.Add(l);

        foreach (var label in distinctLabels)
        {
            var components = ExtractComponents(labels, cols, rows, label);
            if (components.Count == 0) continue;
            // Keep the largest component (in cells)
            var largest = components.OrderByDescending(c => c.Count).First();
            var ring = TraceRingFromComponent(largest, cols, rows, originX, originY, cellSize);
            if (ring.Count >= 4) result[label] = ring;
        }
        return result;
    }

    private static List<HashSet<int>> ExtractComponents(int[] labels, int cols, int rows, int label)
    {
        var visited = new bool[labels.Length];
        var components = new List<HashSet<int>>();
        for (int idx = 0; idx < labels.Length; idx++)
        {
            if (visited[idx] || labels[idx] != label) continue;
            var comp = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(idx);
            visited[idx] = true;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                comp.Add(c);
                int i = c % cols, j = c / cols;
                if (i > 0) Push(c - 1, labels, label, visited, queue);
                if (i + 1 < cols) Push(c + 1, labels, label, visited, queue);
                if (j > 0) Push(c - cols, labels, label, visited, queue);
                if (j + 1 < rows) Push(c + cols, labels, label, visited, queue);
            }
            components.Add(comp);
        }
        return components;
    }

    private static void Push(int idx, int[] labels, int label, bool[] visited, Queue<int> q)
    {
        if (visited[idx] || labels[idx] != label) return;
        visited[idx] = true;
        q.Enqueue(idx);
    }

    /// <summary>
    /// Trace the outer boundary of a connected cell set by walking edges along cell corners.
    /// Each cell occupies 4 corners; an edge between an in-cell and an out-cell is on the boundary.
    /// </summary>
    private static List<Vec2> TraceRingFromComponent(HashSet<int> comp, int cols, int rows,
        double ox, double oy, double cs)
    {
        bool InComp(int i, int j) => i >= 0 && i < cols && j >= 0 && j < rows && comp.Contains(j * cols + i);

        // Find a starting boundary edge: leftmost-bottom cell's left edge.
        int startCell = comp.Min();
        int si = startCell % cols, sj = startCell / cols;

        // Walk boundary using "Moore neighborhood" tracing on the cell grid edges.
        // Corner coordinates: corner (ci, cj) is at world (ox + ci*cs, oy + cj*cs).
        // We trace a closed loop of corners around the component.
        var ring = new List<Vec2>();

        // Direction encoding for edge walking: 0=right, 1=up, 2=left, 3=down.
        // Start at the bottom-left corner of (si, sj) going right along the bottom edge.
        int ci = si, cj = sj;
        int dir = 0; // moving right along bottom edge
        var startCorner = (ci, cj, dir);

        int safety = comp.Count * 8 + 16;
        while (safety-- > 0)
        {
            ring.Add(new Vec2(ox + ci * cs, oy + cj * cs));
            // Advance along current direction by 1 corner
            (ci, cj) = dir switch
            {
                0 => (ci + 1, cj),
                1 => (ci, cj + 1),
                2 => (ci - 1, cj),
                _ => (ci, cj - 1),
            };
            // Determine new direction: try left turn, straight, right turn, U-turn.
            // The "interior" of the component is on the left side as we walk CCW.
            int nextDir = TurnDirection(ci, cj, dir, InComp);
            dir = nextDir;
            if (ci == startCorner.ci && cj == startCorner.cj && dir == startCorner.dir) break;
        }
        return ring;
    }

    private static int TurnDirection(int ci, int cj, int dir, Func<int, int, bool> inComp)
    {
        // Cells around corner (ci, cj):
        //  TL = (ci-1, cj),   TR = (ci, cj)
        //  BL = (ci-1, cj-1), BR = (ci, cj-1)
        bool tl = inComp(ci - 1, cj);
        bool tr = inComp(ci, cj);
        bool bl = inComp(ci - 1, cj - 1);
        bool br = inComp(ci, cj - 1);
        // Walking CCW (interior on left). Decide next dir from current dir + occupancy.
        // Try: left turn first, then straight, then right turn, then back.
        int[] tryOrder = { (dir + 1) % 4, dir, (dir + 3) % 4, (dir + 2) % 4 };
        foreach (var d in tryOrder)
        {
            if (CanGo(d, tl, tr, bl, br)) return d;
        }
        return dir;
    }

    // Edge from corner can go in direction d if the cell on its right is in-comp
    // and the cell on its left is out-of-comp (interior on the left for CCW walk).
    private static bool CanGo(int d, bool tl, bool tr, bool bl, bool br)
    {
        // Direction 0 (right): cell below (BR) is left-side, cell above (TR) is right-side.
        // Wait — re-derive carefully. We walk along edges, interior on LEFT of motion.
        //   d=0 (going +X):  left  = above (TR), right = below (BR)
        //   d=1 (going +Y):  left  = left  (TL), right = right (TR)
        //   d=2 (going -X):  left  = below (BL), right = above (TL)
        //   d=3 (going -Y):  left  = right (BR), right = left  (BL)
        return d switch
        {
            0 => tr && !br,
            1 => tl && !tr,
            2 => bl && !tl,
            3 => br && !bl,
            _ => false,
        };
    }
}

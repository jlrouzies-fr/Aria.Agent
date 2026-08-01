namespace Aria.Web.Services.Memory;

// Pure layout math for the full-page Memory graph canvas — a deterministic clustered arrangement
// (no physics simulation, no persisted coordinates). Entities are grouped into "topics" server-side
// (NoosphereService.GetGraphAsync, greedy-modularity communities over relations + co-mention) and each
// topic is laid out here as a hub-and-rings cluster, then clusters are packed on a spiral. Relations
// that bridge two topics stay visible as cross-cluster edges, so connected clusters are pulled next
// to each other during packing.
public static class MemoryGraphLayout
{
    public record MemoryCluster(double Cx, double Cy, double R, string Label, int Group);

    public record ClusteredLayoutResult(
        Dictionary<string, (double X, double Y)> Positions,
        List<MemoryCluster> Clusters,
        double Width, double Height, double CenterX, double CenterY);

    private const double RingCapacity = 9;
    private const double RingRadiusStart = 195;
    private const double RingRadiusStep = 140;
    private const double ClusterMargin = 95;
    private const double ClusterGap = 60;
    private const double CanvasPadding = 150;
    private const string UnlinkedLabel = "UNLINKED";

    public static ClusteredLayoutResult ComputeClusteredLayout(
        IReadOnlyList<MemoryGraphNodeDto> nodes, IReadOnlyList<MemoryGraphEdgeDto> edges)
    {
        if (nodes.Count == 0)
            return new ClusteredLayoutResult([], [], 0, 0, 0, 0);

        var groupOf = nodes.ToDictionary(n => n.Id, n => n.Group);
        var connected = edges.SelectMany(e => new[] { e.From, e.To }).ToHashSet();

        var byGroup = nodes.GroupBy(n => n.Group).ToList();
        // A single-node topic still gets its own cluster if it participates in any relation (it may
        // bridge two topics); only fully isolated entities are swept into the UNLINKED grid.
        var realClusters = byGroup
            .Where(g => g.Count() > 1 || connected.Contains(g.First().Id))
            .OrderByDescending(g => g.Count())
            .Select(g => LayoutCluster(g.ToList(), g.Key))
            .ToList();
        var isolated = byGroup.Where(g => g.Count() == 1 && !connected.Contains(g.First().Id))
            .SelectMany(g => g).ToList();

        var clusters = new List<(Dictionary<string, (double X, double Y)> Local, double Radius, string Label, int Group)>(realClusters);
        if (isolated.Count > 0)
            clusters.Add(LayoutGrid(isolated));

        // Which topic groups are linked by at least one cross-cluster relation — used to pull
        // connected clusters toward each other on the spiral.
        var groupLinks = new HashSet<(int, int)>();
        foreach (var e in edges)
        {
            if (!groupOf.TryGetValue(e.From, out var ga) || !groupOf.TryGetValue(e.To, out var gb) || ga == gb) continue;
            groupLinks.Add(ga < gb ? (ga, gb) : (gb, ga));
        }

        var placedCenters = new List<(double Cx, double Cy, double R, int Group)>();
        var positions = new Dictionary<string, (double X, double Y)>();
        var resultClusters = new List<MemoryCluster>();

        foreach (var cluster in clusters)
        {
            var neighbors = placedCenters
                .Where(p => groupLinks.Contains(cluster.Group < p.Group ? (cluster.Group, p.Group) : (p.Group, cluster.Group)))
                .ToList();
            var target = neighbors.Count > 0
                ? (X: neighbors.Average(p => p.Cx), Y: neighbors.Average(p => p.Cy))
                : (X: 0.0, Y: 0.0);

            var (cx, cy) = PlaceOnSpiral(cluster.Radius, placedCenters, target);
            placedCenters.Add((cx, cy, cluster.Radius, cluster.Group));
            foreach (var (id, local) in cluster.Local)
                positions[id] = (cx + local.X, cy + local.Y);
            resultClusters.Add(new MemoryCluster(cx, cy, cluster.Radius, cluster.Label, cluster.Group));
        }

        var minX = placedCenters.Min(p => p.Cx - p.R);
        var maxX = placedCenters.Max(p => p.Cx + p.R);
        var minY = placedCenters.Min(p => p.Cy - p.R);
        var maxY = placedCenters.Max(p => p.Cy + p.R);

        var offsetX = CanvasPadding - minX;
        var offsetY = CanvasPadding - minY;
        foreach (var id in positions.Keys.ToList())
            positions[id] = (positions[id].X + offsetX, positions[id].Y + offsetY);
        for (var i = 0; i < resultClusters.Count; i++)
        {
            var c = resultClusters[i];
            resultClusters[i] = c with { Cx = c.Cx + offsetX, Cy = c.Cy + offsetY };
        }

        var width = maxX - minX + 2 * CanvasPadding;
        var height = maxY - minY + 2 * CanvasPadding;
        var centerX = CanvasPadding + (maxX - minX) / 2.0;
        var centerY = CanvasPadding + (maxY - minY) / 2.0;

        return new ClusteredLayoutResult(positions, resultClusters, width, height, centerX, centerY);
    }

    // Hub-and-rings: the highest-engram-count member sits at the cluster center; the rest fill
    // concentric rings around it, ~9 nodes per ring so per-node arc spacing stays readable.
    private static (Dictionary<string, (double X, double Y)> Local, double Radius, string Label, int Group) LayoutCluster(
        List<MemoryGraphNodeDto> members, int group)
    {
        var local = new Dictionary<string, (double X, double Y)>();
        var ordered = members.OrderByDescending(m => m.EngramCount).ThenBy(m => m.Name).ToList();
        var hub = ordered[0];
        local[hub.Id] = (0, 0);

        var rest = ordered.Skip(1).ToList();
        var ring = 0;
        var idx = 0;
        while (idx < rest.Count)
        {
            var ringNodes = rest.Skip(idx).Take((int)RingCapacity).ToList();
            var ringRadius = RingRadiusStart + ring * RingRadiusStep;
            var n = ringNodes.Count;
            for (var i = 0; i < n; i++)
            {
                var angle = 2 * Math.PI * i / n - Math.PI / 2;
                local[ringNodes[i].Id] = (ringRadius * Math.Cos(angle), ringRadius * Math.Sin(angle));
            }
            idx += n;
            ring++;
        }

        var radius = rest.Count == 0 ? 70 : RingRadiusStart + (ring - 1) * RingRadiusStep + ClusterMargin;
        return (local, radius, hub.Name, group);
    }

    private static (Dictionary<string, (double X, double Y)> Local, double Radius, string Label, int Group) LayoutGrid(
        List<MemoryGraphNodeDto> members)
    {
        var local = new Dictionary<string, (double X, double Y)>();
        var ordered = members.OrderBy(m => m.Name).ToList();
        var n = ordered.Count;
        var cols = (int)Math.Ceiling(Math.Sqrt(n));
        var rows = (int)Math.Ceiling(n / (double)cols);
        const double spacing = 140;

        for (var i = 0; i < n; i++)
        {
            var col = i % cols;
            var row = i / cols;
            var x = (col - (cols - 1) / 2.0) * spacing;
            var y = (row - (rows - 1) / 2.0) * spacing;
            local[ordered[i].Id] = (x, y);
        }

        var halfW = cols * spacing / 2.0;
        var halfH = rows * spacing / 2.0;
        var radius = Math.Sqrt(halfW * halfW + halfH * halfH) + ClusterMargin;
        return (local, radius, UnlinkedLabel, -1);
    }

    // Greedy circle packing on an Archimedean spiral: walk outward from the canvas origin collecting
    // non-overlapping candidate positions, and keep the one nearest the target point (the centroid of
    // already-placed clusters this one shares relations with) for up to one extra spiral loop past the
    // first fit. Deterministic — clusters are placed in a fixed order at a fixed angular step.
    private static (double Cx, double Cy) PlaceOnSpiral(
        double radius, List<(double Cx, double Cy, double R, int Group)> placed, (double X, double Y) target)
    {
        if (placed.Count == 0) return (0, 0);

        const double b = 60; // spiral tightness — distance between successive loops
        const double thetaStep = 0.15;
        var theta = 0.0;
        double? firstFitTheta = null;
        var best = (X: 0.0, Y: 0.0);
        var bestDist = double.MaxValue;

        while (true)
        {
            theta += thetaStep;
            var r = b * theta;
            var x = r * Math.Cos(theta);
            var y = r * Math.Sin(theta);
            var overlaps = placed.Any(p =>
            {
                var dx = p.Cx - x;
                var dy = p.Cy - y;
                return Math.Sqrt(dx * dx + dy * dy) < p.R + radius + ClusterGap;
            });
            if (!overlaps)
            {
                firstFitTheta ??= theta;
                var dx = target.X - x;
                var dy = target.Y - y;
                var dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; best = (x, y); }
            }
            if (firstFitTheta != null && theta > firstFitTheta + 2 * Math.PI)
                return best;
        }
    }

    // Quadratic bezier control point offset perpendicular to the A-B line, for a gentle arc between
    // two arbitrary points (unlike Hive's fixed hub-and-spoke vertical-midpoint bezier).
    public static (double Cx, double Cy) ArcControlPoint(double ax, double ay, double bx, double by, double curvature = 0.15)
    {
        var mx = (ax + bx) / 2.0;
        var my = (ay + by) / 2.0;
        var dx = bx - ax;
        var dy = by - ay;
        return (mx - dy * curvature, my + dx * curvature);
    }

    // Waypoints of a single-bend orthogonal "trace" between two arbitrary points, with a 45-degree
    // chamfer at the corner — reads as a circuit-board / schematic wire rather than an organic curve.
    // Routes along whichever axis has the larger span first, so short cross-links don't take
    // needlessly long runs.
    private static List<(double X, double Y)> ElbowPoints(double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var signX = dx >= 0 ? 1 : -1;
        var signY = dy >= 0 ? 1 : -1;
        var chamfer = Math.Min(14, Math.Min(Math.Abs(dx), Math.Abs(dy)) / 2.0);

        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            var cornerX = bx - signX * chamfer;
            var chamferEndY = ay + signY * chamfer;
            return [(ax, ay), (cornerX, ay), (bx, chamferEndY), (bx, by)];
        }

        var cornerY = by - signY * chamfer;
        var chamferEndX = ax + signX * chamfer;
        return [(ax, ay), (ax, cornerY), (chamferEndX, by), (bx, by)];
    }

    private static string PathFromPoints(List<(double X, double Y)> pts) =>
        string.Join(" ", pts.Select((p, i) => (i == 0 ? "M" : "L") + $"{p.X:F0},{p.Y:F0}"));

    // Point on the polyline at 50% of its total arc length — unlike snapping to one endpoint's raw
    // coordinate (the previous approach), this naturally sits away from both nodes the edge connects,
    // roughly in proportion to how long each run is either side of the bend.
    private static (double X, double Y) ArcLengthMidpoint(List<(double X, double Y)> pts)
    {
        var lens = new double[pts.Count - 1];
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var ddx = pts[i + 1].X - pts[i].X;
            var ddy = pts[i + 1].Y - pts[i].Y;
            lens[i] = Math.Sqrt(ddx * ddx + ddy * ddy);
        }
        var target = lens.Sum() / 2.0;
        var acc = 0.0;
        for (var i = 0; i < lens.Length; i++)
        {
            if (acc + lens[i] >= target || i == lens.Length - 1)
            {
                var t = lens[i] <= 0 ? 0 : (target - acc) / lens[i];
                return (pts[i].X + (pts[i + 1].X - pts[i].X) * t, pts[i].Y + (pts[i + 1].Y - pts[i].Y) * t);
            }
            acc += lens[i];
        }
        return pts[0];
    }

    private static double SegmentToPointDistance(double ax, double ay, double bx, double by, double px, double py)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lenSq = dx * dx + dy * dy;
        var t = lenSq <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0, 1);
        var cx = ax + t * dx;
        var cy = ay + t * dy;
        var ddx = px - cx;
        var ddy = py - cy;
        return Math.Sqrt(ddx * ddx + ddy * ddy);
    }

    public static (string PathD, double LabelX, double LabelY) CircuitPath(double ax, double ay, double bx, double by)
    {
        var pts = ElbowPoints(ax, ay, bx, by);
        var (lx, ly) = ArcLengthMidpoint(pts);
        return (PathFromPoints(pts), lx, ly);
    }

    // Same trace, but bows around any obstacle cluster the direct route would otherwise cut through —
    // for cross-cluster relations, where a straight/elbow line between two distant entities can easily
    // pass over a third topic's hull that happens to sit in between. Detours via one waypoint pushed
    // perpendicular to the A-B line, on whichever side clears the obstacle.
    public static (string PathD, double LabelX, double LabelY) CircuitPathAvoiding(
        double ax, double ay, double bx, double by, IReadOnlyList<(double Cx, double Cy, double R)> obstacles)
    {
        const double margin = 30.0;
        (double Cx, double Cy, double R)? blocker = null;
        foreach (var o in obstacles)
        {
            if (SegmentToPointDistance(ax, ay, bx, by, o.Cx, o.Cy) < o.R + margin)
            {
                blocker = o;
                break;
            }
        }
        if (blocker == null)
            return CircuitPath(ax, ay, bx, by);

        var dx = bx - ax;
        var dy = by - ay;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1)
            return CircuitPath(ax, ay, bx, by);

        var nx = -dy / len;
        var ny = dx / len;
        var mx = (ax + bx) / 2.0;
        var my = (ay + by) / 2.0;
        var side = (blocker.Value.Cx - ax) * dy - (blocker.Value.Cy - ay) * dx < 0 ? 1.0 : -1.0;
        var bow = blocker.Value.R + margin + 40;
        var waypointX = mx + nx * bow * side;
        var waypointY = my + ny * bow * side;

        var pts = ElbowPoints(ax, ay, waypointX, waypointY);
        pts.AddRange(ElbowPoints(waypointX, waypointY, bx, by).Skip(1));
        var (lx, ly) = ArcLengthMidpoint(pts);
        return (PathFromPoints(pts), lx, ly);
    }

    public static string KindColor(string? kind) => kind switch
    {
        "person"  => "#f0d060", // amber — matches text-bright
        "place"   => "#2ab8b8", // teal
        "org"     => "#b070e0", // violet
        "concept" => "#5a9cf0", // blue
        "thing"   => "#7ac47a", // green
        "event"   => "#e07070", // red
        "project" => "#e0a050", // orange
        "other"   => "#a89878", // khaki — explicit other (not missing)
        _         => "#888888", // null / unknown
    };

    public static string KindGlyph(string? kind) => kind switch
    {
        "person"  => "☉",
        "place"   => "◈",
        "org"     => "⬡",
        "concept" => "◇",
        "thing"   => "▣",
        "event"   => "✦",
        "project" => "◉",
        "other"   => "◌",
        _         => "○",
    };
}

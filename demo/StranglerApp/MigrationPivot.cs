namespace StranglerApp;

/// <summary>
/// The routing seam — the one place a route id becomes a handler. Promoted
/// routes dispatch to the new implementation; everything else stays legacy.
/// This is the pivot the cockpit scans for wiring evidence.
/// </summary>
public sealed class MigrationPivot
{
    private readonly SortedSet<string> _routes = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _promoted = new(StringComparer.Ordinal);

    public void MapRoute(string id) => _routes.Add(id);

    /// <summary>Moves a route onto the new implementation — the fig's bite.</summary>
    public void Promote(string id) => _promoted.Add(id);

    public bool IsPromoted(string id) => _promoted.Contains(id);

    public IReadOnlyList<string> Routes => _routes.ToArray();
}

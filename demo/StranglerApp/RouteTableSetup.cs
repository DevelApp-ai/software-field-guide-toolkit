namespace StranglerApp;

/// <summary>
/// The demo app's wired route table: three routes at the seam, one of them
/// (shipping.quote) already strangled — promoted to the new implementation
/// and retired below the traffic threshold.
/// </summary>
public static class RouteTableSetup
{
    public static MigrationPivot BuildPivot()
    {
        var pivot = new MigrationPivot();
        pivot.MapRoute("orders.search");
        pivot.MapRoute("orders.get");
        pivot.MapRoute("shipping.quote");
        pivot.Promote("shipping.quote");
        return pivot;
    }
}

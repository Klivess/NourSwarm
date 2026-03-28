namespace NourSwarm;

public sealed class MarketDataService
{
    private readonly List<LOBTick> _ticks = [];

    public void Ingest(IEnumerable<LOBTick> ticks)
    {
        _ticks.AddRange(ticks);
    }

    public IReadOnlyList<LOBTick> GetHistory() => _ticks;
}

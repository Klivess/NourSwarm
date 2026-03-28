namespace NourSwarm;

public sealed class TradingAgent
{
    public Guid Id { get; }
    public AgentRole Role { get; }
    public Side Side { get; }
    public decimal PriceLevel { get; }
    public decimal InitialVolume { get; }
    public decimal RemainingVolume { get; private set; }
    public AgentDna OCEANTraits { get; }
    public decimal CancellationRate { get; }
    public bool IsCancelled { get; private set; }
    public bool IsTopOfBook { get; }

    public TradingAgent(
        Guid id,
        AgentRole role,
        Side side,
        decimal priceLevel,
        decimal volume,
        AgentDna traits,
        decimal cancellationRate,
        bool isTopOfBook)
    {
        Id = id;
        Role = role;
        Side = side;
        PriceLevel = priceLevel;
        InitialVolume = volume;
        RemainingVolume = volume;
        OCEANTraits = traits;
        CancellationRate = cancellationRate;
        IsTopOfBook = isTopOfBook;
    }

    public bool IsActive => !IsCancelled && RemainingVolume > 0m;

    public decimal Consume(decimal quantity)
    {
        if (IsCancelled || quantity <= 0m || RemainingVolume <= 0m)
        {
            return 0m;
        }

        var filled = Math.Min(quantity, RemainingVolume);
        RemainingVolume -= filled;
        return filled;
    }

    public bool TryCancelByNeuroticTrigger(decimal simulatedPrice, decimal tickSize, int triggerTicks)
    {
        if (Role != AgentRole.Passive || IsCancelled || OCEANTraits.Neuroticism < 70.0)
        {
            return false;
        }

        var triggerDistance = tickSize * triggerTicks;
        if (Math.Abs(PriceLevel - simulatedPrice) > triggerDistance)
        {
            return false;
        }

        IsCancelled = true;
        RemainingVolume = 0m;
        return true;
    }

    public static TradingAgent CreatePassive(
        Guid id,
        Side side,
        decimal priceLevel,
        decimal volume,
        AgentDna traits,
        decimal cancellationRate,
        bool isTopOfBook)
    {
        return new TradingAgent(id, AgentRole.Passive, side, priceLevel, volume, traits, cancellationRate, isTopOfBook);
    }

    public static TradingAgent CreateAggressive(Guid id, Side side, decimal priceLevel, decimal quantity, AgentDna traits)
    {
        return new TradingAgent(id, AgentRole.Aggressive, side, priceLevel, quantity, traits, 0m, isTopOfBook: false);
    }
}

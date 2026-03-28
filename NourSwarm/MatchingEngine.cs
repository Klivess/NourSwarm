namespace NourSwarm;

public sealed class MatchingEngine
{
    private readonly SortedDictionary<decimal, TradingAgent> _bids;
    private readonly SortedDictionary<decimal, TradingAgent> _asks;
    private readonly decimal _tickSize;

    public decimal SimulatedPrice { get; private set; }

    public MatchingEngine(IEnumerable<TradingAgent> passiveAgents, decimal initialPrice, decimal tickSize)
    {
        _tickSize = Math.Max(0.00000001m, tickSize);
        SimulatedPrice = initialPrice;

        _bids = new SortedDictionary<decimal, TradingAgent>(Comparer<decimal>.Create((x, y) => y.CompareTo(x)));
        _asks = new SortedDictionary<decimal, TradingAgent>();

        foreach (var agent in passiveAgents)
        {
            if (agent.Role != AgentRole.Passive || agent.InitialVolume <= 0m)
            {
                continue;
            }

            if (agent.Side == Side.Buy)
            {
                _bids[agent.PriceLevel] = agent;
            }
            else
            {
                _asks[agent.PriceLevel] = agent;
            }
        }
    }

    public DeterministicPrediction RunDeterministic(IReadOnlyList<TradingAgent> aggressiveAgents, int neuroticPullTicks = 2)
    {
        var levelsConsumed = 0;
        var cancelledLevels = 0;
        decimal remainingAggressiveVolume = 0m;

        for (int i = 0; i < aggressiveAgents.Count; i++)
        {
            var aggressor = aggressiveAgents[i];
            if (aggressor.Role != AgentRole.Aggressive || aggressor.InitialVolume <= 0m)
            {
                continue;
            }

            var remaining = aggressor.InitialVolume;
            remainingAggressiveVolume += remaining;

            while (remaining > 0m)
            {
                cancelledLevels += PullTriggeredNeuroticLiquidity(aggressor.Side, neuroticPullTicks);

                if (!TryGetBestPassiveLevel(aggressor.Side, out var restingAgent))
                {
                    break;
                }

                var filled = restingAgent.Consume(remaining);
                if (filled <= 0m)
                {
                    RemoveIfInactive(restingAgent);
                    continue;
                }

                remaining -= filled;
                remainingAggressiveVolume -= filled;
                SimulatedPrice = restingAgent.PriceLevel;

                if (!restingAgent.IsActive)
                {
                    levelsConsumed++;
                    RemoveIfInactive(restingAgent);
                }
            }
        }

        return new DeterministicPrediction(
            PredictedClose: SimulatedPrice,
            ExhaustionPrice: SimulatedPrice,
            RemainingAggressiveVolume: Math.Max(0m, remainingAggressiveVolume),
            LevelsConsumed: levelsConsumed,
            CancelledLevels: cancelledLevels);
    }

    private int PullTriggeredNeuroticLiquidity(Side aggressorSide, int triggerTicks)
    {
        var cancelled = 0;
        var sideToCheck = aggressorSide == Side.Buy ? _asks : _bids;

        foreach (var kvp in sideToCheck.ToArray())
        {
            var passive = kvp.Value;
            if (passive.TryCancelByNeuroticTrigger(SimulatedPrice, _tickSize, triggerTicks))
            {
                sideToCheck.Remove(kvp.Key);
                cancelled++;
            }
        }

        return cancelled;
    }

    private bool TryGetBestPassiveLevel(Side aggressorSide, out TradingAgent passive)
    {
        var oppositeBook = aggressorSide == Side.Buy ? _asks : _bids;

        foreach (var kvp in oppositeBook)
        {
            if (!kvp.Value.IsActive)
            {
                continue;
            }

            passive = kvp.Value;
            return true;
        }

        passive = null!;
        return false;
    }

    private void RemoveIfInactive(TradingAgent passive)
    {
        if (passive.IsActive)
        {
            return;
        }

        if (passive.Side == Side.Buy)
        {
            _bids.Remove(passive.PriceLevel);
        }
        else
        {
            _asks.Remove(passive.PriceLevel);
        }
    }
}

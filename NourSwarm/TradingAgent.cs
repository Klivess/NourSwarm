namespace NourSwarm;

public sealed class TradingAgent
{
    public Guid Id { get; }
    public AgentDna OCEANTraits { get; }

    public TradingAgent(Guid id, AgentDna traits)
    {
        Id = id;
        OCEANTraits = traits;
    }

    public AgentAction? Act(OrderBookSnapshot lob, ReadOnlySpan<decimal> closeHistory, decimal recentChange, Random random)
    {
        var activity = 0.10 + (OCEANTraits.Extraversion / 200.0);
        if (random.NextDouble() > activity)
        {
            return null;
        }

        var panicThreshold = 0.001m + (decimal)((100.0 - OCEANTraits.Neuroticism) / 100.0) * 0.02m;
        if (recentChange < -panicThreshold)
        {
            var qty = 0.5m + (decimal)(OCEANTraits.Neuroticism / 100.0) * 1.5m;
            return new AgentAction(true, new Order(Id, Side.Sell, 0m, qty, OrderType.Market));
        }

        var marketProbability = OCEANTraits.Extraversion / 100.0;
        var isMarket = random.NextDouble() < marketProbability;

        var side = ChooseSide(closeHistory, lob, random);
        var quantity = 0.5m + (decimal)((OCEANTraits.Extraversion + OCEANTraits.Openness) / 200.0);
        var price = isMarket ? 0m : ComputeLimitPrice(side, lob, random);

        return new AgentAction(false, new Order(Id, side, price, quantity, isMarket ? OrderType.Market : OrderType.Limit));
    }

    private Side ChooseSide(ReadOnlySpan<decimal> closeHistory, OrderBookSnapshot lob, Random random)
    {
        var momentumBias = 0;

        if (OCEANTraits.Agreeableness >= 60 && closeHistory.Length >= 4)
        {
            var i = closeHistory.Length - 1;
            //increasing price.
            var g1 = closeHistory[i] > closeHistory[i - 1];
            var g2 = closeHistory[i - 1] > closeHistory[i - 2];
            var g3 = closeHistory[i - 2] > closeHistory[i - 3];
            if (g1 && g2 && g3)
            {
                momentumBias = 1;
            }
            else if (!g1 && !g2 && !g3)
            {
                momentumBias = -1;
            }
        }

        if (momentumBias > 0)
        {
            return Side.Buy;
        }

        if (momentumBias < 0)
        {
            return Side.Sell;
        }

        var imbalanceBias = lob.Imbalance >= 0m ? Side.Buy : Side.Sell;
        return random.NextDouble() < 0.55 ? imbalanceBias : (imbalanceBias == Side.Buy ? Side.Sell : Side.Buy);
    }

    private decimal ComputeLimitPrice(Side side, OrderBookSnapshot lob, Random random)
    {
        var price = lob.MidPrice;
        var spread = lob.Spread == 0m ? 0.01m : lob.Spread;

        if (OCEANTraits.Conscientiousness >= 65)
        {
            price = lob.Vwap;
        }
        else if (OCEANTraits.Openness >= 65)
        {
            var multiplier = 1m + (decimal)(OCEANTraits.Openness / 100.0) * 0.5m;
            price = side == Side.Buy
                ? lob.BestBid - spread * multiplier
                : lob.BestAsk + spread * multiplier;
        }
        else
        {
            var jitter = (decimal)(random.NextDouble() - 0.5) * spread;
            price = side == Side.Buy ? lob.BestBid + jitter : lob.BestAsk + jitter;
        }

        return Math.Max(0.0001m, decimal.Round(price, 4));
    }
}

namespace NourSwarm;

public sealed class PsychMapper
{
    public OCEAN CalculateMeans(IReadOnlyList<LOBTick> history)
    {
        if (history.Count < 2)
        {
            return new OCEAN(50, 50, 50, 50, 50);
        }

        var midPrices = new decimal[history.Count];
        var midSpan = midPrices.AsSpan();

        decimal totalDepth = 0m;
        decimal totalSpread = 0m;
        decimal ofi = 0m;
        decimal fills = 0m;
        decimal cancels = 0m;
        int trendAlignedSteps = 0;

        for (int i = 0; i < history.Count; i++)
        {
            var t = history[i];
            midSpan[i] = (t.BidPrice + t.AskPrice) * 0.5m;
            totalDepth += t.BidSize + t.AskSize;
            totalSpread += Math.Max(0.0000001m, t.AskPrice - t.BidPrice);

            if (i == 0)
            {
                continue;
            }

            var prev = history[i - 1];
            var dBid = t.BidSize - prev.BidSize;
            var dAsk = t.AskSize - prev.AskSize;
            ofi += dBid - dAsk;

            if (dBid < 0m && dAsk < 0m)
            {
                fills += (-dBid - dAsk) * 0.5m;
            }
            else
            {
                cancels += Math.Max(0m, dBid) + Math.Max(0m, dAsk);
            }

            var deltaNow = midSpan[i] - midSpan[i - 1];
            if (i >= 2)
            {
                var deltaPrev = midSpan[i - 1] - midSpan[i - 2];
                if (Math.Sign(deltaNow) != 0 && Math.Sign(deltaNow) == Math.Sign(deltaPrev))
                {
                    trendAlignedSteps++;
                }
            }
        }

        var volatility = CalculateStdDev(midSpan);
        var avgDepth = totalDepth / history.Count;
        var avgSpread = totalSpread / history.Count;
        var avgMid = midPrices.Average();
        var fillToCancel = (double)(fills / (cancels + 1m));
        var depthScore = avgDepth / 200m;
        var normalizedOfi = ofi / (totalDepth + 1m);
        var spreadRatio = avgSpread / (avgMid == 0m ? 1m : avgMid);
        var trendPersistence = history.Count > 2 ? (double)trendAlignedSteps / (history.Count - 2) : 0.0;

        var neuroticism = ClampScore(50 + (int)Math.Round(volatility * 4500.0));
        var extraversion = ClampScore(50 + (int)Math.Round((double)(normalizedOfi * 1000m)));
        var conscientiousness = ClampScore(45 + (int)Math.Round((double)(depthScore * 35m)) + (int)Math.Round(fillToCancel * 4));
        var openness = ClampScore(45 + (int)Math.Round((double)(spreadRatio * 7000m)) + (int)Math.Round((1.0 - Math.Min(fillToCancel, 2.0) / 2.0) * 15));
        var agreeableness = ClampScore(40 + (int)Math.Round(trendPersistence * 45));

        return new OCEAN(openness, conscientiousness, extraversion, agreeableness, neuroticism);
    }

    private static double CalculateStdDev(ReadOnlySpan<decimal> values)
    {
        if (values.Length == 0)
        {
            return 0d;
        }

        decimal mean = 0m;
        for (int i = 0; i < values.Length; i++)
        {
            mean += values[i];
        }

        mean /= values.Length;
        double sum = 0d;

        for (int i = 0; i < values.Length; i++)
        {
            var diff = (double)(values[i] - mean);
            sum += diff * diff;
        }

        return Math.Sqrt(sum / values.Length);
    }

    private static int ClampScore(int value) => Math.Clamp(value, 1, 100);
}

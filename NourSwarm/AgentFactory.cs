namespace NourSwarm;

public sealed class AgentFactory
{
    public DigitalTwinSwarm GenerateDigitalTwinSwarm(IReadOnlyList<LOBLevel> levels, IReadOnlyList<TapePrint> tape, decimal midPrice, decimal tickSize)
    {
        var passive = new List<TradingAgent>(levels.Count);
        var aggressive = new List<TradingAgent>(tape.Count);

        var bestBid = levels.Where(l => l.Side == Side.Buy).Select(l => l.Price).DefaultIfEmpty(midPrice - tickSize).Max();
        var bestAsk = levels.Where(l => l.Side == Side.Sell).Select(l => l.Price).DefaultIfEmpty(midPrice + tickSize).Min();

        for (int i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            var isTopOfBook = level.Side == Side.Buy
                ? level.Price == bestBid
                : level.Price == bestAsk;

            passive.Add(GenerateAgentFromLOBLevel(level, midPrice, tickSize, isTopOfBook));
        }

        for (int i = 0; i < tape.Count; i++)
        {
            aggressive.Add(GenerateAgentFromTape(tape[i], midPrice, tickSize));
        }

        return new DigitalTwinSwarm(passive, aggressive, midPrice, tickSize);
    }

    public TradingAgent GenerateAgentFromLOBLevel(LOBLevel level, decimal midPrice, decimal tickSize, bool isTopOfBook)
    {
        var distanceTicks = (double)(Math.Abs(level.Price - midPrice) / Math.Max(tickSize, 0.00000001m));
        var normalizedDistance = Math.Min(1.0, distanceTicks / 50.0);
        var normalizedVolume = Math.Min(1.0, (double)(level.Volume / 25m));
        var cancellationRate = Math.Clamp((double)level.CancellationRate, 0.0, 1.0);

        var openness = ClampTrait(10 + normalizedVolume * 45 + normalizedDistance * 45);
        var conscientiousness = ClampTrait(10 + normalizedDistance * 90);
        var extraversion = 0.0;
        var agreeableness = isTopOfBook
            ? 95.0
            : ClampTrait(20 + (1.0 - normalizedDistance) * 55 + normalizedVolume * 20);
        var neuroticism = ClampTrait(1 + cancellationRate * 99);

        var traits = new AgentDna(openness, conscientiousness, extraversion, agreeableness, neuroticism);
        return TradingAgent.CreatePassive(Guid.NewGuid(), level.Side, level.Price, level.Volume, traits, level.CancellationRate, isTopOfBook);
    }

    public TradingAgent GenerateAgentFromTape(TapePrint print, decimal midPrice, decimal tickSize)
    {
        var distanceTicks = (double)(Math.Abs(print.Price - midPrice) / Math.Max(tickSize, 0.00000001m));
        var normalizedDistance = Math.Min(1.0, distanceTicks / 25.0);
        var normalizedVolume = Math.Min(1.0, (double)(print.Quantity / 25m));

        var openness = ClampTrait(30 + normalizedDistance * 30 + normalizedVolume * 40);
        var conscientiousness = 0.0;
        var extraversion = 100.0;
        var agreeableness = ClampTrait(15 + normalizedVolume * 25);
        var neuroticism = ClampTrait(5 + normalizedDistance * 20);

        var traits = new AgentDna(openness, conscientiousness, extraversion, agreeableness, neuroticism);
        return TradingAgent.CreateAggressive(Guid.NewGuid(), print.Side, print.Price, print.Quantity, traits);
    }

    private static double ClampTrait(double value) => Math.Clamp(value, 1.0, 100.0);
}

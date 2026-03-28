namespace NourSwarm;

public sealed class AgentFactory
{
    private readonly Random _random;

    public AgentFactory(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public List<TradingAgent> GenerateSwarm(OCEAN means, int populationSize = 1000)
    {
        var agents = new List<TradingAgent>(populationSize);

        for (int i = 0; i < populationSize; i++)
        {
            var dna = new AgentDna(
                ClampTrait(NextGaussian(means.Openness, 12)),
                ClampTrait(NextGaussian(means.Conscientiousness, 11)),
                ClampTrait(NextGaussian(means.Extraversion, 13)),
                ClampTrait(NextGaussian(means.Agreeableness, 10)),
                ClampTrait(NextGaussian(means.Neuroticism, 14)));

            agents.Add(new TradingAgent(Guid.NewGuid(), dna));
        }

        return agents;
    }

    private double NextGaussian(double mean, double standardDeviation)
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        var randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + standardDeviation * randStdNormal;
    }

    private static double ClampTrait(double value) => Math.Clamp(value, 1.0, 100.0);
}

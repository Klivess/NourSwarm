namespace NourSwarm;

public sealed class Simulator
{
    public DeterministicPrediction RunDeterministic(DigitalTwinSwarm swarm, int neuroticPullTicks = 2)
    {
        var engine = new MatchingEngine(swarm.PassiveAgents, swarm.MidPrice, swarm.TickSize);
        return engine.RunDeterministic(swarm.AggressiveAgents, neuroticPullTicks);
    }
}

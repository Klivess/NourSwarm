using System.Collections.Concurrent;
namespace NourSwarm;

public sealed class Simulator
{
    private readonly MatchingEngine _matchingEngine;

    public Simulator(MatchingEngine matchingEngine)
    {
        _matchingEngine = matchingEngine;
    }

    public PredictionResult Run(IReadOnlyList<TradingAgent> agents, int ticksToPredict)
    {
        if (agents.Count == 0)
        {
            return new PredictionResult(_matchingEngine.SimulatedPrice, 0, 0, [_matchingEngine.SimulatedPrice]);
        }

        var closes = new List<decimal>(ticksToPredict + 1) { _matchingEngine.SimulatedPrice };
        var totalSubmitted = 0;
        var totalBuySubmitted = 0;
        var totalSellSubmitted = 0;

        var randomPerThread = new ThreadLocal<Random>(() => new Random(Random.Shared.Next()));

        for (int tick = 0; tick < ticksToPredict; tick++)
        {
            var snapshot = _matchingEngine.GetSnapshot();
            var recentChange = closes.Count >= 2
                ? (closes[^1] - closes[^2]) / (closes[^2] == 0m ? 1m : closes[^2])
                : 0m;

            var closeHistorySnapshot = closes.ToArray();
            var actions = new ConcurrentBag<AgentAction>();

            Parallel.ForEach(agents, agent =>
            {
                var action = agent.Act(snapshot, closeHistorySnapshot, recentChange, randomPerThread.Value!);
                if (action.HasValue)
                {
                    actions.Add(action.Value);
                }
            });

            foreach (var action in actions)
            {
                if (action.CancelAllOrders && action.Order is not null)
                {
                    _matchingEngine.CancelAgentOrders(action.Order.AgentId);
                }

                if (action.Order is null)
                {
                    continue;
                }

                totalSubmitted++;
                if (action.Order.Side == Side.Buy) totalBuySubmitted++;
                else totalSellSubmitted++;

                _matchingEngine.Process(action.Order);
            }

            _matchingEngine.DrainIncomingAndMatch();
            closes.Add(_matchingEngine.SimulatedPrice);
        }

        var predictedDirectionUp = closes[^1] >= closes[0];
        var alignedOrders = predictedDirectionUp ? totalBuySubmitted : totalSellSubmitted;
        var conviction = totalSubmitted == 0 ? 0 : (double)alignedOrders / totalSubmitted;

        var expectedClose = closes.Skip(1).DefaultIfEmpty(closes[0]).Average();
        var volatility = ComputeStdDev(closes.Skip(1));

        return new PredictionResult(expectedClose, volatility, conviction, closes);
    }

    private static double ComputeStdDev(IEnumerable<decimal> values)
    {
        var data = values.ToArray();
        if (data.Length == 0)
        {
            return 0;
        }

        var mean = data.Average();
        double variance = 0;

        foreach (var value in data)
        {
            var diff = (double)(value - mean);
            variance += diff * diff;
        }

        variance /= data.Length;
        return Math.Sqrt(variance);
    }
}

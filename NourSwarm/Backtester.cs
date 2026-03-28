namespace NourSwarm;

public record BacktestResult(
    int WindowsEvaluated,
    double DirectionalAccuracy,
    decimal MeanAbsoluteError,
    double RootMeanSquaredError,
    double VolatilityForecastError,
    IReadOnlyList<BacktestSample> Samples);

public record BacktestSample(
    int WindowIndex,
    decimal PredictedClose,
    decimal ActualClose,
    double PredictedVolatility,
    double ActualVolatility,
    bool PredictedUp,
    bool ActualUp);

public sealed class Backtester
{
    public BacktestResult Run(
        IReadOnlyList<LOBTick> ticks,
        int calibrationWindow,
        int predictionHorizon,
        int step,
        int swarmSize,
        int seed = 7)
    {
        if (ticks.Count < calibrationWindow + predictionHorizon + 1)
        {
            throw new ArgumentException("Not enough ticks for backtesting with provided window/horizon.", nameof(ticks));
        }

        var totalWindows = ((ticks.Count - (calibrationWindow + predictionHorizon) - 1) / step) + 1;
        Console.WriteLine($"Starting backtest. Ticks={ticks.Count}, Calibration={calibrationWindow}, Horizon={predictionHorizon}, Step={step}, Swarm={swarmSize}, Windows={totalWindows}");

        var samples = new List<BacktestSample>();
        var psychMapper = new PsychMapper();

        decimal maeSum = 0m;
        double rmseSum = 0d;
        double volErrSum = 0d;
        int directionHits = 0;

        int windowIndex = 0;

        for (int start = 0; start + calibrationWindow + predictionHorizon < ticks.Count; start += step)
        {
            var calibrationSlice = ticks.Skip(start).Take(calibrationWindow).ToList();
            var futureSlice = ticks.Skip(start + calibrationWindow).Take(predictionHorizon).ToList();

            var means = psychMapper.CalculateMeans(calibrationSlice);
            var factory = new AgentFactory(seed + windowIndex);
            var agents = factory.GenerateSwarm(means, swarmSize);

            var lastCalib = calibrationSlice[^1];
            var initialMid = (lastCalib.BidPrice + lastCalib.AskPrice) * 0.5m;
            var simulator = new Simulator(new MatchingEngine(initialMid));

            var prediction = simulator.Run(agents, predictionHorizon);

            var actualCloses = futureSlice.Select(t => (t.BidPrice + t.AskPrice) * 0.5m).ToArray();
            var actualClose = actualCloses.Average();
            var actualVol = ComputeStdDev(actualCloses);

            var predictedClose = prediction.ExpectedCandleClose;
            var predictedVol = prediction.VolatilityForecast;

            var basePrice = initialMid;
            var predictedUp = predictedClose >= basePrice;
            var actualUp = actualClose >= basePrice;

            if (predictedUp == actualUp)
            {
                directionHits++;
            }

            var absErr = Math.Abs(predictedClose - actualClose);
            maeSum += absErr;

            var diff = (double)(predictedClose - actualClose);
            rmseSum += diff * diff;

            volErrSum += Math.Abs(predictedVol - actualVol);

            samples.Add(new BacktestSample(
                WindowIndex: windowIndex,
                PredictedClose: predictedClose,
                ActualClose: actualClose,
                PredictedVolatility: predictedVol,
                ActualVolatility: actualVol,
                PredictedUp: predictedUp,
                ActualUp: actualUp));

            if ((windowIndex + 1) % 1 == 0 || windowIndex == totalWindows - 1)
            {
                Console.WriteLine($"Backtest progress: {windowIndex + 1}/{totalWindows} windows completed...");
            }

            windowIndex++;
        }

        if (samples.Count == 0)
        {
            throw new InvalidOperationException("Backtest produced zero windows. Reduce calibration/prediction constraints.");
        }

        Console.WriteLine($"Backtest complete. Windows={samples.Count}, DirectionalAccuracy={(double)directionHits / samples.Count:P2}, MAE={maeSum / samples.Count:F6}, RMSE={Math.Sqrt(rmseSum / samples.Count):F6}");

        return new BacktestResult(
            WindowsEvaluated: samples.Count,
            DirectionalAccuracy: (double)directionHits / samples.Count,
            MeanAbsoluteError: maeSum / samples.Count,
            RootMeanSquaredError: Math.Sqrt(rmseSum / samples.Count),
            VolatilityForecastError: volErrSum / samples.Count,
            Samples: samples);
    }

    private static double ComputeStdDev(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var mean = values.Average();
        double sum = 0d;

        for (int i = 0; i < values.Count; i++)
        {
            var diff = (double)(values[i] - mean);
            sum += diff * diff;
        }

        return Math.Sqrt(sum / values.Count);
    }
}

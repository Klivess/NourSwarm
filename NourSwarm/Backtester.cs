namespace NourSwarm;

public record BacktestResult(
    int WindowsEvaluated,
    double DirectionalAccuracy,
    decimal MeanAbsoluteError,
    double RootMeanSquaredError,
    double VolatilityForecastError,
    IReadOnlyList<BacktestSample> Samples)
{
    public string SaveWindowGraphsToDesktop(string? folderName = null)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var targetFolder = string.IsNullOrWhiteSpace(folderName)
            ? $"PSIE_Backtest_{DateTime.Now:yyyyMMdd_HHmmss}"
            : folderName;

        var outputDir = Path.Combine(desktop, targetFolder);
        Directory.CreateDirectory(outputDir);

        foreach (var sample in Samples)
        {
            var plot = new ScottPlot.Plot(1600, 900);

            if (sample.ActualCandles.Count > 0)
            {
                var ohlc = sample.ActualCandles
                    .Select(c => new ScottPlot.OHLC(
                        open: (double)c.Open,
                        high: (double)c.High,
                        low: (double)c.Low,
                        close: (double)c.Close,
                        timeStart: c.Timestamp.ToOADate(),
                        timeSpan: TimeSpan.FromMilliseconds(150).TotalDays))
                    .ToArray();

                plot.AddCandlesticks(ohlc);

                var xs = sample.ActualCandles.Select(c => c.Timestamp.ToOADate()).ToArray();
                var ys = AlignPredictedPath(sample.PredictedPath, xs.Length);
                plot.AddScatter(xs, ys, color: System.Drawing.Color.Red, lineWidth: 2, label: "Predicted");
            }

            plot.Title($"Window {sample.WindowIndex} | PredClose={sample.PredictedClose:F2} | ActualClose={sample.ActualClose:F2}");
            plot.XAxis.DateTimeFormat(true);
            plot.XLabel("Time");
            plot.YLabel("Price (USDT)");
            plot.Legend(location: ScottPlot.Alignment.UpperLeft);

            var path = Path.Combine(outputDir, $"window_{sample.WindowIndex:D5}.png");
            plot.SaveFig(path);
        }

        return outputDir;
    }

    private static double[] AlignPredictedPath(IReadOnlyList<decimal> predictedPath, int targetLength)
    {
        if (targetLength <= 0)
        {
            return [];
        }

        if (predictedPath.Count == 0)
        {
            return Enumerable.Repeat(0d, targetLength).ToArray();
        }

        if (predictedPath.Count == targetLength)
        {
            return predictedPath.Select(x => (double)x).ToArray();
        }

        var aligned = new double[targetLength];
        for (int i = 0; i < targetLength; i++)
        {
            var srcIndex = (int)Math.Round((double)i / Math.Max(1, targetLength - 1) * (predictedPath.Count - 1));
            aligned[i] = (double)predictedPath[Math.Clamp(srcIndex, 0, predictedPath.Count - 1)];
        }

        return aligned;
    }
}

public record BacktestSample(
    int WindowIndex,
    decimal PredictedClose,
    decimal ActualClose,
    double PredictedVolatility,
    double ActualVolatility,
    bool PredictedUp,
    bool ActualUp,
    IReadOnlyList<WindowCandle> ActualCandles,
    IReadOnlyList<decimal> PredictedPath);

public readonly record struct WindowCandle(DateTime Timestamp, decimal Open, decimal High, decimal Low, decimal Close);

public sealed class Backtester
{
    public BacktestResult Run(
        IReadOnlyList<LOBTick> ticks,
        int calibrationWindow,
        int predictionHorizon,
        int step)
    {
        var snapshots = ticks.Select(t => new MarketSnapshot(t, Array.Empty<TapePrint>())).ToList();
        return Run(snapshots, calibrationWindow, predictionHorizon, step);
    }

    public BacktestResult Run(
        IReadOnlyList<MarketSnapshot> snapshots,
        int calibrationWindow,
        int predictionHorizon,
        int step)
    {
        if (snapshots.Count < calibrationWindow + predictionHorizon + 1)
        {
            throw new ArgumentException("Not enough snapshots for backtesting with provided window/horizon.", nameof(snapshots));
        }

        var totalWindows = ((snapshots.Count - (calibrationWindow + predictionHorizon) - 1) / step) + 1;
        Console.WriteLine($"Starting deterministic backtest. Snapshots={snapshots.Count}, Calibration={calibrationWindow}, Horizon={predictionHorizon}, Step={step}, Windows={totalWindows}");

        var samples = new List<BacktestSample>(Math.Max(1, totalWindows));
        var factory = new AgentFactory();
        var simulator = new Simulator();

        decimal maeSum = 0m;
        double rmseSum = 0d;
        double volErrSum = 0d;
        int directionHits = 0;

        int windowIndex = 0;

        for (int start = 0; start + calibrationWindow + predictionHorizon < snapshots.Count; start += step)
        {
            var calibrationSnapshots = snapshots.Skip(start).Take(calibrationWindow).ToList();
            var calibrationLob = calibrationSnapshots.Select(s => s.LOB).ToList();
            var futureSnapshots = snapshots.Skip(start + calibrationWindow).Take(predictionHorizon).ToList();
            var futureLob = futureSnapshots.Select(s => s.LOB).ToList();

            var lastCalib = calibrationLob[^1];
            var initialMid = (lastCalib.BidPrice + lastCalib.AskPrice) * 0.5m;
            var tickSize = EstimateTickSize(calibrationLob);
            var levels = BuildLobLevels(calibrationLob);

            var projectedTape = BuildProjectedTapeFromCalibration(
                calibrationSnapshots.SelectMany(s => s.TapePrints).ToList(),
                predictionHorizon,
                initialMid,
                tickSize,
                lastCalib.Timestamp);

            if (projectedTape.Count == 0)
            {
                projectedTape = BuildProjectedTapeFromLob(calibrationLob, predictionHorizon);
            }

            var swarm = factory.GenerateDigitalTwinSwarm(levels, projectedTape, initialMid, tickSize);
            var prediction = simulator.RunDeterministic(swarm);

            var actualCloses = futureLob.Select(t => (t.BidPrice + t.AskPrice) * 0.5m).ToArray();
            var actualClose = actualCloses.Average();
            var actualVol = ComputeStdDev(actualCloses);

            var predictedClose = prediction.PredictedClose;
            var predictedVol = ComputeStdDev(new[] { initialMid, prediction.PredictedClose });

            var predictedUp = predictedClose >= initialMid;
            var actualUp = actualClose >= initialMid;
            if (predictedUp == actualUp)
            {
                directionHits++;
            }

            var absErr = Math.Abs(predictedClose - actualClose);
            maeSum += absErr;

            var diff = (double)(predictedClose - actualClose);
            rmseSum += diff * diff;
            volErrSum += Math.Abs(predictedVol - actualVol);

            var candles = BuildCandles(futureLob, lastCalib.Timestamp);
            var predictedPath = BuildPredictedPath(initialMid, predictedClose, candles.Count);

            samples.Add(new BacktestSample(
                WindowIndex: windowIndex,
                PredictedClose: predictedClose,
                ActualClose: actualClose,
                PredictedVolatility: predictedVol,
                ActualVolatility: actualVol,
                PredictedUp: predictedUp,
                ActualUp: actualUp,
                ActualCandles: candles,
                PredictedPath: predictedPath));

            if ((windowIndex + 1) % 5 == 0 || windowIndex == totalWindows - 1)
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

    private static List<LOBLevel> BuildLobLevels(IReadOnlyList<LOBTick> calibrationSlice)
    {
        var bidStats = new Dictionary<decimal, LevelStats>();
        var askStats = new Dictionary<decimal, LevelStats>();

        for (int i = 0; i < calibrationSlice.Count; i++)
        {
            var tick = calibrationSlice[i];
            UpdateStats(bidStats, tick.BidPrice, tick.BidSize, i > 0 ? calibrationSlice[i - 1].BidPrice : tick.BidPrice, i > 0 ? calibrationSlice[i - 1].BidSize : tick.BidSize);
            UpdateStats(askStats, tick.AskPrice, tick.AskSize, i > 0 ? calibrationSlice[i - 1].AskPrice : tick.AskPrice, i > 0 ? calibrationSlice[i - 1].AskSize : tick.AskSize);
        }

        var levels = new List<LOBLevel>(bidStats.Count + askStats.Count);

        foreach (var (price, stat) in bidStats)
        {
            var cancellationRate = stat.Canceled / (stat.Canceled + stat.Added + 0.000001m);
            levels.Add(new LOBLevel(price, stat.GetAverageVolume(), Side.Buy, cancellationRate));
        }

        foreach (var (price, stat) in askStats)
        {
            var cancellationRate = stat.Canceled / (stat.Canceled + stat.Added + 0.000001m);
            levels.Add(new LOBLevel(price, stat.GetAverageVolume(), Side.Sell, cancellationRate));
        }

        return levels;
    }

    private static List<TapePrint> BuildProjectedTapeFromCalibration(
        IReadOnlyList<TapePrint> calibrationTape,
        int predictionHorizon,
        decimal mid,
        decimal tickSize,
        DateTime start)
    {
        if (calibrationTape.Count == 0 || predictionHorizon <= 0)
        {
            return [];
        }

        var take = Math.Min(calibrationTape.Count, Math.Max(8, predictionHorizon * 4));
        var recent = calibrationTape.Skip(Math.Max(0, calibrationTape.Count - take)).ToArray();
        var projected = new List<TapePrint>(recent.Length);

        for (int i = 0; i < recent.Length; i++)
        {
            var p = recent[i];
            var price = p.Price <= 0m ? mid + (p.Side == Side.Buy ? tickSize : -tickSize) : p.Price;
            projected.Add(new TapePrint(p.Side, price, p.Quantity, start.AddMilliseconds((i + 1) * 150)));
        }

        return projected;
    }

    private static List<TapePrint> BuildProjectedTapeFromLob(IReadOnlyList<LOBTick> lobSlice, int predictionHorizon)
    {
        var prints = new List<TapePrint>(lobSlice.Count);
        if (lobSlice.Count < 2)
        {
            return prints;
        }

        for (int i = 1; i < lobSlice.Count; i++)
        {
            var prev = lobSlice[i - 1];
            var current = lobSlice[i];

            var buyQty = Math.Max(0m, prev.AskSize - current.AskSize);
            var sellQty = Math.Max(0m, prev.BidSize - current.BidSize);
            var midPrev = (prev.BidPrice + prev.AskPrice) * 0.5m;
            var midCurrent = (current.BidPrice + current.AskPrice) * 0.5m;

            if (buyQty == 0m && sellQty == 0m)
            {
                var fallbackQty = Math.Max(0.0001m, (prev.BidSize + prev.AskSize) * 0.01m);
                if (midCurrent > midPrev)
                {
                    prints.Add(new TapePrint(Side.Buy, current.AskPrice, fallbackQty, current.Timestamp));
                }
                else if (midCurrent < midPrev)
                {
                    prints.Add(new TapePrint(Side.Sell, current.BidPrice, fallbackQty, current.Timestamp));
                }

                continue;
            }

            if (buyQty >= sellQty && buyQty > 0m)
            {
                prints.Add(new TapePrint(Side.Buy, current.AskPrice, buyQty, current.Timestamp));
            }
            else if (sellQty > 0m)
            {
                prints.Add(new TapePrint(Side.Sell, current.BidPrice, sellQty, current.Timestamp));
            }
        }

        if (prints.Count > predictionHorizon * 4)
        {
            return prints.Skip(prints.Count - (predictionHorizon * 4)).ToList();
        }

        return prints;
    }

    private static decimal EstimateTickSize(IReadOnlyList<LOBTick> slice)
    {
        decimal minStep = decimal.MaxValue;

        for (int i = 1; i < slice.Count; i++)
        {
            var bidStep = Math.Abs(slice[i].BidPrice - slice[i - 1].BidPrice);
            var askStep = Math.Abs(slice[i].AskPrice - slice[i - 1].AskPrice);

            if (bidStep > 0m && bidStep < minStep) minStep = bidStep;
            if (askStep > 0m && askStep < minStep) minStep = askStep;
        }

        return minStep == decimal.MaxValue ? 0.01m : minStep;
    }

    private static void UpdateStats(Dictionary<decimal, LevelStats> stats, decimal price, decimal size, decimal prevPrice, decimal prevSize)
    {
        if (!stats.TryGetValue(price, out var stat))
        {
            stat = new LevelStats();
            stats[price] = stat;
        }

        stat.TotalVolume += size;
        stat.SeenCount++;

        if (price == prevPrice)
        {
            if (size < prevSize)
            {
                stat.Canceled += prevSize - size;
            }
            else if (size > prevSize)
            {
                stat.Added += size - prevSize;
            }
        }
        else
        {
            stat.Added += size;
        }
    }

    private sealed class LevelStats
    {
        public decimal TotalVolume;
        public int SeenCount;
        public decimal Added;
        public decimal Canceled;

        public decimal GetAverageVolume() => SeenCount == 0 ? 0m : TotalVolume / SeenCount;
    }

    private static List<WindowCandle> BuildCandles(IReadOnlyList<LOBTick> lobSlice, DateTime fallbackStart)
    {
        if (lobSlice.Count == 0)
        {
            return [];
        }

        var candles = new List<WindowCandle>(lobSlice.Count);
        var prevClose = (lobSlice[0].BidPrice + lobSlice[0].AskPrice) * 0.5m;

        for (int i = 0; i < lobSlice.Count; i++)
        {
            var t = lobSlice[i];
            var close = (t.BidPrice + t.AskPrice) * 0.5m;
            var open = i == 0 ? prevClose : candles[^1].Close;
            var high = Math.Max(open, close);
            var low = Math.Min(open, close);
            var ts = t.Timestamp == default ? fallbackStart.AddMilliseconds(i * 150) : t.Timestamp;

            candles.Add(new WindowCandle(ts, open, high, low, close));
            prevClose = close;
        }

        return candles;
    }

    private static List<decimal> BuildPredictedPath(decimal startPrice, decimal endPrice, int points)
    {
        if (points <= 0)
        {
            return [];
        }

        if (points == 1)
        {
            return [endPrice];
        }

        var path = new List<decimal>(points);
        var step = (endPrice - startPrice) / (points - 1);
        for (int i = 0; i < points; i++)
        {
            path.Add(startPrice + step * i);
        }

        return path;
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

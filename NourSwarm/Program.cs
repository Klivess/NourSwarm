using System.Globalization;
using System.Text.Json;

namespace NourSwarm;

internal static class Program
{
    private const int TickDelayMs = 150;

    private static async Task Main(string[] args)
    {
        var symbol = "BTCUSDT";
        var snapshots = 50000;

        var marketSnapshots = await LoadOrFetchHistoricalMarketSnapshotsAsync(symbol, snapshots, TickDelayMs);

        var marketDataService = new MarketDataService();
        marketDataService.Ingest(marketSnapshots.Select(s => s.LOB));

        var backtester = new Backtester();
        var backtest = backtester.Run(
            snapshots: marketSnapshots,
            calibrationWindow: 80,
            predictionHorizon: (1000*60)/ TickDelayMs, //(careful here, truncation!!!!) this ensures its predicting 1 minute into the future.
            step: 20);

        Console.WriteLine();
        Console.WriteLine("Backtest Results (BTC LOB)");
        Console.WriteLine($"Windows Evaluated: {backtest.WindowsEvaluated}");
        Console.WriteLine($"Directional Accuracy: {backtest.DirectionalAccuracy:P2}");
        Console.WriteLine($"MAE: {backtest.MeanAbsoluteError:F6}");
        Console.WriteLine($"RMSE: {backtest.RootMeanSquaredError:F6}");
        Console.WriteLine($"Volatility Error: {backtest.VolatilityForecastError:F6}");
        Console.WriteLine($"Tested across {Math.Round(TimeSpan.FromMilliseconds(TickDelayMs * marketSnapshots.Count).TotalHours, 3)} hours of data.");

        var folder = backtest.SaveWindowGraphsToDesktop();
        Console.WriteLine($"Graphs saved to: {folder}");
    }

    private static async Task<List<MarketSnapshot>> LoadOrFetchHistoricalMarketSnapshotsAsync(string symbol, int snapshots, int delayMs)
    {
        var cachePath = GetCachePath(symbol);

        if (File.Exists(cachePath))
        {
            try
            {
                await using var cacheStream = File.OpenRead(cachePath);
                var cached = await JsonSerializer.DeserializeAsync<List<MarketSnapshot>>(cacheStream);
                if (cached is { Count: > 0 })
                {
                    var tapeCount = cached.Sum(s => s.TapePrints.Count);
                    Console.WriteLine($"Loaded {Math.Min(cached.Count, snapshots)} snapshots from cache: {cachePath}. Tape prints: {tapeCount}");
                    return cached.Count > snapshots ? cached.Take(snapshots).ToList() : cached;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cache read failed. Fetching live data. Reason: {ex.Message}");
            }
        }

        var historicalSnapshots = await FetchHistoricalMarketSnapshotsAsync(symbol, snapshots, delayMs);

        try
        {
            await using var cacheWriteStream = File.Create(cachePath);
            await JsonSerializer.SerializeAsync(cacheWriteStream, historicalSnapshots);
            Console.WriteLine($"Saved {historicalSnapshots.Count} snapshots to cache: {cachePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cache write failed. Continuing without cache. Reason: {ex.Message}");
        }

        return historicalSnapshots;
    }

    private static string GetCachePath(string symbol)
    {
        var cacheDir = Path.Combine(AppContext.BaseDirectory, "cache");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, $"market_{symbol.ToUpperInvariant()}.json");
    }

    private static async Task<List<MarketSnapshot>> FetchHistoricalMarketSnapshotsAsync(string symbol, int snapshots, int delayMs)
    {
        Console.WriteLine($"Starting historical LOB+tape fetch for {symbol}. Snapshots: {snapshots}, Bucket: {delayMs}ms");

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var requestedDurationMs = (long)snapshots * delayMs;
        var targetStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - requestedDurationMs;
        var tapePrints = new List<TapePrint>(Math.Min(200_000, Math.Max(10_000, snapshots * 4)));

        var endTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var page = 0;

        while (page < 300)
        {
            using var tapeResponse = await http.GetAsync($"https://api.binance.com/api/v3/aggTrades?symbol={symbol}&endTime={endTimeMs}&limit=1000");
            tapeResponse.EnsureSuccessStatusCode();

            await using var tapeStream = await tapeResponse.Content.ReadAsStreamAsync();
            using var tapeDoc = await JsonDocument.ParseAsync(tapeStream);

            var batch = ParseAggTrades(tapeDoc.RootElement, out _)
                .OrderBy(t => t.Timestamp)
                .ToList();

            if (batch.Count == 0)
            {
                break;
            }

            tapePrints.AddRange(batch);

            var earliestInBatch = batch[0].Timestamp;
            endTimeMs = new DateTimeOffset(earliestInBatch).ToUnixTimeMilliseconds() - 1;
            page++;

            if ((page % 10) == 0)
            {
                Console.WriteLine($"Fetched {page} aggTrades pages. Tape prints so far: {tapePrints.Count}");
            }

            if (endTimeMs <= targetStartTimeMs)
            {
                break;
            }
        }

        tapePrints = tapePrints
            .OrderBy(t => t.Timestamp)
            .ThenBy(t => t.Price)
            .ThenBy(t => t.Quantity)
            .ToList();

        if (tapePrints.Count == 0)
        {
            throw new InvalidOperationException("No historical aggTrades were fetched.");
        }

        var snapshotsOut = BuildSnapshotsFromTapeBuckets(tapePrints, delayMs, snapshots);
        var totalTapePrints = snapshotsOut.Sum(s => s.TapePrints.Count);

        if (snapshotsOut.Count == 0)
        {
            throw new InvalidOperationException("No live market snapshots were fetched.");
        }

        Console.WriteLine($"Completed historical LOB+tape fetch. Total snapshots: {snapshotsOut.Count}, Total tape prints used: {totalTapePrints}");

        return snapshotsOut;
    }

    private static List<MarketSnapshot> BuildSnapshotsFromTapeBuckets(IReadOnlyList<TapePrint> sortedTape, int bucketMs, int requestedSnapshots)
    {
        var buckets = new List<List<TapePrint>>();

        var currentBucketStart = sortedTape[0].Timestamp;
        var currentBucket = new List<TapePrint>();

        for (int i = 0; i < sortedTape.Count; i++)
        {
            var trade = sortedTape[i];
            while ((trade.Timestamp - currentBucketStart).TotalMilliseconds >= bucketMs)
            {
                buckets.Add(currentBucket);
                currentBucket = new List<TapePrint>();
                currentBucketStart = currentBucketStart.AddMilliseconds(bucketMs);

                if (buckets.Count >= requestedSnapshots)
                {
                    break;
                }
            }

            if (buckets.Count >= requestedSnapshots)
            {
                break;
            }

            currentBucket.Add(trade);
        }

        if (buckets.Count < requestedSnapshots)
        {
            buckets.Add(currentBucket);
        }

        if (buckets.Count > requestedSnapshots)
        {
            buckets = buckets.Skip(buckets.Count - requestedSnapshots).ToList();
        }

        if (buckets.Count < requestedSnapshots)
        {
            Console.WriteLine($"Warning: requested {requestedSnapshots} snapshots but only {buckets.Count} could be built from available historical tape.");
        }

        var snapshots = new List<MarketSnapshot>(buckets.Count);

        for (int i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            if (bucket.Count > 0)
            {
                var vwap = ComputeVwap(bucket);
                var buyVol = bucket.Where(t => t.Side == Side.Buy).Sum(t => t.Quantity);
                var sellVol = bucket.Where(t => t.Side == Side.Sell).Sum(t => t.Quantity);

                var imbalance = (buyVol - sellVol) / (buyVol + sellVol + 0.000001m);
                var spread = Math.Max(0.01m, vwap * 0.00005m);

                var bid = decimal.Round(vwap - spread / 2m, 4);
                var ask = decimal.Round(vwap + spread / 2m, 4);
                var bidSize = Math.Max(0.0001m, buyVol);
                var askSize = Math.Max(0.0001m, sellVol);

                if (imbalance > 0)
                {
                    askSize = Math.Max(0.0001m, askSize * (1m - Math.Min(0.4m, imbalance)));
                }
                else if (imbalance < 0)
                {
                    bidSize = Math.Max(0.0001m, bidSize * (1m - Math.Min(0.4m, Math.Abs(imbalance))));
                }

                snapshots.Add(new MarketSnapshot(
                    new LOBTick(bid, bidSize, ask, askSize, bucket[^1].Timestamp),
                    bucket));
            }
        }

        return snapshots;
    }

    private static decimal ComputeVwap(IReadOnlyList<TapePrint> prints)
    {
        decimal notional = 0m;
        decimal volume = 0m;

        for (int i = 0; i < prints.Count; i++)
        {
            notional += prints[i].Price * prints[i].Quantity;
            volume += prints[i].Quantity;
        }

        return volume > 0m ? notional / volume : prints[^1].Price;
    }

    private static List<TapePrint> ParseAggTrades(JsonElement aggTradesArray, out long? maxAggTradeId)
    {
        maxAggTradeId = null;
        var prints = new List<TapePrint>();

        if (aggTradesArray.ValueKind != JsonValueKind.Array)
        {
            return prints;
        }

        foreach (var trade in aggTradesArray.EnumerateArray())
        {
            var id = trade.GetProperty("a").GetInt64();
            var price = decimal.Parse(trade.GetProperty("p").GetString()!, CultureInfo.InvariantCulture);
            var qty = decimal.Parse(trade.GetProperty("q").GetString()!, CultureInfo.InvariantCulture);
            var timeMs = trade.GetProperty("T").GetInt64();
            var buyerIsMaker = trade.GetProperty("m").GetBoolean();
            var side = buyerIsMaker ? Side.Sell : Side.Buy;

            prints.Add(new TapePrint(
                Side: side,
                Price: price,
                Quantity: qty,
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime));

            if (!maxAggTradeId.HasValue || id > maxAggTradeId.Value)
            {
                maxAggTradeId = id;
            }
        }

        return prints;
    }
}

using System.Globalization;
using System.Text.Json;

namespace NourSwarm;

internal static class Program
{
    const int tickDelayMS = 150;

    private static async Task Main(string[] args)
    {
        var marketDataService = new MarketDataService();
        var liveTicks = await LoadOrFetchLobTicksAsync(symbol: "BTCUSDT", snapshots: 1000, delayMs: tickDelayMS);
        marketDataService.Ingest(liveTicks);

        var backtester = new Backtester();
        var backtest = backtester.Run(
            ticks: marketDataService.GetHistory(),
            calibrationWindow: 80,
            predictionHorizon: (1000*60)/tickDelayMS, //(careful here, truncation!!!!) this ensures its predicting 1 minute into the future.
            step: 20,
            swarmSize: 100,
            seed: 7);

        Console.WriteLine();
        Console.WriteLine("Backtest Results (BTC LOB)");
        Console.WriteLine($"Windows Evaluated: {backtest.WindowsEvaluated}");
        Console.WriteLine($"Directional Accuracy: {backtest.DirectionalAccuracy:P2}");
        Console.WriteLine($"MAE: {backtest.MeanAbsoluteError:F6}");
        Console.WriteLine($"RMSE: {backtest.RootMeanSquaredError:F6}");
        Console.WriteLine($"Volatility Error: {backtest.VolatilityForecastError:F6}");
    }

    private static async Task<List<LOBTick>> LoadOrFetchLobTicksAsync(string symbol, int snapshots, int delayMs)
    {
        var cachePath = GetCachePath(symbol);

        if (File.Exists(cachePath))
        {
            try
            {
                await using var cacheStream = File.OpenRead(cachePath);
                var cached = await JsonSerializer.DeserializeAsync<List<LOBTick>>(cacheStream);
                if (cached is { Count: > 0 })
                {
                    Console.WriteLine($"Loaded {Math.Min(cached.Count, snapshots)} snapshots from cache: {cachePath}");
                    return cached.Count > snapshots ? cached.Take(snapshots).ToList() : cached;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cache read failed. Fetching live data. Reason: {ex.Message}");
            }
        }

        var liveTicks = await FetchLobTicksAsync(symbol, snapshots, delayMs);

        try
        {
            await using var cacheWriteStream = File.Create(cachePath);
            await JsonSerializer.SerializeAsync(cacheWriteStream, liveTicks);
            Console.WriteLine($"Saved {liveTicks.Count} snapshots to cache: {cachePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cache write failed. Continuing without cache. Reason: {ex.Message}");
        }

        return liveTicks;
    }

    private static string GetCachePath(string symbol)
    {
        var cacheDir = Path.Combine(AppContext.BaseDirectory, "cache");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, $"lobticks_{symbol.ToUpperInvariant()}.json");
    }

    private static async Task<List<LOBTick>> FetchLobTicksAsync(string symbol, int snapshots, int delayMs)
    {
        Console.WriteLine($"Starting live LOB fetch for {symbol}. Snapshots: {snapshots}, Delay: {delayMs}ms, Estimated time: {(snapshots*delayMs)/1000} seconds");

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var ticks = new List<LOBTick>(snapshots);

        for (int i = 0; i < snapshots; i++)
        {
            using var response = await http.GetAsync($"https://api.binance.com/api/v3/ticker/bookTicker?symbol={symbol}");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var root = doc.RootElement;
            var bidPrice = decimal.Parse(root.GetProperty("bidPrice").GetString()!, CultureInfo.InvariantCulture);
            var bidSize = decimal.Parse(root.GetProperty("bidQty").GetString()!, CultureInfo.InvariantCulture);
            var askPrice = decimal.Parse(root.GetProperty("askPrice").GetString()!, CultureInfo.InvariantCulture);
            var askSize = decimal.Parse(root.GetProperty("askQty").GetString()!, CultureInfo.InvariantCulture);

            ticks.Add(new LOBTick(bidPrice, bidSize, askPrice, askSize, DateTime.UtcNow));

            if ((i + 1) % 20 == 0 || i == snapshots - 1)
            {
                Console.WriteLine($"Fetched {i + 1}/{snapshots} snapshots...");
            }

            if (i < snapshots - 1)
            {
                await Task.Delay(delayMs);
            }
        }

        if (ticks.Count == 0)
        {
            throw new InvalidOperationException("No live LOB snapshots were fetched.");
        }

        Console.WriteLine($"Completed live LOB fetch. Total snapshots: {ticks.Count}");

        return ticks;
    }
}

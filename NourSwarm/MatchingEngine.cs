using System.Collections.Concurrent;

namespace NourSwarm;

public sealed class MatchingEngine
{
    private sealed class LimitOrderEntry
    {
        public required Order Order { get; init; }
        public decimal RemainingQuantity { get; set; }
        public bool IsCancelled { get; set; }
    }

    private readonly ConcurrentQueue<Order> _incoming = new();
    private readonly SortedDictionary<decimal, Queue<LimitOrderEntry>> _bids = new(Comparer<decimal>.Create((x, y) => y.CompareTo(x)));
    private readonly SortedDictionary<decimal, Queue<LimitOrderEntry>> _asks = [];
    private readonly Dictionary<Guid, List<LimitOrderEntry>> _ordersByAgent = [];
    private readonly Queue<(decimal Price, decimal Quantity)> _recentTrades = new();

    private const int MaxTradeWindow = 256;

    public decimal SimulatedPrice { get; private set; }

    public MatchingEngine(decimal initialPrice)
    {
        SimulatedPrice = initialPrice;
    }

    public void Process(Order order) => _incoming.Enqueue(order);

    public void CancelAgentOrders(Guid agentId)
    {
        if (_ordersByAgent.TryGetValue(agentId, out var entries))
        {
            foreach (var entry in entries)
            {
                entry.IsCancelled = true;
                entry.RemainingQuantity = 0m;
            }

            entries.Clear();
        }
    }

    public ExecutionBatch DrainIncomingAndMatch()
    {
        var aggressiveBuys = 0;
        var aggressiveSells = 0;
        var executedTrades = 0;

        while (_incoming.TryDequeue(out var order))
        {
            if (order.Quantity <= 0m)
            {
                continue;
            }

            if (order.Type == OrderType.Market)
            {
                var tradeCount = ExecuteMarket(order);
                executedTrades += tradeCount;
                if (tradeCount > 0)
                {
                    if (order.Side == Side.Buy) aggressiveBuys++;
                    else aggressiveSells++;
                }

                continue;
            }

            var remaining = order.Quantity;
            var tradeCountForLimit = ExecuteAgainstBook(order.Side, order.AgentId, order.Price, ref remaining, isAggressor: true);
            executedTrades += tradeCountForLimit;

            if (tradeCountForLimit > 0)
            {
                if (order.Side == Side.Buy) aggressiveBuys++;
                else aggressiveSells++;
            }

            if (remaining > 0m)
            {
                AddLimit(new Order(order.AgentId, order.Side, order.Price, remaining, OrderType.Limit));
            }
        }

        return new ExecutionBatch(executedTrades, aggressiveBuys, aggressiveSells);
    }

    public OrderBookSnapshot GetSnapshot()
    {
        var bestBid = GetBestPrice(_bids, Side.Buy) ?? SimulatedPrice - 0.01m;
        var bestAsk = GetBestPrice(_asks, Side.Sell) ?? SimulatedPrice + 0.01m;

        if (bestAsk <= bestBid)
        {
            bestAsk = bestBid + 0.01m;
        }

        var spread = Math.Max(0.0001m, bestAsk - bestBid);
        var mid = (bestBid + bestAsk) * 0.5m;
        var vwap = CalculateTradeVwap();

        var bidQty = GetTopQuantity(_bids);
        var askQty = GetTopQuantity(_asks);
        var imbalance = (bidQty - askQty) / (bidQty + askQty + 1m);

        return new OrderBookSnapshot(bestBid, bestAsk, mid, vwap, spread, imbalance);
    }

    private int ExecuteMarket(Order order)
    {
        var remaining = order.Quantity;
        return ExecuteAgainstBook(order.Side, order.AgentId, 0m, ref remaining, isAggressor: true);
    }

    private int ExecuteAgainstBook(Side side, Guid aggressorAgentId, decimal price, ref decimal remaining, bool isAggressor)
    {
        var tradeCount = 0;

        if (side == Side.Buy)
        {
            while (remaining > 0m && TryGetBestOrder(_asks, out var bestAskPrice, out var entry))
            {
                if (price > 0m && bestAskPrice > price)
                {
                    break;
                }

                var traded = Math.Min(remaining, entry.RemainingQuantity);
                remaining -= traded;
                entry.RemainingQuantity -= traded;
                tradeCount++;
                RegisterTrade(bestAskPrice, traded);
            }
        }
        else
        {
            while (remaining > 0m && TryGetBestOrder(_bids, out var bestBidPrice, out var entry))
            {
                if (price > 0m && bestBidPrice < price)
                {
                    break;
                }

                var traded = Math.Min(remaining, entry.RemainingQuantity);
                remaining -= traded;
                entry.RemainingQuantity -= traded;
                tradeCount++;
                RegisterTrade(bestBidPrice, traded);
            }
        }

        return tradeCount;
    }

    private void AddLimit(Order order)
    {
        var levels = order.Side == Side.Buy ? _bids : _asks;
        if (!levels.TryGetValue(order.Price, out var queue))
        {
            queue = new Queue<LimitOrderEntry>();
            levels[order.Price] = queue;
        }

        var entry = new LimitOrderEntry { Order = order, RemainingQuantity = order.Quantity };
        queue.Enqueue(entry);

        if (!_ordersByAgent.TryGetValue(order.AgentId, out var entries))
        {
            entries = [];
            _ordersByAgent[order.AgentId] = entries;
        }

        entries.Add(entry);
    }

    private void RegisterTrade(decimal price, decimal quantity)
    {
        SimulatedPrice = price;
        _recentTrades.Enqueue((price, quantity));

        while (_recentTrades.Count > MaxTradeWindow)
        {
            _recentTrades.Dequeue();
        }
    }

    private decimal CalculateTradeVwap()
    {
        if (_recentTrades.Count == 0)
        {
            return SimulatedPrice;
        }

        decimal notional = 0m;
        decimal volume = 0m;
        foreach (var (price, quantity) in _recentTrades)
        {
            notional += price * quantity;
            volume += quantity;
        }

        return volume > 0m ? notional / volume : SimulatedPrice;
    }

    private static decimal GetTopQuantity(SortedDictionary<decimal, Queue<LimitOrderEntry>> levels)
    {
        foreach (var level in levels)
        {
            decimal qty = 0m;
            foreach (var entry in level.Value)
            {
                if (!entry.IsCancelled && entry.RemainingQuantity > 0m)
                {
                    qty += entry.RemainingQuantity;
                }
            }

            if (qty > 0m)
            {
                return qty;
            }
        }

        return 0m;
    }

    private static decimal? GetBestPrice(SortedDictionary<decimal, Queue<LimitOrderEntry>> levels, Side side)
    {
        foreach (var level in levels)
        {
            while (level.Value.Count > 0 && (level.Value.Peek().IsCancelled || level.Value.Peek().RemainingQuantity <= 0m))
            {
                level.Value.Dequeue();
            }

            if (level.Value.Count > 0)
            {
                return level.Key;
            }
        }

        return null;
    }

    private static bool TryGetBestOrder(SortedDictionary<decimal, Queue<LimitOrderEntry>> levels, out decimal price, out LimitOrderEntry entry)
    {
        foreach (var level in levels)
        {
            while (level.Value.Count > 0 && (level.Value.Peek().IsCancelled || level.Value.Peek().RemainingQuantity <= 0m))
            {
                level.Value.Dequeue();
            }

            if (level.Value.Count > 0)
            {
                price = level.Key;
                entry = level.Value.Peek();
                return true;
            }
        }

        price = 0m;
        entry = null!;
        return false;
    }
}

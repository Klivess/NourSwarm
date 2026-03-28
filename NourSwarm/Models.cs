namespace NourSwarm;

public record LOBTick(decimal BidPrice, decimal BidSize, decimal AskPrice, decimal AskSize, DateTime Timestamp);

public record OCEAN(int Openness, int Conscientiousness, int Extraversion, int Agreeableness, int Neuroticism);

public record AgentDna(double Openness, double Conscientiousness, double Extraversion, double Agreeableness, double Neuroticism);

public enum OrderType { Market, Limit }
public enum Side { Buy, Sell }

public record Order(Guid AgentId, Side Side, decimal Price, decimal Quantity, OrderType Type);

public readonly record struct OrderBookSnapshot(decimal BestBid, decimal BestAsk, decimal MidPrice, decimal Vwap, decimal Spread, decimal Imbalance);

public readonly record struct AgentAction(bool CancelAllOrders, Order? Order);

public record PredictionResult(decimal ExpectedCandleClose, double VolatilityForecast, double ConvictionScore, IReadOnlyList<decimal> SimulatedCloses);

public readonly record struct ExecutionBatch(int ExecutedTrades, int AggressiveBuys, int AggressiveSells);

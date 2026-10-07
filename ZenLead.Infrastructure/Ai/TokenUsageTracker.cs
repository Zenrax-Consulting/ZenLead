namespace ZenLead.Infrastructure.Ai;

public static class TokenUsageTracker
{
    private static long _totalTokens;

    public static void Add(int tokens) => Interlocked.Add(ref _totalTokens, tokens);
    public static long Total => Interlocked.Read(ref _totalTokens);
}

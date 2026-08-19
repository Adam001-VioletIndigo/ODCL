using System.Text.Json;

namespace ODCL;

public static class DiskStrategy
{
    public const double RebuildMargin = 1.2;
    public const double FastPathFactor = 1.5;
    private const double WalBudgetFraction = 0.25;
    private const long Overhead = 1_000_000;

    public static long EstimateFinal(long keptEventBytes, long keptRelatedBytes)
        => (long)((keptEventBytes + keptRelatedBytes) * RebuildMargin) + Overhead;

    public static bool CanRebuild(long free, long estFinal)
        => free >= (long)(estFinal * RebuildMargin);

    public static bool NeedBatch(long free, long estFinal)
        => free < (long)(estFinal * FastPathFactor);

    public static int BatchRows(long free, int pageSize, long avgEventBytes)
    {
        long pagesPerRow = (long)Math.Max(1, Math.Ceiling((double)avgEventBytes / pageSize));
        long budget = (long)(free * WalBudgetFraction);
        long rows = budget / (pagesPerRow * pageSize);
        return (int)Math.Clamp(rows, 100, 5000);
    }

    public static void SelfCheck()
    {
        Assert(EstimateFinal(0, 0) == Overhead, "EstimateFinal base");
        long est = EstimateFinal(100_000_000, 50_000_000);
        Assert(!CanRebuild(est, est), "CanRebuild requires margin");
        Assert(CanRebuild(est * 3, est), "CanRebuild generous");
        Assert(!NeedBatch(est * 10, est), "big free -> fast path");
        Assert(NeedBatch(est, est), "tight free -> batch");
        Assert(NeedBatch((long)(est * 1.3), est), "medium free -> batch");

        long avg = 20_000;
        int ps = 4096;
        long free = 100L * 1024 * 1024;
        int rows = BatchRows(free, ps, avg);
        Assert(rows >= 100 && rows <= 5000, $"BatchRows bounds got {rows}");
        double pages = Math.Ceiling((double)avg / ps);
        Assert(rows * pages * ps <= free * WalBudgetFraction,
            $"batch WAL budget rows={rows} need={rows * pages * ps} budget={free * WalBudgetFraction}");

        int bigRows = BatchRows(5_000_000_000L, ps, avg);
        Assert(bigRows == 5000, $"BatchRows cap got {bigRows}");
    }

    private static void Assert(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException("SELFSHECK FAIL: " + what);
    }
}
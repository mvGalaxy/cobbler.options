namespace YourNamespace.Services
{

    /// <summary>
    /// Result of multi-day intraday realized vol computation.
    /// Pools all 15-min returns across N trading sessions into one vol number.
    /// </summary>
    public class RollingIntradayVolResult
    {
        public string Ticker { get; set; } = "";
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }

        /// <summary>Annualized realized vol from pooled 15-min returns.</summary>
        public double RealizedVol { get; set; }

        /// <summary>Total 15-min log returns used (across all sessions).</summary>
        public int TotalReturns { get; set; }

        /// <summary>Number of trading sessions with bar data.</summary>
        public int TradingSessions { get; set; }

        /// <summary>Average bars per session (should be ~26 for a full day).</summary>
        public double AvgBarsPerSession { get; set; }

        /// <summary>Daily variance (not annualized) — useful for comparison.</summary>
        public double DailyVariance { get; set; }

        /// <summary>Per-session breakdown: each day's standalone intraday vol.</summary>
        public List<(DateTime Date, double Vol, int Bars)> SessionBreakdown { get; set; } = new();

        public override string ToString() =>
            $"{Ticker}  {FromDate:yyyy-MM-dd} → {ToDate:yyyy-MM-dd}  " +
            $"Sessions:{TradingSessions}  Returns:{TotalReturns}  " +
            $"RealizedVol:{RealizedVol:P2}";
    }
}


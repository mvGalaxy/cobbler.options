namespace YourNamespace.Services
{
    public static class VolEstimators
    {
        private const double TradingDays = 252.0;
        private const double BarsPerDay = 26.0;   // 6.5 hrs / 0.25 hr = 26 fifteen-min bars

        /// <summary>
        /// Realized vol from intraday bar closes (e.g. 15-min bars for one day).
        /// Annualized by bars-per-day × trading-days-per-year.
        /// </summary>
        public static double IntradayRealizedVol(decimal[] barCloses)
        {
            if (barCloses.Length < 3) return 0;

            var logReturns = new double[barCloses.Length - 1];
            for (int i = 1; i < barCloses.Length; i++)
                logReturns[i - 1] = Math.Log((double)(barCloses[i] / barCloses[i - 1]));

            double mean = logReturns.Average();
            double variance = logReturns.Sum(r => (r - mean) * (r - mean)) / (logReturns.Length - 1);

            // Each bar is 1/26th of a day, so multiply by bars-per-day to get daily variance,
            // then by 252 to annualize
            return Math.Sqrt(variance * BarsPerDay * TradingDays);
        }

        /// <summary>
        /// Multi-day intraday realized vol: pools 15-min returns across N trading sessions.
        /// Bars must be grouped by session (list of daily bar arrays) so overnight gaps are excluded.
        /// Returns annualized vol + per-session breakdown.
        /// </summary>
        /// <param name="sessionBars">
        /// Each inner array is one trading day's 15-min closes, ordered chronologically.
        /// Overnight returns between sessions are NOT included.
        /// </param>
        public static (double annualizedVol, double dailyVariance, List<(DateTime date, double vol, int bars)> breakdown)
            MultiDayIntradayVol(IList<(DateTime date, decimal[] closes)> sessionBars)
        {
            if (sessionBars.Count == 0) return (0, 0, new());

            var allReturns = new List<double>();
            var breakdown = new List<(DateTime date, double vol, int bars)>();

            foreach (var (date, closes) in sessionBars)
            {
                if (closes.Length < 3) continue;  // need at least 3 bars for meaningful returns

                var dayReturns = new double[closes.Length - 1];
                for (int i = 1; i < closes.Length; i++)
                    dayReturns[i - 1] = Math.Log((double)(closes[i] / closes[i - 1]));

                allReturns.AddRange(dayReturns);

                // Per-session standalone vol for breakdown
                double dayMean = dayReturns.Average();
                double dayVar = dayReturns.Sum(r => (r - dayMean) * (r - dayMean)) / (dayReturns.Length - 1);
                double dayVol = Math.Sqrt(dayVar * BarsPerDay * TradingDays);
                breakdown.Add((date, dayVol, closes.Length));
            }

            if (allReturns.Count < 2) return (0, 0, breakdown);

            // Pool all intra-session returns, compute one variance
            double mean = allReturns.Average();
            double variance = allReturns.Sum(r => (r - mean) * (r - mean)) / (allReturns.Count - 1);

            // Each return is a 15-min interval → multiply by bars-per-day to get daily variance,
            // then by 252 to annualize
            double annualized = Math.Sqrt(variance * BarsPerDay * TradingDays);

            // Daily variance = variance * bars-per-day (not annualized, for comparison)
            double dailyVar = variance * BarsPerDay;

            return (annualized, dailyVar, breakdown);
        }

        /// <summary>
        /// Standard close-to-close historical volatility from daily closes.
        /// </summary>
        public static double CloseToClose(decimal[] dailyCloses)
        {
            if (dailyCloses.Length < 3) return 0;

            var logReturns = new double[dailyCloses.Length - 1];
            for (int i = 1; i < dailyCloses.Length; i++)
                logReturns[i - 1] = Math.Log((double)(dailyCloses[i] / dailyCloses[i - 1]));

            double mean = logReturns.Average();
            double variance = logReturns.Sum(r => (r - mean) * (r - mean)) / (logReturns.Length - 1);
            return Math.Sqrt(variance * TradingDays);
        }

        /// <summary>
        /// Parkinson estimator: uses High-Low range. ~5x more efficient than close-to-close.
        /// </summary>
        public static double Parkinson(IList<(decimal high, decimal low)> dailyHL)
        {
            if (dailyHL.Count < 2) return 0;

            double sum = 0;
            foreach (var (h, l) in dailyHL)
            {
                double hl = Math.Log((double)(h / l));
                sum += hl * hl;
            }

            double variance = sum / (4.0 * Math.Log(2.0) * dailyHL.Count);
            return Math.Sqrt(variance * TradingDays);
        }

        /// <summary>
        /// Single-day Parkinson variance contribution (not annualized).
        /// Store this daily, then rolling-average + annualize over N days.
        /// </summary>
        public static double ParkinsonDailyVariance(decimal high, decimal low)
        {
            double hl = Math.Log((double)(high / low));
            return hl * hl / (4.0 * Math.Log(2.0));
        }

        /// <summary>
        /// Garman-Klass estimator: uses OHLC. More efficient than Parkinson.
        /// </summary>
        public static double GarmanKlass(IList<(decimal open, decimal high, decimal low, decimal close)> dailyOHLC)
        {
            if (dailyOHLC.Count < 2) return 0;

            double sum = 0;
            foreach (var (o, h, l, c) in dailyOHLC)
            {
                double hl = Math.Log((double)(h / l));
                double co = Math.Log((double)(c / o));
                sum += 0.5 * hl * hl - (2.0 * Math.Log(2.0) - 1.0) * co * co;
            }

            double variance = sum / dailyOHLC.Count;
            return Math.Sqrt(variance * TradingDays);
        }

        /// <summary>
        /// Single-day Garman-Klass variance contribution (not annualized).
        /// </summary>
        public static double GarmanKlassDailyVariance(decimal open, decimal high, decimal low, decimal close)
        {
            double hl = Math.Log((double)(high / low));
            double co = Math.Log((double)(close / open));
            return 0.5 * hl * hl - (2.0 * Math.Log(2.0) - 1.0) * co * co;
        }

        /// <summary>
        /// Yang-Zhang estimator: best OHLC estimator. Handles overnight jumps + intraday range.
        /// Requires open, high, low, close + previous close for overnight return.
        /// </summary>
        public static double YangZhang(
            IList<(decimal prevClose, decimal open, decimal high, decimal low, decimal close)> data)
        {
            if (data.Count < 3) return 0;

            int n = data.Count;
            double k = 0.34 / (1.34 + (n + 1.0) / (n - 1.0));

            // Overnight returns: log(open / prevClose)
            var overnightReturns = data.Select(d => Math.Log((double)(d.open / d.prevClose))).ToArray();
            double oMean = overnightReturns.Average();
            double overnightVar = overnightReturns.Sum(r => (r - oMean) * (r - oMean)) / (n - 1);

            // Close-to-open returns: log(close / open)
            var closeOpenReturns = data.Select(d => Math.Log((double)(d.close / d.open))).ToArray();
            double cMean = closeOpenReturns.Average();
            double closeOpenVar = closeOpenReturns.Sum(r => (r - cMean) * (r - cMean)) / (n - 1);

            // Rogers-Satchell variance (intraday component)
            double rsSum = 0;
            foreach (var d in data)
            {
                double ho = Math.Log((double)(d.high / d.open));
                double hc = Math.Log((double)(d.high / d.close));
                double lo = Math.Log((double)(d.low / d.open));
                double lc = Math.Log((double)(d.low / d.close));
                rsSum += ho * hc + lo * lc;
            }
            double rsVar = rsSum / n;

            double variance = overnightVar + k * closeOpenVar + (1.0 - k) * rsVar;
            return Math.Sqrt(Math.Max(variance, 0) * TradingDays);
        }
    }
}

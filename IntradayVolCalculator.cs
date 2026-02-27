using System.Globalization;

namespace YourNamespace.Services
{
    /// <summary>
    /// Pure calculation layer (no HTTP, no SQL). Safe to unit test.
    /// </summary>
    public sealed class IntradayVolCalculator
    {
        public DailyVolSnapshot ComputeDailyVolSnapshot(
            string ticker,
            DateTime date,
            IReadOnlyList<AggBar> intradayBars,
            IReadOnlyList<AggBar> dailyBars,
            int rollingDays = 30)
        {
            if (intradayBars is null) throw new ArgumentNullException(nameof(intradayBars));
            if (dailyBars is null) throw new ArgumentNullException(nameof(dailyBars));

            var orderedDaily = dailyBars.OrderBy(b => b.Timestamp).ToList();

            var todayBar = orderedDaily.LastOrDefault()
                ?? throw new Exception($"No daily bar found for {ticker} on {date:yyyy-MM-dd}");

            var prevBar = orderedDaily.Count >= 2
                ? orderedDaily[^2]
                : todayBar;

            // Intraday realized vol from 15-min closes
            var intradayCloses = intradayBars
                .OrderBy(b => b.Timestamp)
                .Select(b => b.Close)
                .ToArray();

            double intradayVol = VolEstimators.IntradayRealizedVol(intradayCloses);

            // Rolling close-to-close vol
            var rollingCloses = orderedDaily
                .TakeLast(rollingDays + 1)
                .Select(b => b.Close)
                .ToArray();

            double ccVol = VolEstimators.CloseToClose(rollingCloses);

            // Rolling Parkinson
            var rollingHL = orderedDaily
                .TakeLast(rollingDays)
                .Select(b => (b.High, b.Low))
                .ToList();

            double pkVol = VolEstimators.Parkinson(rollingHL);

            // Rolling Garman-Klass
            var rollingOHLC = orderedDaily
                .TakeLast(rollingDays)
                .Select(b => (b.Open, b.High, b.Low, b.Close))
                .ToList();

            double gkVol = VolEstimators.GarmanKlass(rollingOHLC);

            // Rolling Yang-Zhang (needs prev close for overnight return)
            var yzData = new List<(decimal prevClose, decimal open, decimal high, decimal low, decimal close)>();
            for (int i = Math.Max(1, orderedDaily.Count - rollingDays); i < orderedDaily.Count; i++)
            {
                yzData.Add((
                    orderedDaily[i - 1].Close,
                    orderedDaily[i].Open,
                    orderedDaily[i].High,
                    orderedDaily[i].Low,
                    orderedDaily[i].Close
                ));
            }

            double yzVol = VolEstimators.YangZhang(yzData);

            return new DailyVolSnapshot
            {
                Ticker = ticker,
                Date = date.Date,
                OpenPrice = todayBar.Open,
                HighPrice = todayBar.High,
                LowPrice = todayBar.Low,
                ClosePrice = todayBar.Close,
                PrevClose = prevBar.Close,
                IntradayRealizedVol = intradayVol,
                IntradayBarCount = intradayCloses.Length,
                CloseToCloseVol = ccVol,
                ParkinsonVol = pkVol,
                GarmanKlassVol = gkVol,
                YangZhangVol = yzVol,
                RollingWindowDays = Math.Min(rollingDays, Math.Max(0, rollingCloses.Length - 1)),
                DailyParkinsonVar = VolEstimators.ParkinsonDailyVariance(todayBar.High, todayBar.Low),
                DailyGarmanKlassVar = VolEstimators.GarmanKlassDailyVariance(
                                          todayBar.Open, todayBar.High, todayBar.Low, todayBar.Close),
            };
        }

        public (double realizedVol, double ivSpread) ComputeIntradayVolVsIV(
            IReadOnlyList<AggBar> intradayBars,
            double currentIV)
        {
            if (intradayBars is null) throw new ArgumentNullException(nameof(intradayBars));

            var closes = intradayBars
                .OrderBy(b => b.Timestamp)
                .Select(b => b.Close)
                .ToArray();

            double rv = VolEstimators.IntradayRealizedVol(closes);
            return (rv, rv - currentIV);
        }

        public RollingIntradayVolResult ComputeRollingIntradayVol(
            string ticker,
            int days,
            IReadOnlyList<AggBar> allBars,
            TimeZoneInfo easternTimeZone)
        {
            if (allBars is null) throw new ArgumentNullException(nameof(allBars));
            if (easternTimeZone is null) throw new ArgumentNullException(nameof(easternTimeZone));
            if (days <= 0) throw new ArgumentOutOfRangeException(nameof(days));

            if (allBars.Count == 0)
                throw new Exception("No 15-min bar data provided.");

            var sessionGroups = allBars
                .OrderBy(b => b.Timestamp)
                .GroupBy(b =>
                {
                    var utc = DateTimeOffset.FromUnixTimeMilliseconds(b.Timestamp).UtcDateTime;
                    var et = TimeZoneInfo.ConvertTimeFromUtc(utc, easternTimeZone);
                    return et.Date;
                })
                .OrderBy(g => g.Key)
                .ToList();

            var sessions = sessionGroups
                .TakeLast(days)
                .Select(g => (
                    date: g.Key,
                    closes: g.OrderBy(b => b.Timestamp).Select(b => b.Close).ToArray()
                ))
                .ToList();

            if (sessions.Count == 0)
                throw new Exception("No trading sessions could be formed from the provided bars.");

            var (annVol, dailyVar, breakdown) = VolEstimators.MultiDayIntradayVol(sessions);

            return new RollingIntradayVolResult
            {
                Ticker = ticker,
                FromDate = sessions.First().date,
                ToDate = sessions.Last().date,
                RealizedVol = annVol,
                TotalReturns = sessions.Sum(s => Math.Max(0, s.closes.Length - 1)),
                TradingSessions = sessions.Count,
                AvgBarsPerSession = sessions.Average(s => (double)s.closes.Length),
                DailyVariance = dailyVar,
                SessionBreakdown = breakdown,
            };
        }
    }
}
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace YourNamespace.Services
{
    public class IntradayVolService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string? _connStr;
        private readonly JsonSerializerOptions _json;
        private readonly IntradayVolCalculator _calculator;
        private const string Base = "https://api.massive.com";

        /// <param name="apiKey">Massive API key</param>
        /// <param name="sqlConnectionString">SQL Server connection string (optional, pass null to skip persistence)</param>
        public IntradayVolService(
            string apiKey,
            string? sqlConnectionString = null,
            HttpClient? httpClient = null,
            IntradayVolCalculator? calculator = null)
        {
            _apiKey = apiKey;
            _connStr = sqlConnectionString;
            _http = httpClient ?? new HttpClient();
            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _calculator = calculator ?? new IntradayVolCalculator();
        }

        /// <summary>
        /// Compute all vol metrics for a ticker on a specific date.
        /// Pulls 15-min bars for intraday + daily bars for rolling OHLC estimators.
        /// </summary>
        /// <param name="ticker">Stock ticker</param>
        /// <param name="date">Date to compute vol for</param>
        /// <param name="rollingDays">Window for rolling OHLC estimators (default 30)</param>
        /// <param name="persist">If true and connStr was provided, saves to SQL</param>
        public async Task<DailyVolSnapshot> ComputeDailyVolAsync(
            string ticker,
            DateTime date,
            int rollingDays = 30,
            bool persist = true,
            CancellationToken ct = default)
        {
            var dateStr = date.ToString("yyyy-MM-dd");

            // 1. Fetch 15-minute bars for the target date
            var intradayBars = await GetAggsAsync(ticker, 15, "minute", dateStr, dateStr, ct);

            // 2. Fetch daily bars for rolling window (extra buffer for weekends/holidays)
            int calDays = (int)(rollingDays * 1.6) + 10;
            var fromDate = date.AddDays(-calDays).ToString("yyyy-MM-dd");
            var dailyBars = await GetAggsAsync(ticker, 1, "day", fromDate, dateStr, ct);

            // 3..9 Calculate (pure)
            var snapshot = _calculator.ComputeDailyVolSnapshot(
                ticker: ticker,
                date: date,
                intradayBars: intradayBars,
                dailyBars: dailyBars,
                rollingDays: rollingDays);

            // 10. Persist if configured
            if (persist && !string.IsNullOrEmpty(_connStr))
                await SaveToSqlAsync(snapshot, ct);

            return snapshot;
        }

        /// <summary>
        /// Quick check: today's intraday realized vol vs a reference IV.
        /// Positive = vol is running hot vs implied. Negative = quiet day.
        /// </summary>
        public async Task<(double realizedVol, double ivSpread)> IntradayVolVsIVAsync(
            string ticker, double currentIV, CancellationToken ct = default)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var bars = await GetAggsAsync(ticker, 15, "minute", today, today, ct);

            return _calculator.ComputeIntradayVolVsIV(bars, currentIV);
        }

        /// <summary>
        /// Multi-day intraday realized vol: pulls N trading days of 15-min bars,
        /// groups by session, excludes overnight gaps, pools all intra-session returns
        /// into one annualized vol number.
        /// </summary>
        public async Task<RollingIntradayVolResult> ComputeRollingIntradayVolAsync(
            string ticker,
            int days = 30,
            DateTime? asOfDate = null,
            bool persist = true,
            CancellationToken ct = default)
        {
            var endDate = (asOfDate ?? DateTime.UtcNow).Date;

            // Over-fetch calendar days to cover weekends/holidays
            int calDays = (int)(days * 1.6) + 10;
            var startDate = endDate.AddDays(-calDays);

            var fromStr = startDate.ToString("yyyy-MM-dd");
            var toStr = endDate.ToString("yyyy-MM-dd");

            var allBars = await GetAggsAsync(ticker, 15, "minute", fromStr, toStr, ct);
            if (allBars.Count == 0)
                throw new Exception($"No 15-min bar data for {ticker} from {fromStr} to {toStr}");

            var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

            var result = _calculator.ComputeRollingIntradayVol(
                ticker: ticker,
                days: days,
                allBars: allBars,
                easternTimeZone: eastern);

            // Persist to SQL if configured
            if (persist && !string.IsNullOrEmpty(_connStr))
                await SaveRollingIntradayToSqlAsync(result, ct);

            return result;
        }

        /// <summary>
        /// Compute vol snapshots for a date range. Useful for backfilling history.
        /// </summary>
        public async Task<List<DailyVolSnapshot>> ComputeVolRangeAsync(
            string ticker,
            DateTime from,
            DateTime to,
            int rollingDays = 30,
            bool persist = true,
            CancellationToken ct = default)
        {
            var dailyBars = await GetAggsAsync(ticker, 1, "day",
                from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), ct);

            var tradingDates = dailyBars
                .OrderBy(b => b.Timestamp)
                .Select(b => b.DateUtc)
                .Distinct()
                .ToList();

            var results = new List<DailyVolSnapshot>();

            foreach (var date in tradingDates)
            {
                try
                {
                    var snap = await ComputeDailyVolAsync(ticker, date, rollingDays, persist, ct);
                    results.Add(snap);
                    await Task.Delay(250, ct);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Skipping {ticker} {date:yyyy-MM-dd}: {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// Compare multi-day intraday realized vol against current implied volatility.
        /// This is the premium harvesting metric: if IV > realized, short vol is profitable.
        /// </summary>
        public async Task<(double realizedVol, double ivSpread, RollingIntradayVolResult detail)>
            RollingIntradayVsIVAsync(
                string ticker,
                double currentIV,
                int days = 30,
                CancellationToken ct = default)
        {
            var result = await ComputeRollingIntradayVolAsync(ticker, days, ct: ct);
            return (result.RealizedVol, result.RealizedVol - currentIV, result);
        }

        // ── SQL Persistence ──────────────────────────────────────────────

        public async Task EnsureTableExistsAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_connStr)) return;

            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);

            var cmd = new SqlCommand(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DailyVolSnapshots')
            CREATE TABLE DailyVolSnapshots (
                Id                   INT IDENTITY(1,1) PRIMARY KEY,
                Ticker               VARCHAR(10)   NOT NULL,
                [Date]               DATE           NOT NULL,
                OpenPrice            DECIMAL(18,4),
                HighPrice            DECIMAL(18,4),
                LowPrice             DECIMAL(18,4),
                ClosePrice           DECIMAL(18,4),
                PrevClose            DECIMAL(18,4),
                IntradayRealizedVol  FLOAT,
                IntradayBarCount     INT,
                CloseToCloseVol      FLOAT,
                ParkinsonVol         FLOAT,
                GarmanKlassVol       FLOAT,
                YangZhangVol         FLOAT,
                RollingWindowDays    INT,
                DailyParkinsonVar    FLOAT,
                DailyGarmanKlassVar  FLOAT,
                ComputedAt           DATETIME2      DEFAULT SYSUTCDATETIME(),

                INDEX IX_VolSnap_Ticker_Date NONCLUSTERED (Ticker, [Date]),
                CONSTRAINT UQ_VolSnap_Ticker_Date UNIQUE (Ticker, [Date])
            );

            -- Companion view for quick analysis
            IF NOT EXISTS (SELECT * FROM sys.views WHERE name = 'vw_VolComparison')
            EXEC('
                CREATE VIEW vw_VolComparison AS
                SELECT
                    Ticker,
                    [Date],
                    ClosePrice,
                    IntradayRealizedVol,
                    YangZhangVol,
                    CloseToCloseVol,
                    -- Vol spread: how much hotter/cooler intraday was vs rolling YZ
                    IntradayRealizedVol - YangZhangVol AS IntradayVsYZ,
                    -- Day-over-day vol change
                    IntradayRealizedVol - LAG(IntradayRealizedVol) 
                        OVER (PARTITION BY Ticker ORDER BY [Date]) AS IntradayVolChange,
                    -- 5-day moving average of intraday vol
                    AVG(IntradayRealizedVol) 
                        OVER (PARTITION BY Ticker ORDER BY [Date] 
                              ROWS BETWEEN 4 PRECEDING AND CURRENT ROW) AS IntradayVol5dMA
                FROM DailyVolSnapshots
            ');
        ", conn);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Creates the RollingIntradayVol table for multi-day pooled intraday vol.
        /// Call once at startup alongside EnsureTableExistsAsync.
        /// </summary>
        public async Task EnsureRollingIntradayTableAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_connStr)) return;

            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);

            var cmd = new SqlCommand(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'RollingIntradayVol')
            CREATE TABLE RollingIntradayVol (
                Id                INT IDENTITY(1,1) PRIMARY KEY,
                Ticker            VARCHAR(10)   NOT NULL,
                AsOfDate          DATE          NOT NULL,   -- last session date
                FromDate          DATE          NOT NULL,   -- first session date
                WindowDays        INT           NOT NULL,   -- requested trading days
                TradingSessions   INT           NOT NULL,   -- actual sessions with data
                TotalReturns      INT           NOT NULL,   -- total 15-min returns pooled
                AvgBarsPerSession FLOAT,
                RealizedVol       FLOAT         NOT NULL,   -- annualized
                DailyVariance     FLOAT,                    -- non-annualized daily
                ComputedAt        DATETIME2     DEFAULT SYSUTCDATETIME(),

                INDEX IX_RollIntra_Ticker_Date NONCLUSTERED (Ticker, AsOfDate),
                CONSTRAINT UQ_RollIntra_Ticker_Date_Window UNIQUE (Ticker, AsOfDate, WindowDays)
            );

            -- View: compare rolling intraday vol across windows and vs OHLC estimators
            IF NOT EXISTS (SELECT * FROM sys.views WHERE name = 'vw_RollingIntradayVsOHLC')
            EXEC('
                CREATE VIEW vw_RollingIntradayVsOHLC AS
                SELECT
                    r.Ticker,
                    r.AsOfDate,
                    r.WindowDays,
                    r.TradingSessions,
                    r.TotalReturns,
                    r.RealizedVol       AS IntradayRealizedVol,
                    d.YangZhangVol,
                    d.CloseToCloseVol,
                    d.ParkinsonVol,
                    d.GarmanKlassVol,
                    -- How much intraday movement C2C misses
                    r.RealizedVol - d.CloseToCloseVol   AS IntradayVsC2C,
                    -- Intraday vs best OHLC estimator
                    r.RealizedVol - d.YangZhangVol      AS IntradayVsYZ,
                    -- Ratio: >1 means intraday captures more vol than C2C
                    CASE WHEN d.CloseToCloseVol > 0
                         THEN r.RealizedVol / d.CloseToCloseVol
                         ELSE NULL END                  AS IntradayToC2CRatio
                FROM RollingIntradayVol r
                LEFT JOIN DailyVolSnapshots d
                    ON r.Ticker = d.Ticker AND r.AsOfDate = d.[Date]
            ');
        ", conn);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task SaveRollingIntradayToSqlAsync(RollingIntradayVolResult result, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);

            var cmd = new SqlCommand(@"
            MERGE RollingIntradayVol AS target
            USING (SELECT @Ticker AS Ticker, @AsOfDate AS AsOfDate, @WindowDays AS WindowDays) AS source
            ON target.Ticker = source.Ticker
               AND target.AsOfDate = source.AsOfDate
               AND target.WindowDays = source.WindowDays
            WHEN MATCHED THEN UPDATE SET
                FromDate          = @FromDate,
                TradingSessions   = @Sessions,
                TotalReturns      = @Returns,
                AvgBarsPerSession = @AvgBars,
                RealizedVol       = @Vol,
                DailyVariance     = @DailyVar,
                ComputedAt        = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (Ticker, AsOfDate, FromDate, WindowDays, TradingSessions,
                 TotalReturns, AvgBarsPerSession, RealizedVol, DailyVariance)
            VALUES
                (@Ticker, @AsOfDate, @FromDate, @WindowDays, @Sessions,
                 @Returns, @AvgBars, @Vol, @DailyVar);
        ", conn);

            cmd.Parameters.AddWithValue("@Ticker", result.Ticker);
            cmd.Parameters.AddWithValue("@AsOfDate", result.ToDate);
            cmd.Parameters.AddWithValue("@FromDate", result.FromDate);
            cmd.Parameters.AddWithValue("@WindowDays", result.TradingSessions);
            cmd.Parameters.AddWithValue("@Sessions", result.TradingSessions);
            cmd.Parameters.AddWithValue("@Returns", result.TotalReturns);
            cmd.Parameters.AddWithValue("@AvgBars", result.AvgBarsPerSession);
            cmd.Parameters.AddWithValue("@Vol", result.RealizedVol);
            cmd.Parameters.AddWithValue("@DailyVar", result.DailyVariance);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task SaveToSqlAsync(DailyVolSnapshot snap, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);

            var cmd = new SqlCommand(@"
            MERGE DailyVolSnapshots AS target
            USING (SELECT @Ticker AS Ticker, @Date AS [Date]) AS source
            ON target.Ticker = source.Ticker AND target.[Date] = source.[Date]
            WHEN MATCHED THEN UPDATE SET
                OpenPrice           = @Open,
                HighPrice           = @High,
                LowPrice            = @Low,
                ClosePrice          = @Close,
                PrevClose           = @PrevClose,
                IntradayRealizedVol = @IntradayVol,
                IntradayBarCount    = @BarCount,
                CloseToCloseVol     = @CCVol,
                ParkinsonVol        = @PKVol,
                GarmanKlassVol      = @GKVol,
                YangZhangVol        = @YZVol,
                RollingWindowDays   = @Window,
                DailyParkinsonVar   = @DPKVar,
                DailyGarmanKlassVar = @DGKVar,
                ComputedAt          = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (Ticker, [Date], OpenPrice, HighPrice, LowPrice, ClosePrice, PrevClose,
                 IntradayRealizedVol, IntradayBarCount, CloseToCloseVol, ParkinsonVol,
                 GarmanKlassVol, YangZhangVol, RollingWindowDays,
                 DailyParkinsonVar, DailyGarmanKlassVar)
            VALUES
                (@Ticker, @Date, @Open, @High, @Low, @Close, @PrevClose,
                 @IntradayVol, @BarCount, @CCVol, @PKVol,
                 @GKVol, @YZVol, @Window,
                 @DPKVar, @DGKVar);
        ", conn);

            cmd.Parameters.AddWithValue("@Ticker", snap.Ticker);
            cmd.Parameters.AddWithValue("@Date", snap.Date);
            cmd.Parameters.AddWithValue("@Open", snap.OpenPrice);
            cmd.Parameters.AddWithValue("@High", snap.HighPrice);
            cmd.Parameters.AddWithValue("@Low", snap.LowPrice);
            cmd.Parameters.AddWithValue("@Close", snap.ClosePrice);
            cmd.Parameters.AddWithValue("@PrevClose", snap.PrevClose);
            cmd.Parameters.AddWithValue("@IntradayVol", snap.IntradayRealizedVol);
            cmd.Parameters.AddWithValue("@BarCount", snap.IntradayBarCount);
            cmd.Parameters.AddWithValue("@CCVol", snap.CloseToCloseVol);
            cmd.Parameters.AddWithValue("@PKVol", snap.ParkinsonVol);
            cmd.Parameters.AddWithValue("@GKVol", snap.GarmanKlassVol);
            cmd.Parameters.AddWithValue("@YZVol", snap.YangZhangVol);
            cmd.Parameters.AddWithValue("@Window", snap.RollingWindowDays);
            cmd.Parameters.AddWithValue("@DPKVar", snap.DailyParkinsonVar);
            cmd.Parameters.AddWithValue("@DGKVar", snap.DailyGarmanKlassVar);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ── Massive API ──────────────────────────────────────────────────

        private async Task<List<AggBar>> GetAggsAsync(
            string ticker, int multiplier, string timespan,
            string from, string to, CancellationToken ct)
        {
            var all = new List<AggBar>();
            var url = $"{Base}/v2/aggs/ticker/{ticker}/range/{multiplier}/{timespan}/{from}/{to}" +
                      $"?adjusted=true&sort=asc&limit=50000&apiKey={_apiKey}";

            while (!string.IsNullOrEmpty(url))
            {
                var resp = await _http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<MassiveAggsResponse>(json, _json);

                if (result?.Results != null)
                    all.AddRange(result.Results);

                url = result?.NextUrl;
                if (!string.IsNullOrEmpty(url) && !url.Contains("apiKey"))
                    url += $"&apiKey={_apiKey}";
            }

            return all;
        }

        public void Dispose() => _http.Dispose();
    }
}


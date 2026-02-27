using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace YourNamespace.Services;

// ═══════════════════════════════════════════════════════════════════════
//  INTRADAY REALIZED VOL  —  15-min bars from Massive Stocks API
//  Computes daily realized vol, Yang-Zhang, Parkinson, Garman-Klass
//  Persists to SQL Server 2019
// ═══════════════════════════════════════════════════════════════════════

#region ── Vol Estimator Results ────────────────────────────────────────

public class DailyVolSnapshot
{
    public string Ticker { get; set; } = "";
    public DateTime Date { get; set; }

    // Spot reference
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal PrevClose { get; set; }

    // Intraday realized vol (from 15-min bars, annualized)
    public double IntradayRealizedVol { get; set; }
    public int IntradayBarCount { get; set; }

    // OHLC-based estimators (annualized, rolling N-day)
    public double CloseToCloseVol { get; set; }
    public double ParkinsonVol { get; set; }
    public double GarmanKlassVol { get; set; }
    public double YangZhangVol { get; set; }
    public int RollingWindowDays { get; set; }

    // Single-day OHLC contributions (not annualized, raw daily variance components)
    public double DailyParkinsonVar { get; set; }
    public double DailyGarmanKlassVar { get; set; }

    public override string ToString() =>
        $"{Date:yyyy-MM-dd}  {Ticker,-6}  " +
        $"O:{OpenPrice,8:F2} H:{HighPrice,8:F2} L:{LowPrice,8:F2} C:{ClosePrice,8:F2}  " +
        $"Intraday:{IntradayRealizedVol,7:P2}  CC:{CloseToCloseVol,7:P2}  " +
        $"PK:{ParkinsonVol,7:P2}  GK:{GarmanKlassVol,7:P2}  YZ:{YangZhangVol,7:P2}";
}

#endregion
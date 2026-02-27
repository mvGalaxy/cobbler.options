using System.Text.Json.Serialization;

namespace YourNamespace.Services
{
    public sealed record AggBar(
        [property: JsonPropertyName("o")] decimal Open,
        [property: JsonPropertyName("h")] decimal High,
        [property: JsonPropertyName("l")] decimal Low,
        [property: JsonPropertyName("c")] decimal Close,
        [property: JsonPropertyName("v")] double Volume,
        [property: JsonPropertyName("vw")] decimal VolumeWeighted,
        [property: JsonPropertyName("t")] long Timestamp,
        [property: JsonPropertyName("n")] int NumberOfTrades)
    {
        /// <summary>Convenience: converts Unix ms timestamp to DateTime (UTC).</summary>
        [JsonIgnore]
        public DateTime DateUtc =>
            DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).UtcDateTime;
    }
}

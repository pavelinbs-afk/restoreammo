using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace RestoreBullets;

public sealed class RestoreBulletsConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("CheckIntervalSeconds")]
    public float CheckIntervalSeconds { get; set; } = 0.25f;

    [JsonPropertyName("Debug")]
    public bool Debug { get; set; } = false;
}

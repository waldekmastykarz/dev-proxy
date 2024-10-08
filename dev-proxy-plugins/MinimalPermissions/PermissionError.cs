using System.Text.Json.Serialization;

namespace Microsoft.DevProxy.Plugins.MinimalPermissions;

internal class PermissionError
{
    [JsonPropertyName("requestUrl")]
    public string Url { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
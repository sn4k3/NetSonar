using System.Net;
using NetSonar.Avalonia.Converters;
using NetSonar.Avalonia.Settings;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace NetSonar.Avalonia;

public partial class App
{
    public static readonly RuntimeGlobals RuntimeGlobals = new();
    public static AppSettings AppSettings => AppSettings.Instance;

    // Follow redirects automatically (up to 20), and decompress content.
    private static readonly SocketsHttpHandler HttpHandler = new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
        AutomaticDecompression = DecompressionMethods.All,
    };

    public static readonly HttpClient HttpClient = new(HttpHandler);

    public static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        IgnoreReadOnlyFields = true,
        //IgnoreReadOnlyProperties = true,

        Converters =
        {
            new IPAddressJsonConverter(),
            new IPEndPointJsonConverter(),
            new JsonStringEnumConverter(),
        }
    };
}
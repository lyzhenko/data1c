using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Data1c.Cli;

/// <summary>Встроенная страница просмотрщика и общие настройки сериализации для его API.</summary>
internal static class ViewerAssets
{
    /// <summary>Место подставки данных графа в статичной странице.</summary>
    public const string DataPlaceholder = "__GRAPH_DATA__";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lazy<string> Html = new(LoadHtmlCore);

    /// <summary>Возвращает страницу просмотрщика с подставленными данными (null — режим сервера).</summary>
    public static string GetPage(string? graphData) =>
        Html.Value.Replace(DataPlaceholder, graphData ?? "null", StringComparison.Ordinal);

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static string LoadHtmlCore()
    {
        var assembly = typeof(ViewerAssets).Assembly;
        var name = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(static n => n.EndsWith("index.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Встроенная страница просмотрщика не найдена в сборке.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

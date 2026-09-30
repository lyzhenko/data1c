using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Data1c.Mcp;

/// <summary>Ошибка выполнения инструмента: её текст увидит агент как результат с признаком ошибки.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message)
        : base(message)
    {
    }

    public ToolException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Параметр инструмента для схемы входных данных.</summary>
/// <param name="Name">Имя аргумента.</param>
/// <param name="Type">Тип JSON Schema: <c>string</c>, <c>integer</c>, <c>boolean</c>, <c>array</c>.</param>
/// <param name="Description">Пояснение для модели.</param>
/// <param name="Required">Аргумент обязателен.</param>
/// <param name="Values">Допустимые значения (попадают в <c>enum</c>).</param>
public sealed record ToolParameter(
    string Name,
    string Type,
    string Description,
    bool Required = false,
    IReadOnlyList<string>? Values = null);

/// <summary>Описание инструмента MCP: имя, пояснение, схема аргументов и исполнитель.</summary>
public sealed record ToolSpec(
    string Name,
    string Description,
    IReadOnlyList<ToolParameter> Parameters,
    Func<ToolArguments, CancellationToken, Task<string>> Execute)
{
    /// <summary>Схема входных данных в формате JSON Schema.</summary>
    public JsonObject ToSchema()
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in Parameters)
        {
            var property = new JsonObject
            {
                ["type"] = parameter.Type,
                ["description"] = parameter.Description,
            };

            if (parameter.Values is { Count: > 0 } values)
            {
                var allowed = new JsonArray();
                foreach (var value in values)
                {
                    allowed.Add(value);
                }

                property["enum"] = allowed;
            }

            properties[parameter.Name] = property;
            if (parameter.Required)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }
}

/// <summary>Аргументы вызова инструмента с проверкой типов и понятными сообщениями об ошибках.</summary>
public sealed class ToolArguments
{
    private readonly JsonObject _arguments;

    private ToolArguments(JsonObject arguments) => _arguments = arguments;

    public static ToolArguments From(JsonNode? node) => new(node as JsonObject ?? []);

    /// <summary>Строковый аргумент или null.</summary>
    public string? GetString(string name)
    {
        var node = _arguments[name];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        throw new ToolException($"Аргумент «{name}» должен быть строкой.");
    }

    /// <summary>Обязательный строковый аргумент.</summary>
    public string RequireString(string name) =>
        GetString(name) ?? throw new ToolException($"Не указан обязательный аргумент «{name}».");

    /// <summary>Целочисленный аргумент, приведённый к диапазону.</summary>
    public int GetInt(string name, int fallback, int min, int max)
    {
        var node = _arguments[name];
        if (node is null)
        {
            return fallback;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var number))
        {
            return Math.Clamp(number, min, max);
        }

        throw new ToolException($"Аргумент «{name}» должен быть целым числом.");
    }

    /// <summary>Логический аргумент.</summary>
    public bool GetBool(string name, bool fallback)
    {
        var node = _arguments[name];
        if (node is null)
        {
            return fallback;
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        throw new ToolException($"Аргумент «{name}» должен быть логическим (true/false).");
    }

    /// <summary>Массив строк или null.</summary>
    public IReadOnlyList<string>? GetStringList(string name)
    {
        var node = _arguments[name];
        if (node is null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            var result = new List<string>(array.Count);
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
                else
                {
                    throw new ToolException($"Аргумент «{name}» должен быть массивом строк.");
                }
            }

            return result.Count == 0 ? null : result;
        }

        throw new ToolException($"Аргумент «{name}» должен быть массивом строк.");
    }
}

/// <summary>Единый способ готовить текстовый ответ инструмента: JSON без экранирования кириллицы.</summary>
public static class Render
{
    /// <summary>Предел длины одного ответа: дальше модель получает обрезанный хвост с пометкой.</summary>
    public const int MaxChars = 24_000;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string JsonOf(object value) => Truncate(JsonSerializer.Serialize(value, Json));

    public static string Truncate(string text, int maxChars = MaxChars) =>
        text.Length <= maxChars
            ? text
            : text[..maxChars] + $"\n… ответ обрезан до {maxChars} символов; сузьте запрос (limit, depth, диапазон строк).";
}

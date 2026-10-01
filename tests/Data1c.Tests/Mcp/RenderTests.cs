using System.Text.Json;
using Data1c.Mcp;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Обрезка ответа не должна ломать JSON: клиент обязан прочитать документ целиком,
/// иначе агент видит обрывок строки вместо данных.
/// </summary>
public sealed class RenderTests
{
    [Fact]
    public void Обрезанный_ответ_остаётся_разбираемым()
    {
        var longList = Enumerable.Range(0, 4000)
            .Select(static index => new { id = index, name = "ОченьДлинноеИмяРеквизита" + index })
            .ToList();

        var text = Render.JsonOf(new { items = longList });

        Assert.True(text.Length > Render.MaxChars);
        using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(Render.MaxChars, document.RootElement.GetProperty("limit").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("head").GetString()));
    }

    [Fact]
    public void Короткий_ответ_не_меняется()
    {
        var text = Render.JsonOf(new { id = "Catalog.Товары", name = "Товары" });

        using var document = JsonDocument.Parse(text);
        Assert.False(document.RootElement.TryGetProperty("truncated", out _));
        Assert.Equal("Catalog.Товары", document.RootElement.GetProperty("id").GetString());
    }
}

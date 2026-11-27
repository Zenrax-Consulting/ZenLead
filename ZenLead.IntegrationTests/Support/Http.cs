using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLead.IntegrationTests.Support;

/// <summary>JSON helpers matching the API's serializer settings (web defaults + enums as strings).</summary>
public static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) => client.PostAsJsonAsync(url, body, Json);
}

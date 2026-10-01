using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SupplyChain.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<JsonElement> Json(string path, HttpStatusCode expected)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Route_ReturnsRoutesWithAllTheMetrics()
    {
        var json = await Json("/api/route/CNSHA/NLRTM", HttpStatusCode.OK);
        Assert.Equal("CNSHA", json.GetProperty("origin").GetString());
        Assert.Equal("NLRTM", json.GetProperty("destination").GetString());

        var routes = json.GetProperty("routes").EnumerateArray().ToList();
        Assert.InRange(routes.Count, 2, 5);
        var recommended = json.GetProperty("recommended").GetString();
        Assert.Contains(routes, r => r.GetProperty("id").GetString() == recommended);

        var first = routes[0];
        foreach (var field in new[] { "id", "labels", "hubs", "modes", "chokepoints", "legs", "totals" })
            Assert.True(first.TryGetProperty(field, out _), field);
        foreach (var field in new[] { "days", "freightUsd", "pipelineUsd", "safetyStockUsd", "landedUsd", "kgCo2", "waitingShare", "sigmaDays" })
            Assert.True(first.GetProperty("totals").TryGetProperty(field, out _), field);
        Assert.True(first.GetProperty("legs")[0].TryGetProperty("mode", out _));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("baseline").ValueKind);
    }

    [Fact]
    public async Task Route_EchoesTheAssumptionsUsed()
    {
        var json = await Json("/api/route/cnsha/nlrtm?tonnes=10&value=5000&demand=2&carry=0.1&service=0.99&avoid=suez&modes=sea,rail", HttpStatusCode.OK);
        var cargo = json.GetProperty("cargo");
        Assert.Equal(10, cargo.GetProperty("tonnes").GetDouble());
        Assert.Equal(5000, cargo.GetProperty("valuePerTonne").GetDouble());
        Assert.Equal(0.1, cargo.GetProperty("carryRate").GetDouble());
        Assert.Equal(["SUEZ"], json.GetProperty("avoid").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["rail", "sea"], json.GetProperty("modes").EnumerateArray().Select(e => e.GetString()));
        Assert.NotEqual(JsonValueKind.Null, json.GetProperty("baseline").ValueKind);
    }

    [Fact]
    public async Task Closures_AreHonoured()
    {
        var json = await Json("/api/route/SGSIN/GBFXT?avoid=BAB,SUEZ", HttpStatusCode.OK);
        foreach (var route in json.GetProperty("routes").EnumerateArray())
            Assert.DoesNotContain("BAB", route.GetProperty("hubs").EnumerateArray().Select(h => h.GetString()));
    }

    [Theory]
    [InlineData("/api/route/CN/NLRTM")]                     // not a hub code
    [InlineData("/api/route/CNSHA/NLRTM%21")]
    [InlineData("/api/route/CNSHA/CNSHA")]                  // same hub
    [InlineData("/api/route/CNSHA/NLRTM?tonnes=abc")]
    [InlineData("/api/route/CNSHA/NLRTM?tonnes=0")]
    [InlineData("/api/route/CNSHA/NLRTM?carry=2")]
    [InlineData("/api/route/CNSHA/NLRTM?service=1")]
    [InlineData("/api/route/CNSHA/NLRTM?modes=boat")]
    [InlineData("/api/route/CNSHA/NLRTM?avoid=CNSHA")]      // closing the origin
    public async Task BadInput_Returns400WithJsonError(string path)
    {
        var json = await Json(path, HttpStatusCode.BadRequest);
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("/api/route/XXXXX/NLRTM")]
    [InlineData("/api/route/CNSHA/XXXXX")]
    [InlineData("/api/route/CNSHA/NLRTM?avoid=NOWHERE")]
    public async Task UnknownHub_Returns404WithJsonError(string path)
    {
        var json = await Json(path, HttpStatusCode.NotFound);
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task NoRoute_Returns404WithJsonError()
    {
        var json = await Json("/api/route/CNSHA/NLRTM?modes=road", HttpStatusCode.NotFound);
        Assert.Contains("No route", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Network_ListsHubsLanesModesAndPresets()
    {
        var response = await _client.GetAsync("/api/network");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age", response.Headers.CacheControl?.ToString());

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var hubs = json.GetProperty("hubs").EnumerateArray().ToList();
        Assert.True(hubs.Count > 50);
        Assert.Contains(hubs, h => h.GetProperty("kind").GetString() == "chokepoint" && h.GetProperty("id").GetString() == "SUEZ");
        Assert.True(json.GetProperty("lanes").GetArrayLength() > 100);
        Assert.Equal(4, json.GetProperty("modes").GetArrayLength());
        Assert.True(json.GetProperty("presets").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Api_DescribesItself()
    {
        var json = await Json("/api", HttpStatusCode.OK);
        Assert.True(json.GetProperty("usage").GetArrayLength() > 0);
        Assert.True(json.GetProperty("hubs").GetInt32() > 50);
    }
}

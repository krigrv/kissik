using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenKamera.Core.Tests;

public class ControlApiTests
{
    private static (ControlApi Api, List<string> Calls) Make()
    {
        var calls = new List<string>();
        var tools = new[]
        {
            new ControlTool("switch_scene", "Switch scene", ControlApi.Schema(("name", "string", "Scene name")),
                a => { calls.Add("scene:" + a["name"]); return Task.FromResult("Switched to " + a["name"]); }),
        };
        var api = new ControlApi(tools, Path.Combine(Path.GetTempPath(), "OpenKameraTests", $"tok-{Guid.NewGuid():N}.txt"));
        api.Start(0);
        return (api, calls);
    }

    [Fact]
    public async Task RejectsRequestsWithoutTheToken()
    {
        var (api, _) = Make(); await using var _a = api;
        using var http = new HttpClient();
        var r = await http.GetAsync(api.BaseUrl + "/tools");
        Assert.Equal(401, (int)r.StatusCode);
    }

    [Fact]
    public async Task RestCallRunsTheTool()
    {
        var (api, calls) = Make(); await using var _a = api;
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Token);
        var r = await http.PostAsync(api.BaseUrl + "/tools/switch_scene", new StringContent("{\"name\":\"Lower Third\"}", Encoding.UTF8, "application/json"));
        Assert.Contains("Switched to Lower Third", await r.Content.ReadAsStringAsync());
        Assert.Equal(["scene:Lower Third"], calls);
    }

    [Fact]
    public async Task McpListsAndCallsTools()
    {
        var (api, calls) = Make(); await using var _a = api;
        var init = JsonNode.Parse(await api.HandleMcpAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}"))!;
        Assert.Equal("kissik", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("", await api.HandleMcpAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        var list = JsonNode.Parse(await api.HandleMcpAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"))!;
        Assert.Equal("switch_scene", list["result"]!["tools"]![0]!["name"]!.GetValue<string>());
        var call = JsonNode.Parse(await api.HandleMcpAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"switch_scene\",\"arguments\":{\"name\":\"Blank\"}}}"))!;
        Assert.Contains("Switched to Blank", call["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Contains("scene:Blank", calls);
        var bad = JsonNode.Parse(await api.HandleMcpAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"nope\"}"))!;
        Assert.Equal(-32601, bad["error"]!["code"]!.GetValue<int>());
    }
}

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenKamera.Core;

/// <summary>One thing an AI agent, Stream Deck button or script can do.</summary>
public sealed record ControlTool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, Task<string>> Run);

/// <summary>
/// Local control for KISSIK: plain REST (POST /tools/{name}) and an MCP endpoint (POST /mcp, JSON-RPC 2.0,
/// "streamable HTTP") so AI agents can switch scenes, hide you, record and more. Listens on 127.0.0.1 only and
/// every request needs the secret token (Authorization: Bearer …), kept in the user's app data.
/// </summary>
public sealed class ControlApi : IAsyncDisposable
{
    public const int DefaultPort = 47830;
    private readonly Dictionary<string, ControlTool> _tools;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public string Token { get; }
    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public ControlApi(IEnumerable<ControlTool> tools, string tokenFile)
    {
        _tools = tools.ToDictionary(t => t.Name);
        string? saved = null;
        try { saved = File.ReadAllText(tokenFile).Trim(); } catch { }
        Token = saved is { Length: >= 32 } ? saved : Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        if (saved != Token) { Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!); File.WriteAllText(tokenFile, Token); }
    }

    public void Start(int port = DefaultPort)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        try { _listener.Start(); } catch (SocketException) when (port != 0) { _listener = new TcpListener(IPAddress.Loopback, 0); _listener.Start(); }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener!.AcceptTcpClientAsync(ct); } catch { return; }
            _ = Task.Run(() => Serve(c, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var s = client.GetStream();
        try
        {
            var (method, path, headers, body) = await ReadAsync(s, ct);
            // Browsers can't reach this from web pages: no CORS headers, and the token is required.
            bool ok = headers.TryGetValue("authorization", out var auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                      && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(auth[7..].Trim()), Encoding.ASCII.GetBytes(Token));
            if (!ok) { await WriteAsync(s, 401, Json(new { error = "Missing or wrong token." }), ct); return; }
            if (method == "GET" && path == "/tools")
                await WriteAsync(s, 200, JsonSerializer.Serialize(_tools.Values.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema })), ct);
            else if (method == "POST" && path.StartsWith("/tools/") && _tools.TryGetValue(path[7..], out var tool))
                await WriteAsync(s, 200, Json(new { result = await RunTool(tool, ParseObject(body)) }), ct);
            else if (method == "POST" && path == "/mcp")
                await WriteAsync(s, 200, await HandleMcpAsync(body), ct);
            else await WriteAsync(s, 404, Json(new { error = "Not found. Try GET /tools or POST /mcp." }), ct);
        }
        catch (Exception ex) { try { await WriteAsync(s, 500, Json(new { error = ex.Message }), ct); } catch { } }
    }

    private static async Task<string> RunTool(ControlTool tool, JsonObject args)
    {
        try { return await tool.Run(args); }
        catch (Exception ex) { return "Failed: " + ex.Message; }
    }

    /// <summary>MCP (JSON-RPC 2.0): initialize, tools/list, tools/call, ping. Notifications get no body.</summary>
    public async Task<string> HandleMcpAsync(string body)
    {
        var req = ParseObject(body);
        var id = req["id"]?.DeepClone();
        string method = req["method"]?.GetValue<string>() ?? "";
        object? result = method switch
        {
            "initialize" => new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "kissik", version = "1.0" },
                instructions = "Controls the KISSIK camera studio on this PC: scenes, privacy shutter, recording, captions, status.",
            },
            "tools/list" => new { tools = _tools.Values.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }) },
            "tools/call" => await CallAsync(req["params"] as JsonObject),
            "ping" => new { },
            _ => null,
        };
        if (id is null) return ""; // notification (e.g. notifications/initialized)
        var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (result is null) resp["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + method };
        else resp["result"] = JsonSerializer.SerializeToNode(result);
        return resp.ToJsonString();
    }

    private async Task<object> CallAsync(JsonObject? p)
    {
        string name = p?["name"]?.GetValue<string>() ?? "";
        if (!_tools.TryGetValue(name, out var tool))
            return new { content = new[] { new { type = "text", text = "Unknown tool: " + name } }, isError = true };
        string text = await RunTool(tool, p?["arguments"] as JsonObject ?? []);
        return new { content = new[] { new { type = "text", text } }, isError = text.StartsWith("Failed:") };
    }

    private static JsonObject ParseObject(string body)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) as JsonObject ?? []; } catch { return []; }
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new StringBuilder(); var one = new byte[1];
        while (head.Length < 16384 && await s.ReadAsync(one, ct) == 1)
        {
            head.Append((char)one[0]);
            if (head.Length >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r') break;
        }
        var lines = head.ToString().Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>();
        foreach (var l in lines.Skip(1)) { int c = l.IndexOf(':'); if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim(); }
        int len = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? Math.Clamp(n, 0, 1_000_000) : 0;
        var buf = new byte[len]; int got = 0;
        while (got < len) { int r = await s.ReadAsync(buf.AsMemory(got), ct); if (r == 0) break; got += r; }
        string path = first.ElementAtOrDefault(1) ?? "/"; int q = path.IndexOf('?'); if (q >= 0) path = path[..q];
        return (first.ElementAtOrDefault(0) ?? "", path, headers, Encoding.UTF8.GetString(buf, 0, got));
    }

    private static async Task WriteAsync(Stream s, int code, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        string reason = code switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await s.WriteAsync(body, ct);
    }

    public async ValueTask DisposeAsync() { _cts?.Cancel(); try { _listener?.Stop(); } catch { } await Task.CompletedTask; }

    public static JsonObject Schema(params (string Name, string Type, string Description)[] props)
    {
        var p = new JsonObject();
        foreach (var (n, t, d) in props) p[n] = new JsonObject { ["type"] = t, ["description"] = d };
        return new JsonObject { ["type"] = "object", ["properties"] = p };
    }
}

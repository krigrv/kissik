# Control KISSIK from AI assistants, Stream Deck and scripts

Turn it on in **⚙ View options → Let AI assistants and Stream Deck control KISSIK**.
It is off by default, listens on this PC only (`127.0.0.1:47830`), and every request needs the
secret key shown when you turn it on (kept in `%APPDATA%\OpenKamera\control-token.txt`).

## AI assistants (MCP)

KISSIK is an MCP server over streamable HTTP. Paste the settings it shows into your assistant:

```json
{ "mcpServers": { "kissik": { "type": "http", "url": "http://127.0.0.1:47830/mcp",
  "headers": { "Authorization": "Bearer <your key>" } } } }
```

Then ask things like "switch KISSIK to my Lower Third scene", "blur me", "start recording".

## Scripts and Stream Deck

```
GET  /tools                      list tools
POST /tools/switch_scene         {"name":"Lower Third"}
POST /tools/set_privacy          {"mode":"visible|blur|away"}
POST /tools/set_background       {"mode":"off|blur|replace|remove"}
POST /tools/start_recording      {}
POST /tools/stop_recording       {}
POST /tools/set_captions         {"on":true}
POST /tools/take_snapshot        {}
POST /tools/get_status           {}
POST /tools/list_scenes          {}
```

Every call needs `Authorization: Bearer <your key>`.

"""Call one MCP tool over Streamable HTTP and print the result.

There is no test suite in this project; phases are verified against the running
stack. This is the harness for exercising mcp-worker tools by hand.

    python scripts/probe_mcp.py list                      # list tools
    python scripts/probe_mcp.py ping
    python scripts/probe_mcp.py search_supplier '{"supplier_name":"ACME"}'
"""

from __future__ import annotations

import json
import sys
import urllib.request

URL = "http://localhost:8000/mcp"
HEADERS = {
    "Content-Type": "application/json",
    "Accept": "application/json, text/event-stream",
}


def rpc(method: str, params: dict) -> dict:
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    req = urllib.request.Request(URL, data=body, headers=HEADERS, method="POST")
    with urllib.request.urlopen(req, timeout=180) as res:
        return json.loads(res.read().decode())


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    name = sys.argv[1]
    if name == "list":
        tools = rpc("tools/list", {})["result"]["tools"]
        for t in tools:
            print(f"{t['name']}\n    {t.get('description','').splitlines()[0]}")
        return 0

    args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    res = rpc("tools/call", {"name": name, "arguments": args})

    if "error" in res:
        print("RPC ERROR:", json.dumps(res["error"], ensure_ascii=False, indent=2))
        return 1

    result = res["result"]
    payload = result.get("structuredContent")
    if payload is None:
        text = "".join(c.get("text", "") for c in result.get("content", []))
        try:
            payload = json.loads(text)
        except json.JSONDecodeError:
            payload = text

    print(json.dumps(payload, ensure_ascii=False, indent=2, default=str))
    return 1 if result.get("isError") else 0


if __name__ == "__main__":
    sys.exit(main())

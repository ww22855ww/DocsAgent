"""Compare how each mode routes a task, across a matrix of phrasings.

A rehearsal tool, not part of the demo. Use it to pick which sentences to show
and to have a real number ready if someone asks how reliable the routing is.

    python scripts/probe_routing.py                 # the built-in matrix, 3 runs each
    python scripts/probe_routing.py -n 5            # 5 runs each
    python scripts/probe_routing.py "任務句" esg     # one sentence, expected screen

Both sides are measured through the running services, so the keyword rule and
the system prompt are whatever the code actually uses today.
"""

from __future__ import annotations

import json
import sys
import urllib.request

URL = "http://localhost:5000/api/routing"

# Graded on purpose. A test that only shows the keyword rule failing invites
# "then add the keyword", so it has to also show what adding one costs.
MATRIX: list[tuple[str, str, str]] = [
    # group, prompt, expected screen
    ("直接點名", "處理今天 SCM 文件", "scm"),
    ("直接點名", "處理今年的 ESG 供應商問卷", "esg"),
    ("同義詞", "把供應商永續調查表建檔", "esg"),
    ("領域用語", "整理各家廠商回覆的碳排與勞權自評表", "esg"),
    ("領域用語", "盤點供應商的溫室氣體盤查與衝突礦產申報", "esg"),
    ("領域用語", "把廠商交回來的社會責任自評結果整理一下", "esg"),
    ("關鍵字誤觸", "處理今天 SCM 文件，包含永續發展部轉來的那幾張發票", "scm"),
    ("關鍵字誤觸", "把今天的發票和品檢報告歸檔，問卷先不用管", "scm"),
]


def post(payload: dict) -> dict:
    req = urllib.request.Request(
        URL, data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=900) as res:
        return json.load(res)


def agent_verdict(agent: dict, expected: str) -> tuple[str, str]:
    """Reduce the repeat runs to one verdict plus a readable tally."""
    if agent.get("error"):
        return "錯誤", agent["error"][:40]

    picks: dict[str, int] = agent["picks"]
    runs = agent["runs"] or 1
    right = picks.get(expected, 0)

    tally = "  ".join(f"{k}×{v}" for k, v in sorted(picks.items(), key=lambda kv: -kv[1]))
    if right == runs:
        return "正確", tally
    if right == 0:
        return "錯誤", tally
    return f"不穩 {right}/{runs}", tally


def main() -> int:
    args = [a for a in sys.argv[1:]]
    repeat = 3
    if "-n" in args:
        i = args.index("-n")
        repeat = int(args[i + 1])
        del args[i:i + 2]

    cases = (
        [("自訂", args[0], args[1] if len(args) > 1 else "esg")]
        if args else MATRIX
    )

    print(f"routing probe · {repeat} runs per sentence · this takes a moment\n")
    data = post({"prompts": [c[1] for c in cases], "repeat": repeat})

    print(f"model      {data['model']}")
    print(f"keywords   {' '.join(data['keywords'])}\n")

    header = f"{'組別':<12}{'任務句':<46}{'正解':<6}{'固定關鍵字':<14}{'Agent':<12}明細"
    print(header)
    print("-" * 118)

    script_wrong = agent_wrong = unstable = 0

    for (group, _prompt, expected), r in zip(cases, data["results"]):
        s_route = r["scripted"]["route"]
        s_ok = s_route == expected
        if not s_ok:
            script_wrong += 1
        matched = r["scripted"]["matchedKeywords"]
        s_cell = f"{s_route} {'✓' if s_ok else '✗'}"
        if matched:
            s_cell += f" ({'/'.join(matched)})"

        verdict, tally = agent_verdict(r["agent"], expected)
        if verdict == "錯誤":
            agent_wrong += 1
        elif verdict.startswith("不穩"):
            unstable += 1

        prompt = r["prompt"]
        pad = 46 - sum(2 if ord(c) > 0x2E80 else 1 for c in prompt)
        print(f"{group:<12}{prompt}{' ' * max(1, pad)}{expected:<6}{s_cell:<14}{verdict:<12}{tally}")

    total = len(cases)
    print("-" * 118)
    print(f"固定關鍵字   {total - script_wrong}/{total} 正確")
    print(f"Agent        {total - agent_wrong - unstable}/{total} 正確"
          + (f"，{unstable} 句結果不穩定" if unstable else ""))
    print(f"平均延遲     {data['results'][0]['agent']['avgMs']} ms 左右 / 次決策")
    return 0


if __name__ == "__main__":
    sys.exit(main())

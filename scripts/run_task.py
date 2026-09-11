"""Start an agent task, wait for it to finish, and print its execution trace.

    python scripts/run_task.py scripted
    python scripts/run_task.py llm "處理今天 SCM 文件"
"""

from __future__ import annotations

import json
import sys
import time
import urllib.error
import urllib.request

BASE = "http://localhost:5000/api/tasks"
TERMINAL = {"Completed", "ManualReview", "Failed"}


def post(url: str, payload: dict) -> dict:
    req = urllib.request.Request(
        url, data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=60) as res:
        return json.load(res)


def get(url: str) -> dict:
    with urllib.request.urlopen(url, timeout=60) as res:
        return json.load(res)


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else "scripted"
    prompt = sys.argv[2] if len(sys.argv) > 2 else "處理今天 SCM 文件"

    started = post(BASE, {"prompt": prompt, "mode": mode})
    task_id = started["id"]
    print(f"task {task_id}  mode={started['mode']}  prompt={started['prompt']}")
    print("-" * 78)

    seen = 0
    deadline = time.time() + 900
    task: dict = {}

    while time.time() < deadline:
        task = get(f"{BASE}/{task_id}")

        for step in task["steps"][seen:]:
            mark = " " if step["success"] else "!"
            print(f"{mark}{step['index']:>3} [{step['kind']:<8}] {step['title']}"
                  f"{'  (' + str(step['durationMs']) + 'ms)' if step['durationMs'] else ''}")
            if step.get("thought"):
                print(f"       thought: {step['thought'][:150]}")
            if step.get("detail") and step["kind"] in ("summary", "error", "agent"):
                print(f"       {step['detail'][:200]}")
        seen = len(task["steps"])

        if task["state"] in TERMINAL:
            break
        time.sleep(2)

    print("-" * 78)
    print(f"state={task.get('state')}  duration={task.get('durationMs')}ms  steps={seen}")
    if task.get("error"):
        print(f"error: {task['error']}")
    print(f"summary: {task.get('summary')}")
    print(f"counts: {json.dumps(task.get('counts'), ensure_ascii=False)}")
    print("documents:")
    for d in task.get("documents", []):
        print(f"  {d['filename']:<20} {d['status']:<14} {d.get('category') or '-':<16}"
              f" supplier={d.get('supplierCode') or d.get('supplierName') or '-'}"
              f" notified={d.get('notified')}")
        if d.get("reviewReason"):
            print(f"      reason: {d['reviewReason']}")

    return 0 if task.get("state") in ("Completed", "ManualReview") else 1


if __name__ == "__main__":
    sys.exit(main())

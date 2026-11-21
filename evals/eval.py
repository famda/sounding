"""Diagnosis scenarios for Sounding's MCP server, in two steps so that the slow part is done once.

    eval.py record <case>                 live: wait for the environment, break something in it, wait for the fault
                                          to show in the data, and save the database as a snapshot (with its manifest)
    eval.py plan <run> <repeat> <case>...
                                          write the run's manifest: every planned attempt and what it runs on
    eval.py run <case> [--prompt]         replay: load the snapshot, ask a local model (the plain question, or the
                                          server prompt), grade the answer, and record the attempt whatever happens
    eval.py clock <case> [seconds]        replay twice, the given time apart, and check that every answer is the same
    eval.py grade                         check the grader against grading/answers.json
    eval.py report [--run R] [--baseline B]
                                          the newest run's results, compared only with an explicit baseline

A replay holds the database's clock at the moment the snapshot was taken (see restore), so ages, windows and
"online" read the same however long an investigation takes.
"""
import gzip
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

import grading

MCP = os.environ.get("MCP_URL", "http://dashboard:8080/mcp")
OLLAMA = os.environ.get("OLLAMA_URL", "http://model:11434")
MODEL = os.environ.get("MODEL", "qwen3.5:9b")
RUN_ID = os.environ.get("RUN_ID", "")
LABEL = os.environ.get("RUN_LABEL", "")
IMPLEMENTATION = os.environ.get("IMPLEMENTATION", "unknown")
ATTEMPT = int(os.environ.get("ATTEMPT", "1"))
MAX_TURNS = 20
NUM_CTX = 65536
OUT = Path("/out")
RUNS = OUT / "runs"
SNAPSHOTS = Path("/snapshots")
MANIFEST = SNAPSHOTS / "manifest.json"
# How a snapshot is replayed. 2: the database clock is held at the snapshot's observation time.
SNAPSHOT_FORMAT = 2

# The environment's containers that report to Sounding, and how long they run undisturbed before a fault: tools that
# compare with "before" need a before.
EXPECTED_CONTAINERS = 6
BASELINE_SECONDS = 75
ENV = "sounding-evals-env-"

# What each fault does. Docker commands name a container of the environment; "http" calls a URL inside it.
FAULTS = {
    "slow-db": ["docker", "update", "--cpus", "0.01", ENV + "shop-db-1"],
    "stopped-catalog": ["docker", "stop", ENV + "catalog-api-1"],
    "stopped-cache": ["docker", "stop", ENV + "cache-1"],
    "frozen-catalog": ["docker", "pause", ENV + "catalog-api-1"],
    "leak": ["docker", "start", ENV + "worker-1"],
    "hog": ["docker", "start", ENV + "reports-batch-1"],
    "disk": ["docker", "start", ENV + "exporter-1"],
    "near-limit": ["docker", "start", ENV + "indexer-1"],
    "orders-leak": ["http", "http://proxy/api/orders/history"],
}

# Colours make the turns and results easy to tell apart; NO_COLOR turns them off (for a log file).
COLOR = not os.environ.get("NO_COLOR")
BOLD, DIM, RED, GREEN, YELLOW, CYAN = ("\033[1m", "\033[2m", "\033[31m", "\033[32m", "\033[33m", "\033[36m")


def paint(text, *styles):
    return "".join(styles) + str(text) + "\033[0m" if COLOR else str(text)


def preview(text, length=180):
    flat = " ".join(text.split())
    return flat[:length] + ("…" if len(flat) > length else "")


def sha256(data):
    return hashlib.sha256(data if isinstance(data, bytes) else data.encode()).hexdigest()


def post(url, body, headers=None, timeout=3600):
    request = urllib.request.Request(url, json.dumps(body).encode(), {"Content-Type": "application/json", **(headers or {})})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read().decode()


def inject(fault):
    for part in fault.split("+"):
        if part == "none":
            continue
        kind, *arguments = FAULTS[part]
        if kind == "http":
            post(arguments[0], {}, timeout=30)
            continue
        result = subprocess.run([kind, *arguments], capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"{kind} {' '.join(arguments)} failed: {result.stderr.strip()}")


def psql(sql=None, stdin=None, query=False):
    """Runs SQL in the environment's database, from text or from a stream; with query, returns the output."""
    command = ["docker", "exec", "-i", "-e", "PGAPPNAME=eval", ENV + "db-1", "psql", "-U", "sounding", "-d", "sounding", "-q", "-v", "ON_ERROR_STOP=1"]
    if query:
        command += ["-At"]
    if sql is not None:
        command += ["-c", sql]
    process = subprocess.Popen(command, stdin=subprocess.PIPE if stdin else subprocess.DEVNULL,
                               stdout=subprocess.PIPE if query else subprocess.DEVNULL, stderr=subprocess.PIPE)
    if stdin:
        shutil.copyfileobj(stdin, process.stdin)
        process.stdin.close()
    output = process.stdout.read().decode().strip() if query else None
    error = process.stderr.read().decode()
    if process.wait() != 0:
        raise RuntimeError(f"psql failed: {error.strip()[-500:]}")
    return output


# Which schema the database is at, from the migrations the dashboard applied: a snapshot replays only into the schema
# it was recorded with.
SCHEMA = "select md5(string_agg(version || ':' || checksum, ',' order by version)) from schema_migrations"


def snapshot_name(case):
    return case.get("snapshot", case["id"])


def snapshot_path(case):
    return SNAPSHOTS / f"{snapshot_name(case)}.sql.gz"


def read_manifest():
    return json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.exists() else {}


def write_manifest_entry(case, observed, schema):
    manifest = read_manifest()
    manifest[snapshot_name(case)] = {
        "format": SNAPSHOT_FORMAT, "sha256": sha256(snapshot_path(case).read_bytes()), "observedAt": observed,
        "schema": schema, "fault": case["fault"], "settle": case["settle"],
        "recorded": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    MANIFEST.write_text(json.dumps(dict(sorted(manifest.items())), indent=2) + "\n", encoding="utf-8")


def snapshot_entry(case):
    """The manifest entry of a case's snapshot, refused when the file or the replay method no longer match."""
    path = snapshot_path(case)
    if not path.exists():
        raise RuntimeError(f"No snapshot {path.name}. Record it first: .\\platform.ps1 evals record {case['id']}")
    entry = read_manifest().get(snapshot_name(case))
    if entry is None:
        raise RuntimeError(f"{path.name} has no manifest entry. Record it: .\\platform.ps1 evals record {case['id']}")
    if entry.get("format") != SNAPSHOT_FORMAT:
        raise RuntimeError(f"{path.name} is snapshot format {entry.get('format')}, this runner replays format {SNAPSHOT_FORMAT}: record it again.")
    if entry["sha256"] != sha256(path.read_bytes()):
        raise RuntimeError(f"{path.name} changed since its manifest entry was written: record it again.")
    return entry


def save(case):
    SNAPSHOTS.mkdir(exist_ok=True)
    dump = subprocess.Popen(["docker", "exec", ENV + "db-1", "pg_dump", "-U", "sounding", "-d", "sounding", "--clean", "--if-exists", "--no-owner"],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    with gzip.open(snapshot_path(case), "wb", compresslevel=6) as file:
        shutil.copyfileobj(dump.stdout, file)
    if dump.wait() != 0:
        raise RuntimeError(f"pg_dump failed: {dump.stderr.read().decode().strip()}")
    write_manifest_entry(case, psql("select max(last_seen) from hosts", query=True), psql(SCHEMA, query=True))
    print(paint(f"Snapshot: evals/snapshots/{snapshot_path(case).name} ({snapshot_path(case).stat().st_size / 1048576:.1f} MB)", DIM))


# The replay clock: in this database only, now() is the snapshot's observation time. Unqualified now() resolves
# through the search path, which lists the replay schema before pg_catalog. Nothing else moves: rows, embedded times
# and the dashboard's fleet clock (read from now()) all agree, however long an investigation takes.
CLOCK = """
create schema if not exists replay;
create table if not exists replay.clock (at timestamptz not null);
truncate replay.clock;
insert into replay.clock select max(last_seen) from public.hosts;
create or replace function replay.now() returns timestamptz language sql stable as 'select at from replay.clock';
alter database sounding set search_path = public, replay, pg_catalog;
"""


def restore(case):
    """Replaces the whole database with the snapshot, holds its clock, and restarts the dashboard so that its
    connections take the clock and nothing it kept from the case before remains. The dashboard is stopped meanwhile:
    its maintenance would otherwise create partitions while the snapshot's are restored."""
    def dashboard(action):
        result = subprocess.run(["docker", action, ENV + "dashboard-1"], capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"docker {action} dashboard failed: {result.stderr.strip()}")

    dashboard("stop")
    # Into an empty schema: a dump's own drops can't remove an attached partition's constraints.
    psql("drop schema public cascade; create schema public;")
    with gzip.open(snapshot_path(case), "rb") as file:
        psql(stdin=file)
    psql(CLOCK)
    dashboard("start")
    deadline = time.time() + 120
    while True:  # until the dashboard has read the restored fleet
        try:
            hosts = tool("list_hosts").get("items", [])
            if hosts and any(h.get("online") for h in hosts):
                break
        except Exception:
            pass
        if time.time() > deadline:
            raise RuntimeError("The dashboard did not read the snapshot within two minutes.")
        time.sleep(1)
    return psql("select now()", query=True), psql(SCHEMA, query=True)


def mcp(method, params=None, timeout=3600):
    raw = post(MCP, {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}, {"Accept": "application/json, text/event-stream"}, timeout)
    reply = json.loads(raw.split("data: ", 1)[1] if "data: " in raw else raw)
    if "error" in reply:
        raise RuntimeError(reply["error"].get("message", "protocol error"))
    return reply["result"]


def tool_text(name, arguments=None):
    return mcp("tools/call", {"name": name, "arguments": arguments or {}}, timeout=60)["content"][0]["text"]


def tool(name, arguments=None):
    return json.loads(tool_text(name, arguments))


def wait_for_environment():
    """Every container reports and requests flow between them; then a quiet baseline."""
    print(paint("Waiting for the environment to report...", DIM), flush=True)
    deadline = time.time() + 300
    while True:
        try:
            online = sum(1 for c in tool("list_containers").get("items", []) if c.get("status") == "online")
            flowing = any(e.get("requestsPerSecond") for e in tool("get_dependencies").get("edges", []))
            if online >= EXPECTED_CONTAINERS and flowing:
                break
        except Exception:  # the dashboard is still starting, or has not read the fleet yet
            pass
        if time.time() > deadline:
            raise RuntimeError("The environment did not come up within 5 minutes.")
        time.sleep(3)
    print(paint(f"Environment up; letting it run {BASELINE_SECONDS} s as a baseline...", DIM), flush=True)
    time.sleep(BASELINE_SECONDS)


def ensure_model():
    with urllib.request.urlopen(f"{OLLAMA}/api/tags", timeout=60) as response:
        present = any(m["name"] == MODEL for m in json.load(response)["models"])
    if not present:
        print(f"Downloading {MODEL} (once; several GB)...", flush=True)
        post(f"{OLLAMA}/api/pull", {"model": MODEL, "stream": False})


def ask(case, mode, progress):
    """The chat loop. Progress holds the messages and figures so far, so a failure part-way keeps them."""
    init = mcp("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "sounding-evals", "version": "2"}})
    tools = [{"type": "function", "function": {"name": t["name"], "description": t["description"], "parameters": t["inputSchema"]}} for t in mcp("tools/list")["tools"]]
    prompt = case["prompt"] if mode == "prompt" else None
    user = mcp("prompts/get", prompt)["messages"][0]["content"]["text"] if prompt else case["question"]
    messages = progress["messages"] = [{"role": "system", "content": init.get("instructions", "")}, {"role": "user", "content": user}]
    print(paint(f"{MODEL} | {len(tools)} tools | {'prompt ' + prompt['name'] if prompt else 'plain question'}", DIM))
    print(f"{paint('Question', BOLD, YELLOW)}  {case['question']}", flush=True)

    started = time.time()
    stats = progress["stats"] = {"calls": [], "errors": 0, "resultChars": 0, "promptTokens": 0, "outputTokens": 0}
    for turn in range(1, MAX_TURNS + 1):
        reply = json.loads(post(f"{OLLAMA}/api/chat", {"model": MODEL, "messages": messages, "tools": tools, "stream": False, "think": True,
                                                       "options": {"num_ctx": NUM_CTX, "temperature": 0, "seed": ATTEMPT}}))
        if "message" not in reply:
            raise RuntimeError(f"malformed model reply: {preview(json.dumps(reply), 300)}")
        message = reply["message"]
        stats["promptTokens"] += reply.get("prompt_eval_count", 0)
        stats["outputTokens"] += reply.get("eval_count", 0)
        messages.append(message)
        if not message.get("tool_calls"):
            stats["seconds"] = round(time.time() - started)
            return message.get("content", "")
        print(f"\n{paint(f'Turn {turn}', BOLD, CYAN)}", flush=True)
        for call in message["tool_calls"]:
            name, arguments = call["function"]["name"], call["function"]["arguments"]
            try:
                result = mcp("tools/call", {"name": name, "arguments": arguments})
                text, failed = result["content"][0]["text"], result.get("isError", False)
            except Exception as error:  # a call the server rejected outright (unknown tool, malformed arguments)
                text, failed = f"Error: {error}", True
            stats["errors"] += failed
            stats["resultChars"] += len(text)
            stats["calls"].append(name)
            status = paint("ERR", BOLD, RED) if failed else paint("ok ", GREEN)
            print(f"  {status} {paint(name, BOLD)} {paint(json.dumps(arguments), DIM)}")
            print(f"      {paint(f'{len(text)} chars', DIM)}  {paint(preview(text), RED if failed else DIM)}", flush=True)
            messages.append({"role": "tool", "tool_name": name, "content": text})
    stats["seconds"] = round(time.time() - started)
    return None


def load_cases():
    return {c["id"]: c for c in json.loads(Path("scenarios.json").read_text(encoding="utf-8"))}


def supports(case, mode):
    return bool(case.get("prompt")) if mode == "prompt" else case.get("plain", True)


def record(case):
    if "snapshot" in case:
        raise RuntimeError(f"{case['id']} replays the snapshot of {case['snapshot']}: record that one.")
    wait_for_environment()
    if case["fault"] != "none":
        print(paint(f"Injecting {case['fault']}; waiting {case['settle']} s for it to show in the data...", DIM), flush=True)
        inject(case["fault"])
        time.sleep(case["settle"])
    save(case)
    return 0


def forms(case):
    """The forms a case is asked in: its plain question, its server prompt, or both."""
    return [mode for mode in ("question", "prompt") if supports(case, mode)]


def form_key(case_id, mode):
    return case_id if mode == "question" else f"{case_id}:prompt"


def plan(run_id, repeat, ids):
    """The run's manifest, written before anything runs: a crash part-way still leaves every planned attempt."""
    cases = load_cases()
    manifest = read_manifest()
    entries = {}
    for case_id in ids:
        case = cases[case_id]
        snapshot = manifest.get(snapshot_name(case), {})
        for mode in forms(case):
            entries[form_key(case_id, mode)] = {
                "set": case["set"], "definition": sha256(json.dumps(case, sort_keys=True))[:16], "snapshot": snapshot_name(case),
                "snapshotSha": snapshot.get("sha256", "")[:16], "snapshotFormat": snapshot.get("format"),
                "mode": mode, "prompt": case["prompt"]["name"] if mode == "prompt" else None,
            }
    RUNS.mkdir(parents=True, exist_ok=True)
    (RUNS / f"{run_id}.json").write_text(json.dumps({
        "run": run_id, "created": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "model": MODEL,
        "repeat": repeat, "seeds": list(range(1, repeat + 1)), "grader": grading.VERSION, "implementation": IMPLEMENTATION,
        "cases": entries,
        "attempts": [{"case": key, "attempt": a} for key in entries for a in range(1, repeat + 1)],
    }, indent=1), encoding="utf-8")
    return 0


def run(case):
    mode = "prompt" if "--prompt" in sys.argv else "question"
    print(f"{paint('Expected', BOLD, YELLOW)}  {case['truth']}", flush=True)
    progress = {"messages": [], "stats": {}}
    row = {"run": RUN_ID, "label": LABEL, "scenario": form_key(case["id"], mode), "set": case["set"], "model": MODEL, "mode": mode,
           "prompt": case["prompt"]["name"] if mode == "prompt" and case.get("prompt") else None,
           "attempt": ATTEMPT, "seed": ATTEMPT, "grader": grading.VERSION}
    answer, checks, stage = None, [], "setup"
    try:
        if not supports(case, mode):
            row["status"] = "skipped"
            print(paint(f"SKIPPED: {case['id']} has no {mode} form", YELLOW))
        else:
            stage = "snapshot"
            entry = snapshot_entry(case)
            row["snapshotSha"] = entry["sha256"][:16]
            stage = "model"
            ensure_model()
            stage = "restore"
            _, schema = restore(case)
            if schema != entry["schema"]:
                raise RuntimeError(f"The dashboard's schema ({schema[:8]}) differs from the one {snapshot_path(case).name} was recorded with ({entry['schema'][:8]}): record it again.")
            stage = "ask"
            answer = ask(case, mode, progress)
            stage = "grade"
            if answer is None:
                row["status"] = "fail"
                print(paint(f"RESULT: FAIL - no answer after {MAX_TURNS} turns", BOLD, RED))
            else:
                print(paint("Answer", BOLD, YELLOW) + "\n" + answer.strip() + "\n")
                checks = grading.grade(answer, case["rubric"])
                for label, ok, clause in checks:
                    print(f"  {paint('✓', GREEN) if ok else paint('✗', BOLD, RED)} {label}" + (paint(f'  «{preview(clause, 120)}»', DIM) if clause else ""))
                row["status"] = "pass" if all(ok for _, ok, _ in checks) else "fail"
                print(f"\n{paint('RESULT: PASS', BOLD, GREEN) if row['status'] == 'pass' else paint('RESULT: FAIL', BOLD, RED)} "
                      + paint("(rubric checks: read the answer against the expected conclusion)", DIM))
    except Exception as error:  # an infrastructure failure is recorded as one, never as a wrong answer
        row.update(status="error", stage=stage, error=preview(f"{type(error).__name__}: {error}", 500))
        print(paint(f"RESULT: ERROR at {stage}: {row['error']}", BOLD, RED))

    stats = progress["stats"]
    row.update(passed=row["status"] == "pass", answered=answer is not None, calls=len(stats.get("calls", [])),
               **{k: stats[k] for k in ("errors", "resultChars", "promptTokens", "outputTokens", "seconds") if k in stats})
    OUT.mkdir(exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    row["time"] = stamp
    (OUT / f"{stamp}-{case['id']}.json").write_text(json.dumps({**row, "tools": stats.get("calls", []), "checks": checks, "answer": answer,
                                                                "messages": progress["messages"]}, indent=1), encoding="utf-8")
    with (OUT / "results.jsonl").open("a", encoding="utf-8") as results:
        results.write(json.dumps(row) + "\n")
    print(paint(f"Transcript: evals/out/{stamp}-{case['id']}.json", DIM), flush=True)
    return {"pass": 0, "skipped": 0, "fail": 1}.get(row["status"], 2)


# Read-only calls whose answers must not depend on when they are asked during a replay.
CLOCK_CALLS = [("diagnose", {}), ("list_containers", {}), ("list_hosts", {}), ("what_changed", {}), ("get_events", {}),
               ("check", {"target": "orders-api", "check": "running"}), ("get_timeline", {"target": "orders-api"})]


def clock(case, seconds):
    """Replays the snapshot, asks the same questions twice the given time apart, and lists what differs."""
    snapshot_entry(case)
    restore(case)
    first = {name: tool_text(name, arguments) for name, arguments in CLOCK_CALLS}
    print(paint(f"Waiting {seconds} s, past every freshness limit...", DIM), flush=True)
    time.sleep(seconds)
    second = {name: tool_text(name, arguments) for name, arguments in CLOCK_CALLS}
    different = [name for name in first if first[name] != second[name]]
    for name in first:
        print(f"  {paint('✗', BOLD, RED) if name in different else paint('✓', GREEN)} {name}")
    for name in different:
        print(paint(f"{name} before: {preview(first[name], 400)}\n{name} after:  {preview(second[name], 400)}", DIM))
    print(paint("RESULT: SAME" if not different else f"RESULT: DIFFERENT ({len(different)})", BOLD, GREEN if not different else RED))
    return 0 if not different else 1


def grade_corpus():
    """The grader against reviewed answers: each must get the verdict it was labelled with."""
    cases = load_cases()
    corpus = json.loads(Path("grading/answers.json").read_text(encoding="utf-8"))
    wrong = 0
    for item in corpus:
        checks = grading.grade(item["answer"], cases[item["scenario"]]["rubric"])
        verdict = "pass" if all(ok for _, ok, _ in checks) else "fail"
        ok = verdict == item["expected"]
        wrong += not ok
        print(f"{paint('✓', GREEN) if ok else paint('✗', BOLD, RED)} {item['scenario']:<20} {item['kind']:<32} expected {item['expected']}, graded {verdict}")
        if not ok:
            for label, passed, clause in checks:
                print(paint(f"      {'ok ' if passed else 'NO '} {label}  «{preview(clause or '', 100)}»", DIM))
    covered = {item["scenario"] for item in corpus}
    missing = [i for i in cases if i not in covered]
    print(f"\n{len(corpus) - wrong} of {len(corpus)} graded as labelled (grader {grading.VERSION})."
          + (f" No calibration answers for: {', '.join(missing)}." if missing else ""))
    return 0 if wrong == 0 else 1


# ── Report ────────────────────────────────────────────────────────────────────────────────────────────────────────

def load_results():
    path = OUT / "results.jsonl"
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()] if path.exists() else []
    seen = {}
    for row in rows:  # rows written before statuses and attempts existed
        row.setdefault("status", "pass" if row.get("passed") else "fail")
        row.setdefault("mode", "prompt" if row.get("prompt") is True else "question")
        if "attempt" not in row:
            key = (row.get("run"), row["scenario"])
            row["attempt"] = seen[key] = seen.get(key, 0) + 1
    return rows


def load_run(run_id, rows):
    path = RUNS / f"{run_id}.json"
    manifest = json.loads(path.read_text(encoding="utf-8")) if path.exists() else None
    own = [r for r in rows if r["run"] == run_id]
    latest = {}
    for row in own:
        latest[(row["scenario"], row["attempt"])] = row
    if manifest:
        for attempt in manifest["attempts"]:  # planned but never recorded: the runner itself failed
            latest.setdefault((attempt["case"], attempt["attempt"]), {"scenario": attempt["case"], "attempt": attempt["attempt"], "status": "error",
                                                                      "stage": "runner", "error": "no result recorded", "calls": 0})
    per_case = {}
    for (case_id, _), row in sorted(latest.items()):
        per_case.setdefault(case_id, []).append(row)
    return manifest, per_case


def summarize(rows):
    graded = [r for r in rows if r["status"] in ("pass", "fail")]
    return {"passed": sum(r["status"] == "pass" for r in rows), "graded": len(graded), "errors": sum(r["status"] == "error" for r in rows),
            "skipped": sum(r["status"] == "skipped" for r in rows),
            "rate": (sum(r["status"] == "pass" for r in graded) / len(graded)) if graded else None,
            "calls": mean([r.get("calls", 0) for r in graded]), "toolErrors": sum(r.get("errors", 0) for r in graded),
            "tokens": mean([r.get("promptTokens", 0) for r in graded]), "seconds": mean([r.get("seconds", 0) for r in graded])}


def mean(values):
    return sum(values) / len(values) if values else None


def incomparable(case_id, run, base):
    """Why a case of two runs cannot be compared, or None: its definition, snapshot, form, grader or model differ."""
    if not run or not base:
        return "no run manifest"
    a, b = run["cases"].get(case_id), base["cases"].get(case_id)
    if b is None:
        return "not in the baseline"
    reasons = [what for what, differs in (("definition", a["definition"] != b["definition"]), ("snapshot", a["snapshotSha"] != b["snapshotSha"]),
                                          ("form", a.get("prompt") != b.get("prompt")), ("grader", run["grader"] != base["grader"]),
                                          ("model", run["model"] != base["model"])) if differs]
    return ", ".join(reasons) + " differ" if reasons else None


def fmt(value, digits=1):
    return "-" if value is None else f"{value:.{digits}f}"


def report(run_id, baseline_id):
    """The newest planned run (or the one named), compared only with the baseline named."""
    rows = load_results()
    if not run_id:
        planned = sorted(p.stem for p in RUNS.glob("*.json")) if RUNS.exists() else []
        if not planned:
            print("No runs yet. Run: .\\platform.ps1 evals run")
            return 0
        run_id = planned[-1]

    run, per_case = load_run(run_id, rows)
    base, base_cases = load_run(baseline_id, rows) if baseline_id else (None, {})
    if baseline_id and not base and not base_cases:
        print(paint(f"No baseline run {baseline_id}.", RED))
        return 2
    title = f"Run {run_id}"
    if run:
        title += f": {run['model']}, {run['repeat']} attempt(s) each, implementation {run['implementation']}"
    print(paint(title, BOLD))
    if base:
        print(paint(f"Baseline {baseline_id}: {base['model']}, implementation {base['implementation']}", DIM))
    if not run:
        print(paint("This run has no manifest (recorded before manifests existed): its planned attempts and inputs are unknown.", YELLOW))

    compare = f"{'Baseline':<9} {'Change':<14} " if baseline_id else ""
    print(f"\n{'Case':<26} {'Set':<8} {'Passed':<8} {compare}{'Errors':<7} {'Calls':<6} {'Tokens':<8} Seconds")
    regressions, improvements, compared_now, compared_base, notes = [], [], [], [], []
    for case_id, case_rows in per_case.items():
        s = summarize(case_rows)
        if s["graded"] == 0 and s["errors"] == 0:
            continue
        why_not = incomparable(case_id, run, base) if baseline_id else "no baseline"
        b = summarize(base_cases.get(case_id, [])) if baseline_id and not why_not else None
        change = ""
        if b and s["rate"] is not None and b["rate"] is not None:
            compared_now.append(s["rate"])
            compared_base.append(b["rate"])
            change = "REGRESSED" if s["rate"] < b["rate"] else "improved" if s["rate"] > b["rate"] else "same"
            (regressions if change == "REGRESSED" else improvements if change == "improved" else []).append(case_id)
        elif baseline_id:
            change = "incomparable"
            notes.append(f"{case_id}: not compared ({why_not})")
        set_name = next((r.get("set") for r in case_rows if r.get("set")), "")
        was = f"{b['passed']}/{b['graded']}" if b else "-"
        shown = paint(f"{change:<14}", BOLD, RED) if change == "REGRESSED" else f"{change:<14}"
        compared = f"{was:<9} {shown} " if baseline_id else ""
        print(f"{case_id:<26} {set_name:<8} {s['passed']}/{s['graded']:<6} {compared}{s['errors']:<7} "
              f"{fmt(s['calls']):<6} {fmt(s['tokens'], 0):<8} {fmt(s['seconds'], 0)}")

    everything = [r for case_rows in per_case.values() for r in case_rows]
    total = summarize(everything)
    case_rates = [summarize(rows_)["rate"] for rows_ in per_case.values() if summarize(rows_)["rate"] is not None]
    print(f"\nPassed {total['passed']} of {total['graded']} attempts; {fmt(100 * mean(case_rates) if case_rates else None, 0)} % per case on average."
          + (f" Baseline {fmt(100 * mean(compared_base), 0)} % on the {len(compared_now)} comparable case(s), now {fmt(100 * mean(compared_now), 0)} %." if compared_now else ""))
    if regressions:
        print(paint(f"Regressed: {', '.join(regressions)}", BOLD, RED))
    if improvements:
        print(paint(f"Improved: {', '.join(improvements)}", GREEN))
    if total["errors"]:
        print(paint(f"Incomplete: {total['errors']} attempt(s) failed to run and are not graded:", BOLD, YELLOW))
        for r in everything:
            if r["status"] == "error":
                print(paint(f"  {r['scenario']} #{r['attempt']} at {r.get('stage')}: {r.get('error')}", YELLOW))
    for note in notes:
        print(paint(note, DIM))
    print(f"Per attempt: {fmt(total['calls'])} tool calls, {total['toolErrors']} tool errors in all, {fmt(total['tokens'], 0)} prompt tokens, {fmt(total['seconds'], 0)} s.")
    return 1 if regressions or total["errors"] else 0


def option(name):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv and sys.argv.index(name) + 1 < len(sys.argv) else None


def main():
    arguments = [a for a in sys.argv[1:] if not a.startswith("--")]
    command = arguments[0] if arguments else ""
    if command == "grade":
        return grade_corpus()
    if command == "report":
        return report(option("--run"), option("--baseline"))
    cases = load_cases()
    if command == "plan" and len(arguments) >= 4:
        return plan(arguments[1], int(arguments[2]), arguments[3:])
    if len(arguments) < 2 or command not in ("record", "run", "clock") or arguments[1] not in cases:
        print(__doc__)
        print(f"Cases: {', '.join(cases)}")
        return 2
    case = cases[arguments[1]]
    print(f"\n{paint('━━ ' + case['id'] + ': ' + case['title'] + ' ━━', BOLD, CYAN)}", flush=True)
    if command == "clock":
        return clock(case, int(arguments[2]) if len(arguments) > 2 else 90)
    return {"record": record, "run": run}[command](case)


if __name__ == "__main__":
    sys.exit(main())

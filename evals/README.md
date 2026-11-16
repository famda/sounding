# Evals: can a small model find the cause?

Each case breaks something in a private copy of the samples, asks a local model why through Sounding's MCP server, and
grades the answer. It measures the tools, their descriptions, the instructions and the prompts, not the model: when a
case fails, the question is what a tool result or the instructions should have said.

```powershell
.\platform.ps1 evals list                          # the cases
.\platform.ps1 evals run                           # every case
.\platform.ps1 evals run slow-db                   # one case (or tuning, or holdout)
.\platform.ps1 evals run -Repeat 3                 # three attempts per case, seeds 1 to 3
.\platform.ps1 evals run -Baseline 20261113-125840 # compare with an earlier run
.\platform.ps1 evals report                        # the newest run's results
.\platform.ps1 evals record slow-db                # re-record a case's snapshot
```

Everything runs in containers; nothing needs to be running first. The first run downloads the model (`qwen3.5:9b`,
6.6 GB). An NVIDIA GPU with 12 GB or more answers in under a minute; without one the model runs on the processor.

## Snapshots

Making a problem real takes minutes: containers start, a baseline builds up, the fault has to show in the data. That
is done once, by `evals record`, and what it leaves in Sounding's database is kept as a snapshot
([snapshots/](snapshots/), about 0.1 MB each, committed) with an entry in
[snapshots/manifest.json](snapshots/manifest.json): the file's hash, the replay format, the observation time and a
fingerprint of the database schema.

Recording creates a private copy of the solution's database and dashboard and of the samples
([environment/](environment/docker-compose.yml): own network, no ports on the host, no volumes), waits until every
container reports, lets it run 75 seconds as a baseline, injects the fault, waits for it to show, and saves the
database. The development stack is not touched. Record again after changing the agent, the database schema, the
samples or a fault; changing tools, descriptions, instructions or prompts never needs a new recording.

## Runs

A run starts only the database and the dashboard. For each case it replaces the whole database with the snapshot and
**holds the database clock at the snapshot's observation time**: in that database `now()` returns that moment, so
ages, windows, "online" and the dashboard's own clock read the same however long the model investigates
(`eval.py clock <case>` asks the same questions 90 s apart and checks the answers are identical). A snapshot whose
hash, format or schema no longer matches its manifest entry is refused. The dashboard restarts for each case, so
nothing of the case before remains.

The model gets what an MCP client gives it: the server's instructions as the system message and `tools/list` as the
tools, thinking on, a 65536-token context, temperature 0, the attempt number as seed, at most 20 turns. Each case is
asked in every form it has: its plain question, its server prompt, or both. Each server prompt has a case that fails
if it stops at the healthy current state (`investigate`: `past-outage`; `check_health`: `past-outage-health`;
`review_container`: `past-outage-review`).

Before anything runs, the run's manifest is written to `out/runs/<run>.json`: every planned attempt, a hash of each
case definition and snapshot, the model, the prompt used, the seeds, the grader version, and an implementation
fingerprint (the commit plus a hash of uncommitted changes to `src`, `samples` and `evals`). Each attempt appends a
line to `out/results.jsonl` with its status, `pass`, `fail` or `error` (an infrastructure failure, with the stage),
and its transcript to `out/<time>-<case>.json`. An attempt that never wrote a line counts as an error.

## Report

The report leads with correctness: attempts passed per case, then the mean pass rate per case (each case weighted
once), and any attempts that failed to run (the run is then marked incomplete). Tool calls, tool errors, tokens and
seconds follow on their own line; there is no composite score. With `-Baseline`, each case shows the baseline's
result and `REGRESSED`, `improved` or `same`; a case whose definition, snapshot, prompt, grader or model differs from
the baseline's is listed as not compared, with why. Runs are never compared implicitly.

A small model takes different paths over small differences: use `-Repeat 3` or more when a decision depends on it,
and read the failures and a sample of passes before treating a run as a baseline.

## Grading

Each case has a rubric in [scenarios.json](scenarios.json), graded by [grading.py](grading.py): conclusions the
answer (or its Cause section) must establish in one clause (or within a few, where a rubric sets `near`), and
conclusions it must not draw. A clause that also
contradicts a required conclusion ("the indexer has plenty of memory") does not count; a clause that exonerates
("shop-db is healthy") is not blame. The run prints the clause behind each verdict.

The grader is checked against labelled answers in [grading/answers.json](grading/answers.json) (right answers and
paraphrases, negations, wrong causes that use the expected words, exonerations, missing evidence, invented failures):
`eval.py grade`, in the runner image, must give each its label. Change a rubric only with an answer that shows why.
The labelled answers were written and labelled by their author: review them, and add real answers a person has
judged, before relying on a pass rate.

## Cases

`tuning` cases are for changing tools, descriptions, instructions and prompts against. `holdout` cases are only
measured; one that leads to a change moves to `tuning` and is replaced by a new kind of fault.

| Case | Set | What is broken | The right answer | The trap |
|---|---|---|---|---|
| `healthy` | tuning | Nothing. | "The data shows no problem." | Inventing a cause because the question assumes one. |
| `ambiguous` | tuning | Nothing; "api" matches two containers. | Asks which one, or covers both. | Picking one silently. |
| `slow-db` | tuning | `shop-db` limited to 0.01 CPU cores. | The database is held back by its own CPU limit. | Blaming `orders-api`, which reports the slow requests. |
| `stopped-dependency` | tuning | `catalog-api` stopped. | catalog-api is stopped, so orders cannot check items. | Blaming `orders-api`, or never concluding. |
| `noisy-neighbour` | tuning | `reports-batch`, with no CPU limit, burns every core. | That container takes the host's CPU. | Blaming each slow container separately. |
| `two-problems` | tuning | A worker in a crash loop, and the slow database. | The database; the worker is unrelated. | Blaming the loudest finding. |
| `frozen-service` | tuning | `catalog-api` paused: running, but silent. | catalog-api no longer answers or reports. | "It is running, so it is fine." |
| `cache-down` | holdout | The Redis cache stopped. | The cache is stopped; the catalog fails without it. | Stopping at `catalog-api`, one call short. |
| `disk-filling` | holdout | `exporter` writes 16 MiB a second. | The exporter's disk is filling, in `/var/exports`. | Answering from the host's free space. |
| `past-outage` | holdout | `orders-api` leaked memory, was killed and restarted. | It ran out of memory and restarted; it is fine now. | Answering from the current, healthy state. |
| `past-outage-health` | holdout | The same snapshot, through `check_health`. | Healthy now; orders-api was OOM-killed minutes ago. | Stopping at a healthy `diagnose`. |
| `past-outage-review` | holdout | The same snapshot, through `review_container`. | It runs, but was OOM-killed within the hour. | Reviewing only the current figures. |
| `memory-headroom` | holdout | `indexer` holds about 90 % of its memory limit. | Close to its limit; nothing has failed. | Reporting a failure, or missing it. |

To add a case: an entry in `scenarios.json` with a rubric, a labelled answer in `grading/answers.json`, then
`evals record <case>` (or `snapshot` naming an existing recording). `fault` names entries of `FAULTS` in
[eval.py](eval.py), joined with `+`. The model runs outside what Sounding monitors, or its own load would show up in
the data it is asked about.

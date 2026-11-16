"""Grades an answer against a scenario's rubric: what it must establish and what it must not conclude.

A rubric is a list of assertions. Each looks at the answer's Cause section ("cause") or the whole answer ("answer"),
split into clauses (sentences, list items, and parts joined by ";"). An assertion matches a clause when every group in
"say" has a pattern (a case-insensitive regular expression) found in that clause:

    {"must": "cause", "say": [["shop-db", "database"], ["throttl", "cpu"]], "unless": ["not throttl"], "why": "..."}
        passes when some clause matches and does not also match an "unless" pattern (a contradiction such as
        "plenty of memory" next to the right container).
    {"mustNot": "cause", "say": [["worker"]], "unless": ["unrelated", "separate"], "why": "..."}
        fails when some clause matches without an "unless" pattern (an exoneration such as "shop-db is healthy").

Patterns are deliberately plain and kept with the scenario, so a reader can see why an answer passed. The grader is
calibrated against grading/answers.json (eval.py grade): change a rubric only together with a case that shows why.
"""
import re

VERSION = 3

_CLAUSE = re.compile(r"(?<=[.!?;])\s+|\n+|\s+(?:but|whereas|while)\s+", re.IGNORECASE)


def cause_section(answer):
    """The text under the answer's Cause heading, up to the next heading; the first third when there is none."""
    match = re.search(r"^[#*\s\d.]*cause\b[^\n]*?:?(.*?)(?=\n[#*\s\d.]*(?:evidence|impact|what to do|uncertain)\b|\Z)",
                      answer, re.IGNORECASE | re.DOTALL | re.MULTILINE)
    return match.group(1) if match and len(match.group(1).strip()) > 20 else answer[: max(300, len(answer) // 3)]


def clauses(text):
    return [c.strip(" \t*-#>`_") for c in _CLAUSE.split(text) if c and c.strip(" \t*-#>`_")]


def _matches(clause, groups):
    return all(any(re.search(p, clause, re.IGNORECASE) for p in group) for group in groups)


def _exempt(clause, unless):
    return any(re.search(p, clause, re.IGNORECASE) for p in unless or [])


def _windows(parts, size):
    """Each run of `size` consecutive clauses, joined: a list item often names the subject and the next one the figure."""
    return parts if size <= 1 else [" ".join(parts[i:i + size]) for i in range(max(1, len(parts) - size + 1))]


def grade(answer, rubric):
    """[(label, passed, clause that decided it or None)] for each assertion. "near": n lets a "must" assertion's
    groups match within n consecutive clauses instead of one."""
    sections = {"cause": clauses(cause_section(answer)), "answer": clauses(answer)}
    results = []
    for rule in rubric:
        if "must" in rule:
            hits = [c for c in _windows(sections[rule["must"]], rule.get("near", 1)) if _matches(c, rule["say"])]
            good = [c for c in hits if not _exempt(c, rule.get("unless"))]
            results.append((rule["why"], bool(good), (good or hits or [None])[0]))
        else:
            bad = [c for c in sections[rule["mustNot"]] if _matches(c, rule["say"]) and not _exempt(c, rule.get("unless"))]
            results.append((rule["why"], not bad, bad[0] if bad else None))
    return results

"""The parts of the frame rate log report that need the second-round columns (format 2), and the profile file.

Used by compare_perf_logs.py; every section says nothing if the log it is given lacks the columns, so older logs still work.
"""
import collections
import os
import statistics

PHASE_NAMES = ["plTime", "plInit", "plEarly", "plFixed", "plPre", "plUpdate", "plLate", "plPost"]
PHASE_MEANING = {
    "plTime": "time update (Unity's wait for the last frame to be presented can be here)",
    "plInit": "initialization", "plEarly": "early update (input)", "plFixed": "fixed update",
    "plPre": "pre-update", "plUpdate": "update (the game's scripts, this mod's tick loop)",
    "plLate": "late update", "plPost": "post late update (drawing, presenting, vertical sync)",
}
ALLOC_SLOTS = ["gameKB", "tebKB", "replayKB", "serKB", "hashKB", "compKB", "sendKB", "recvKB", "deserKB", "steamKB",
               "logKB", "detailKB", "uiKB", "saveKB", "animKB", "speedKB", "tebLookKB", "tebAnimKB", "parKB", "otherKB"]
PER_TICK_COUNTERS = ["entities", "movers", "entTicks", "nRng", "nGuid", "nSpawn", "nSpeed"]
PER_FRAME_COUNTERS = ["nAnim", "nTime", "nDayNight", "nSound", "nInput", "nTicker"]


def has(log, *columns):
    return all(c in log.columns for c in columns)


def summaries(log):
    return [r for r in log.rows_of("S") if r["frames"] > 0]


def total_frames(rows):
    return sum(r["frames"] for r in rows)


def wmean(rows, column):
    frames = total_frames(rows)
    return sum(r[column] * r["frames"] for r in rows) / frames if frames else 0.0


def total_ticks(rows):
    return sum(r["ticks"] for r in rows)


def edges_of(log):
    for parts in log.extras.get("histogram", []):
        if parts and parts[0] == "frameEdgesMs" and len(parts) > 1:
            return [float(x) for x in parts[1].split(",")]
    return []


# ---------------------------------------------------------------- sections

def histogram(log):
    S = summaries(log)
    if not S or not has(log, "fh0"):
        return []
    edges = edges_of(log)
    n = len([c for c in log.columns if c.startswith("fh")])
    counts = [sum(r["fh%d" % i] for r in S) for i in range(n)]
    total = sum(counts)
    if total <= 0:
        return []
    lines = ["%s: %d frames; how long they took:" % (log.label, total)]
    running = 0
    for i, c in enumerate(counts):
        running += c
        if c / total < 0.004:
            continue
        if edges and i < len(edges):
            label = "under %.1f ms" % edges[i] if i == 0 else "%.1f to %.1f ms" % (edges[i - 1], edges[i])
        elif edges:
            label = "%.0f ms or more" % edges[-1]
        else:
            label = "bucket %d" % i
        lines.append("   %-18s %5.1f%%   (%5.1f%% at most this long)" % (label, 100.0 * c / total, 100.0 * running / total))
    return lines


def ticks_per_frame(log):
    S = summaries(log)
    if not S or not has(log, "th0"):
        return []
    labels = ["0", "1", "2", "3-4", "5-9", "10+"]
    counts = [sum(r["th%d" % i] for r in S) for i in range(6)]
    total = sum(counts)
    if total <= 0:
        return []
    return ["%s: ticks run per frame: " % log.label + ", ".join("%s: %.1f%%" % (l, 100.0 * c / total) for l, c in zip(labels, counts))]


def phases(log):
    S = summaries(log)
    if not S or not has(log, "plUpdate"):
        return []
    frame = wmean(S, "frameMs")
    means = {p: wmean(S, p) for p in PHASE_NAMES}
    if sum(means.values()) <= 0:
        return ["%s: the phase timing produced nothing (see the capability lines)" % log.label]
    lines = ["%s: mean frame %.1f ms; time in each phase of Unity's frame:" % (log.label, frame)]
    for p in PHASE_NAMES:
        if means[p] >= 0.05:
            lines.append("   %-9s %6.2f ms  %5.1f%%   %s" % (p, means[p], 100 * means[p] / frame if frame else 0, PHASE_MEANING[p]))
    lines.append("   (outside every phase: %.2f ms)" % max(0.0, frame - sum(means.values())))
    return lines


def cpu(log):
    S = summaries(log)
    if not S or not has(log, "mainCpuMs"):
        return []
    frame = wmean(S, "frameMs")
    main = wmean(S, "mainCpuMs")
    proc = wmean(S, "procCpuMs")
    if main <= 0 and proc <= 0:
        return ["%s: processor time was not available" % log.label]
    lines = ["%s: the game thread used %.1f of every %.1f ms of a frame (%.0f%% busy); the whole process used %.1f ms (%.1f cores' worth)" %
             (log.label, main, frame, 100 * main / frame if frame else 0, proc, proc / frame if frame else 0)]
    if has(log, "ftMain") and wmean(S, "ftMain") > 0:
        lines.append("   Unity's frame timing: main thread %.1f ms, render thread %.1f ms, graphics card %.1f ms, main thread waiting to present %.1f ms" %
                     (wmean(S, "ftMain"), wmean(S, "ftRender"), wmean(S, "ftGpu"), wmean(S, "ftWait")))
    return lines


def rendering(log):
    S = summaries(log)
    if not S or not has(log, "prDraw") or wmean(S, "prDraw") <= 0:
        return []
    return ["%s: per frame %.0f draw calls, %.0f set-pass calls, %.0f batches, %.0f thousand triangles" %
            (log.label, wmean(S, "prDraw"), wmean(S, "prSetPass"), wmean(S, "prBatches"), wmean(S, "prTris") / 1000.0)]


def allocation(log):
    S = summaries(log)
    if not S or not has(log, "gameKB"):
        return []
    ticks = total_ticks(S)
    seconds = sum(r["frameMs"] * r["frames"] for r in S) / 1000.0
    if ticks <= 0:
        return []
    per = {c: sum(r[c] for r in S) / ticks for c in ALLOC_SLOTS}
    total = sum(per.values())
    measured = sum(r["allocKB"] for r in S) / ticks
    if total <= 0:
        return ["%s: allocation per section was not available on this computer" % log.label]
    lines = ["%s: allocated per tick, by the section that did it (%.0f KB/tick in the sections, %.0f KB/tick by the heap, %.0f KB/s):" %
             (log.label, total, measured, sum(r["allocKB"] for r in S) / seconds)]
    for name, kb in sorted(per.items(), key=lambda kv: -kv[1])[:8]:
        if kb >= 0.5:
            lines.append("   %-11s %7.1f KB/tick  %5.1f%%" % (name, kb, 100 * kb / total))
    return lines


def counters(log):
    S = summaries(log)
    if not S or not has(log, "entities"):
        return []
    ticks, frames = total_ticks(S), total_frames(S)
    if ticks <= 0 or frames <= 0:
        return []
    per_tick = ", ".join("%s %.0f" % (c, sum(r[c] for r in S) / ticks) for c in PER_TICK_COUNTERS)
    per_frame = ", ".join("%s %.0f" % (c, sum(r[c] for r in S) / frames) for c in PER_FRAME_COUNTERS)
    lines = ["%s: per tick: %s" % (log.label, per_tick), "   per frame: %s" % per_frame]
    if has(log, "parTickMs"):
        lines.append("   the game's own figure for its parallel tick: %.2f ms per tick; the game thread waited %.2f ms per tick for it" %
                     (sum(r["parTickMs"] for r in S) / ticks, sum(r["parMs"] * r["frames"] for r in S) / ticks))
    return lines


def entity_pass(log):
    S = summaries(log)
    if not S or not has(log, "tebLookMs"):
        return []
    ticks = total_ticks(S)
    if ticks <= 0:
        return []
    frames = total_frames(S)
    def per_tick(column):
        return sum(r[column] * r["frames"] for r in S) / ticks
    entities = sum(r["entities"] for r in S) / ticks
    lines = ["%s: the entity pass per tick: %.2f ms in all (%.2f ms hashing and looping, %.2f ms looking up animators, %.2f ms updating walkers), for %.0f entities of which %.0f walk" %
             (log.label, per_tick("tebMs") + per_tick("tebLookMs") + per_tick("tebAnimMs"), per_tick("tebMs"), per_tick("tebLookMs"),
              per_tick("tebAnimMs"), entities, sum(r["movers"] for r in S) / ticks)]
    if entities > 0:
        lines.append("   = %.2f microseconds per entity" % (1000 * (per_tick("tebMs") + per_tick("tebLookMs") + per_tick("tebAnimMs")) / entities))
    anim_calls = sum(r["nAnim"] for r in S)
    if anim_calls > 0:
        lines.append("   the animation update replacement: %.2f ms per frame, %.2f microseconds per call, %.0f calls per frame" %
                     (wmean(S, "animMs"), 1000 * sum(r["animMs"] * r["frames"] for r in S) / anim_calls, anim_calls / frames))
    if sum(r["nSpeed"] for r in S) > 0:
        lines.append("   speed changes: %d in the session, %.2f ms each" % (sum(r["nSpeed"] for r in S), sum(r["speedMs"] * r["frames"] for r in S) / sum(r["nSpeed"] for r in S)))
    return lines


def overhead(log):
    S = summaries(log)
    if not S or not has(log, "overheadUs"):
        return []
    frame = wmean(S, "frameMs")
    est = wmean(S, "overheadUs")
    probe = wmean(S, "probeUs")
    return ["%s: what the log itself costs, per frame: about %.0f us in measured sections and samples, %.0f us to close the frame: together %.2f%% of a %.1f ms frame" %
            (log.label, est, probe, 100 * (est + probe) / 1000.0 / frame if frame else 0, frame)]


def environment(logs):
    if len(logs) != 2:
        return []
    a, b = logs
    lines = []
    for key in ("cpu", "gpu", "graphics", "display", "quality", "gc", "memoryMB", "os", "gcExperiment"):
        va, vb = a.header.get(key), b.header.get(key)
        if va is None and vb is None:
            continue
        if va == vb:
            lines.append("same %s: %s" % (key, va))
        else:
            lines.append("%s differs:" % key)
            lines.append("   %-14s %s" % (a.role, va))
            lines.append("   %-14s %s" % (b.role, vb))
    def group(log, key):
        return [" ".join(parts) for parts in log.extras.get(key, [])]
    for key, what in (("bootconfig", "boot.config"), ("cmdline", "launch options")):
        ga, gb = group(a, key), group(b, key)
        if ga or gb:
            only_a, only_b = [x for x in ga if x not in gb], [x for x in gb if x not in ga]
            if not only_a and not only_b:
                lines.append("%s: identical (%d entries)" % (what, len(ga)))
            else:
                lines.append("%s differs:" % what)
                for x in only_a:
                    lines.append("   only %-8s %s" % (a.role, x))
                for x in only_b:
                    lines.append("   only %-8s %s" % (b.role, x))
    return lines


def capabilities(log):
    lines = []
    for key in ("capability", "capability-final"):
        for parts in log.extras.get(key, []):
            lines.append("   %s: %s" % (key, " | ".join(parts)))
    if not lines:
        return []
    return ["%s:" % log.label] + lines


def milestones(log):
    rows = log.extras.get("milestone", [])
    if not rows:
        return []
    return ["%s: managed heap / process memory at each stage: " % log.label + "; ".join("%s %s MB / %s MB" % (p[0], p[2], p[3]) for p in rows if len(p) >= 4)]


def events(log):
    """The GC experiment, if one ran, and what happened to the collection pauses either side of it."""
    rows = log.extras.get("event", [])
    if not rows:
        return []
    lines = ["%s:" % log.label] + ["   %s" % " | ".join(p) for p in rows]
    marker = None
    for p in rows:
        if len(p) >= 2 and p[1].startswith("gc-experiment") and p[0].startswith("tick "):
            try:
                marker = int(p[0].split()[1])
            except ValueError:
                pass
            break
    if marker is not None:
        gc_rows = [r for r in log.rows_of("F") if r["gcDelta"] > 0 and not r["saving"]]
        before = [r["frameMs"] for r in gc_rows if r["tick"] < marker]
        after = [r["frameMs"] for r in gc_rows if r["tick"] >= marker]
        if before and after:
            lines.append("   collection pauses: %d before tick %d (median %.0f ms, worst %.0f); %d after (median %.0f ms, worst %.0f)" %
                         (len(before), marker, statistics.median(before), max(before), len(after), statistics.median(after), max(after)))
        else:
            lines.append("   too few collections in slow frames to compare before (%d) and after (%d)" % (len(before), len(after)))
    return lines


# ---------------------------------------------------------------- the profile file

class Profile:
    def __init__(self, path):
        self.path = path
        self.names = {}
        self.rows = []
        self.header = {}
        self.columns = []


def profile_path(log_path):
    base, ext = os.path.splitext(log_path)
    return base + "-profile" + ext


def parse_profile(path):
    profile = Profile(path)
    with open(path, "r", encoding="utf-8", errors="replace", newline="") as f:
        lines = f.read().splitlines()
    for line in lines:
        if not line.strip():
            continue
        if line.startswith("#"):
            text = line[1:].strip()
            if text.startswith("name|"):
                parts = text.split("|")
                if len(parts) >= 5:
                    profile.names[int(parts[2])] = (parts[1], parts[3], parts[4])
            elif ": " in text and not text.startswith("note:"):
                key, _, value = text.partition(": ")
                profile.header[key.strip()] = value.strip()
            continue
        fields = line.split(",")
        if not profile.columns:
            profile.columns = fields
            continue
        if len(fields) != len(profile.columns):
            continue
        row = {}
        for name, value in zip(profile.columns, fields):
            row[name] = value if name == "type" else float(value or 0)
        profile.rows.append(row)
    return profile


def profile_totals(profile):
    """Per (kind, id): total estimated milliseconds, allocation, calls, and the slowest single call."""
    totals = collections.defaultdict(lambda: {"ms": 0.0, "kb": 0.0, "calls": 0.0, "max": 0.0, "samples": 0.0})
    for r in profile.rows:
        t = totals[(r["type"], int(r["id"]))]
        t["ms"] += r["ms"]; t["kb"] += r["allocKB"]; t["calls"] += r["calls"]; t["samples"] += r["sampled"]
        t["max"] = max(t["max"], r["maxMs"])
    return totals


def profile_lines(log, profile, top=12):
    if profile is None or not profile.rows:
        return []
    ticks = total_ticks(summaries(log))
    if ticks <= 0:
        return []
    totals = profile_totals(profile)
    lines = ["%s: where the tick's time and garbage go (estimated from sampled calls; per tick over %d ticks):" % (log.label, ticks)]
    for kind, title in (("E", "entities, by prefab"), ("G", "singletons")):
        rows = [(key, t) for key, t in totals.items() if key[0] == kind]
        if not rows:
            lines.append("   no %s rows (the patch that times them may not have run)" % title)
            continue
        total_ms = sum(t["ms"] for _, t in rows)
        total_kb = sum(t["kb"] for _, t in rows)
        lines.append("   %s: %.2f ms and %.0f KB per tick in all" % (title, total_ms / ticks, total_kb / ticks))
        for (_, ident), t in sorted(rows, key=lambda kv: -(kv[1]["ms"] + kv[1]["kb"] / 100.0))[:top]:
            name, assembly = profile.names.get(ident, ("?", "?", "?"))[1:3]
            lines.append("      %-52s %6.2f ms/tick %5.1f%%  %7.1f KB/tick %5.1f%%   slowest sampled call %.2f ms%s" %
                         (name[-52:], t["ms"] / ticks, 100 * t["ms"] / total_ms if total_ms else 0,
                          t["kb"] / ticks, 100 * t["kb"] / total_kb if total_kb else 0, t["max"],
                          "  [%s]" % assembly if kind == "G" and assembly else ""))
    return lines


# ---------------------------------------------------------------- assembling them

def sections(logs, profiles):
    """A list of (title, lines) for the sections that have something to say."""
    out = []
    def each(title, fn):
        lines = []
        for log in logs:
            lines += fn(log)
        if lines:
            out.append((title, lines))
    each("HOW LONG FRAMES TAKE (every frame, not only the slow ones)", histogram)
    each("HOW MANY TICKS EACH FRAME RUNS", ticks_per_frame)
    each("UNITY'S PHASES OF A FRAME", phases)
    each("THE GAME THREAD: BUSY OR WAITING", cpu)
    each("WHAT IS DRAWN", rendering)
    each("WHAT ALLOCATES", allocation)
    each("HOW OFTEN THINGS HAPPEN, AND THE ENTITY PASS", lambda log: counters(log) + entity_pass(log))
    each("WHERE THE TICK GOES (THE PROFILE FILES)", lambda log: profile_lines(log, profiles.get(log.path)))
    each("WHAT THE LOG COSTS ITSELF", overhead)
    each("MEMORY BEFORE THE SESSION STARTED", milestones)
    env = environment(logs)
    if env:
        out.append(("HOW THE TWO COMPUTERS DIFFER", env))
    each("WHAT EACH COMPUTER COULD MEASURE", capabilities)
    each("EVENTS DURING THE SESSION (THE GC EXPERIMENT)", events)
    return out

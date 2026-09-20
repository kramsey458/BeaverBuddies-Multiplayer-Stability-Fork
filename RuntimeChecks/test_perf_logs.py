"""Checks for compare_perf_logs.py, on made-up logs. Run: python -m unittest discover -s RuntimeChecks -p "test_perf_logs.py" """
import io
import os
import re
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import compare_perf_logs as cpl  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
COLUMNS_SOURCE = os.path.join(HERE, "perf_columns.txt")


def csharp_columns():
    """The column names as the game writes them. perf_columns.txt is what the C# prints (StabilityTests --print-perf-columns), and a C#
    check fails if it goes out of date, so the two cannot drift apart."""
    with open(COLUMNS_SOURCE, encoding="utf-8") as f:
        return f.readline().strip().split(",")


COLUMNS = csharp_columns()
BUILD = "game=1.0.5;mod=1.0.10;modBuild=aaaa;netBuild=bbbb"
MODS = [("beaverbuddies", "BeaverBuddies", "1.0.10"), ("harmony", "Harmony", "v2.4.1"), ("modsettings", "ModSettings", "v1.1.0")]


def line(kind, frame, tick, **values):
    row = {name: 0 for name in COLUMNS}
    row.update(type=kind, frame=frame, tick=tick, focused=1, speed=1, target=1, frames=1 if kind == "F" else 0)
    row.update(values)
    return ",".join(str(row[name]) for name in COLUMNS)


def log_text(role, rows, mods=MODS, build=BUILD, detailed="off", player="P", ended=True, extra=()):
    lines = ["# BeaverBuddies frame rate log, format 1", "# role: " + role, "# player: " + player, "# mod: 1.0.10",
             "# build: " + build, "# detailedLogging: " + detailed, "# thresholdMs: 50", "# summaryTicks: 100"]
    lines += ["# mod|%s|%s|%s" % m for m in mods]
    lines += list(extra)
    lines.append(",".join(COLUMNS))
    lines += rows
    if ended:
        lines.append("# end")
    return "\n".join(lines) + "\n"


def parse(text, name="log.csv"):
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, name)
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        return cpl.parse_log(path)


def analyse(host_rows, guest_rows, window=3):
    host, guest = parse(log_text("host", host_rows), "host.csv"), parse(log_text("guest", guest_rows), "guest.csv")
    results, _ = cpl.analyse([host, guest], window, False)
    return results


def classes(results):
    return [r["class"] for r in results]


class ColumnsAgree(unittest.TestCase):
    def test_python_and_csharp_agree_on_the_slots(self):
        slots = COLUMNS[COLUMNS.index("gameMs"):COLUMNS.index("parMs") + 1]
        self.assertEqual(slots, cpl.SLOTS)
        self.assertIn("otherMs", COLUMNS)
        for name in cpl.SLOT_MEANING:
            self.assertIn(name, COLUMNS)

    def test_the_columns_the_script_reads_exist(self):
        for name in ("type", "frame", "tick", "utcMs", "frames", "frameMs", "maxFrameMs", "ticks", "waiting", "waitMs", "speed",
                     "target", "saving", "focused", "gcDelta", "heapMB", "allocKB", "probeUs"):
            self.assertIn(name, COLUMNS)


class Reading(unittest.TestCase):
    def test_header_mods_and_rows(self):
        log = parse(log_text("host", [line("F", 1, 10, frameMs=80), line("W", 2, 11, waitMs=60)], player="Kyler"))
        self.assertEqual(log.role, "host")
        self.assertEqual(log.player, "Kyler")
        self.assertEqual(log.mods["harmony"], ("Harmony", "v2.4.1"))
        self.assertEqual(len(log.rows_of("F")), 1)
        self.assertEqual(log.rows_of("W")[0]["waitMs"], 60.0)
        self.assertTrue(log.format_ok and log.ended)

    def test_a_file_cut_short_is_still_read(self):
        text = log_text("host", [line("F", 1, 10, frameMs=80)], ended=False) + line("F", 2, 11, frameMs=90)[:30]
        log = parse(text)
        self.assertEqual(len(log.rows), 1)
        self.assertEqual(log.skipped_rows, 1)
        self.assertFalse(log.ended)

    def test_patch_lines_are_read(self):
        patch = "# patch|hot|A.B.TickBuckets|prefix|beaverbuddies|priority=400|index=0|before=|after=|BeaverBuddies|BeaverBuddies.P.Prefix"
        log = parse(log_text("host", [], extra=[patch]))
        self.assertEqual(log.patches[0]["owner"], "beaverbuddies")
        self.assertEqual(log.patches[0]["assembly"], "BeaverBuddies")


class Comparable(unittest.TestCase):
    def pair(self, guest_mods=MODS, guest_build=BUILD, guest_detailed="off"):
        host = parse(log_text("host", []), "host.csv")
        guest = parse(log_text("guest", [], mods=guest_mods, build=guest_build, detailed=guest_detailed), "guest.csv")
        return cpl.compare_environment(host, guest)

    def test_the_same_setup_is_comparable(self):
        problems, warnings = self.pair()
        self.assertEqual(problems, [])
        self.assertEqual(warnings, [])

    def test_a_mod_only_one_player_has_is_flagged(self):
        problems, _ = self.pair(guest_mods=MODS + [("housing", "OptimizedLocalHousing", "1.0")])
        self.assertTrue(any("Only guest" in p and "housing" in p for p in problems), problems)

    def test_a_different_version_of_a_mod_is_flagged(self):
        problems, _ = self.pair(guest_mods=[MODS[0], ("harmony", "Harmony", "v2.5.0"), MODS[2]])
        self.assertTrue(any("harmony" in p and "v2.4.1" in p and "v2.5.0" in p for p in problems), problems)

    def test_a_different_compiled_build_is_flagged(self):
        problems, _ = self.pair(guest_build=BUILD.replace("aaaa", "cccc"))
        self.assertTrue(any("compiled build of this mod" in p for p in problems), problems)

    def test_a_different_game_version_is_flagged(self):
        problems, _ = self.pair(guest_build=BUILD.replace("1.0.5", "1.0.6"))
        self.assertTrue(any("game version" in p for p in problems), problems)

    def test_detailed_logging_on_one_side_is_a_warning_not_a_problem(self):
        problems, warnings = self.pair(guest_detailed="on")
        self.assertEqual(problems, [])
        self.assertTrue(any("Detailed logging" in w for w in warnings), warnings)


class Baseline(unittest.TestCase):
    def test_summary_rows_are_weighted_by_the_frames_they_cover(self):
        log = parse(log_text("host", [
            line("S", 10, 100, frames=10, frameMs=20, maxFrameMs=90, gameMs=10, otherMs=10, utcMs=1000000, heapMB=100, gcDelta=2, allocKB=500),
            line("S", 40, 200, frames=30, frameMs=10, maxFrameMs=30, gameMs=4, otherMs=6, utcMs=1010000, heapMB=140, gcDelta=1, allocKB=900),
        ]))
        b = cpl.baseline(log)
        self.assertAlmostEqual(b["mean_ms"], 12.5)
        self.assertEqual(b["worst_ms"], 90)
        self.assertEqual(b["gc"], 3)
        self.assertAlmostEqual(b["gc_per_minute"], 3 / (10.0 / 60.0))
        self.assertEqual((b["heap_min"], b["heap_max"]), (100, 140))
        self.assertAlmostEqual(b["shares"]["otherMs"], (10 * 10 + 30 * 6) / (20 * 10 + 10 * 30))


class Classes(unittest.TestCase):
    def test_a_host_stall_and_the_guest_waiting_then_catching_up(self):
        host = [line("F", 100, 500, frameMs=300, gcDelta=1, gameMs=250, ticks=1)]
        guest = [line("W", 90, 501, waitMs=280, waiting=6),
                 line("F", 95, 505, frameMs=120, ticks=5, speed=6, target=1, gameMs=100)]
        results = analyse(host, guest)
        self.assertEqual(classes(results), ["local_hitch_with_remote_wait"])
        self.assertIn("host", results[0]["text"])
        self.assertIn("GC", results[0]["text"])
        self.assertIn("waited and then caught up", results[0]["text"])

    def test_a_stall_on_the_host_with_only_the_guests_wait_recorded(self):
        results = analyse([line("F", 1, 500, frameMs=200, gameMs=150, ticks=1)], [line("W", 1, 501, waitMs=190, waiting=4)])
        self.assertEqual(classes(results), ["local_hitch_with_remote_wait"])
        self.assertIn("190", results[0]["text"])

    def test_collections_on_both_at_once(self):
        results = analyse([line("F", 1, 800, frameMs=90, gcDelta=1, gameMs=40)], [line("F", 1, 801, frameMs=110, gcDelta=1, gameMs=50)])
        self.assertEqual(classes(results), ["simultaneous_gc"])

    def test_waits_on_both_with_no_slow_frame(self):
        results = analyse([line("W", 1, 900, waitMs=70, waiting=2)], [line("W", 1, 901, waitMs=90, waiting=3)])
        self.assertEqual(classes(results), ["waits_both_no_cause"])

    def test_a_hitch_the_other_player_never_felt(self):
        results = analyse([], [line("F", 1, 1000, frameMs=150, otherMs=140, ticks=1)])
        self.assertEqual(classes(results), ["local_hitch_no_remote_wait"])
        self.assertIn("outside this mod", results[0]["text"])

    def test_a_wait_with_no_hitch_anywhere(self):
        results = analyse([], [line("W", 1, 1100, waitMs=120, waiting=5)])
        self.assertEqual(classes(results), ["wait_no_remote_hitch"])

    def test_a_wait_then_a_catch_up_burst_on_the_same_player(self):
        results = analyse([], [line("W", 1, 1200, waitMs=200, waiting=6),
                               line("F", 2, 1204, frameMs=130, ticks=6, speed=7, target=1, gameMs=110)])
        self.assertEqual(classes(results), ["wait_then_catch_up"])

    def test_events_far_apart_stay_separate(self):
        results = analyse([line("F", 1, 100, frameMs=90, gameMs=80, ticks=1)], [line("F", 1, 400, frameMs=90, gameMs=80, ticks=1)])
        self.assertEqual(len(results), 2)

    def test_a_slow_frame_in_the_background_is_left_out(self):
        host = parse(log_text("host", [line("F", 1, 100, frameMs=900, focused=0, otherMs=900)]), "host.csv")
        results, ignored = cpl.analyse([host], 0, False)
        self.assertEqual((results, ignored), ([], 1))
        results, ignored = cpl.analyse([host], 0, True)
        self.assertEqual(len(results), 1)

    def test_causes(self):
        cause = lambda **v: cpl.local_cause({**{n: 0 for n in COLUMNS}, "frameMs": 100, "speed": 1, "target": 1, **v})[0]
        self.assertEqual(cause(saving=1, saveMs=90), "save")
        self.assertEqual(cause(gcDelta=1, gameMs=90), "GC")
        self.assertEqual(cause(detailMs=50, logMs=20), "detailed logging")
        self.assertEqual(cause(serMs=30, sendMs=30), "sync work")
        self.assertEqual(cause(gameMs=90, ticks=1), "simulation")
        self.assertEqual(cause(gameMs=90, ticks=6, speed=7), "catch-up")
        self.assertEqual(cause(otherMs=90), "outside this mod")
        self.assertEqual(cause(gameMs=30, otherMs=30, uiMs=10), "mixed")


class Rhythm(unittest.TestCase):
    def rows(self, seconds, **values):
        return [{"type": "F", "frame": 1000 * i, "tick": 10 * i, "utcMs": s * 1000.0, "saving": 0, **values} for i, s in enumerate(seconds)]

    def test_a_regular_beat_is_found(self):
        found = cpl.rhythm(self.rows([10, 15, 20, 25, 30, 35]))
        self.assertTrue(found["regular"])
        self.assertAlmostEqual(found["period_s"], 5.0)
        self.assertAlmostEqual(found["period_ticks"], 10)

    def test_random_spacing_is_not_a_beat(self):
        self.assertFalse(cpl.rhythm(self.rows([1, 2, 9, 11, 30, 55, 56]))["regular"])

    def test_too_few_to_tell(self):
        self.assertIsNone(cpl.rhythm(self.rows([1, 2, 3])))

    def test_frames_close_together_are_one_episode(self):
        rows = [{"type": "F", "frame": f, "tick": f, "utcMs": f * 100.0, "saving": 0} for f in (1, 2, 3, 500, 501, 1000, 1001, 1500, 1502)]
        self.assertEqual(len(cpl.episodes(rows)), 4)

    def test_a_period_is_matched_to_what_exists(self):
        self.assertIn("1 Hz", cpl.explain_period(1.05))
        self.assertIn("2 times", cpl.explain_period(2.0))
        self.assertIn("10 Hz", cpl.explain_period(0.1))
        self.assertIn("garbage", cpl.explain_period(7.0, [(7.2, "garbage collection")]))
        self.assertIsNone(cpl.explain_period(7.3))


class Command(unittest.TestCase):
    def run_main(self, host_text, guest_text, *extra):
        with tempfile.TemporaryDirectory() as directory:
            paths = []
            for name, text in (("host.csv", host_text), ("guest.csv", guest_text)):
                path = os.path.join(directory, name)
                with open(path, "w", encoding="utf-8", newline="") as f:
                    f.write(text)
                paths.append(path)
            out = io.StringIO()
            code = cpl.main(paths + list(extra), out)
            return code, out.getvalue()

    def sessions(self, guest_mods=MODS):
        host = log_text("host", [line("S", 10, 100, frames=10, frameMs=12, maxFrameMs=40, utcMs=1000, heapMB=90),
                                 line("F", 11, 500, frameMs=300, gcDelta=1, gameMs=250, ticks=1, utcMs=2000)])
        guest = log_text("guest", [line("S", 10, 100, frames=10, frameMs=12, maxFrameMs=40, utcMs=1000, heapMB=90),
                                   line("W", 11, 501, waitMs=280, waiting=6, utcMs=2200)], mods=guest_mods)
        return host, guest

    def test_a_comparable_pair_gets_a_full_report(self):
        code, text = self.run_main(*self.sessions())
        self.assertEqual(code, 0)
        for heading in ("ARE THE TWO SESSIONS COMPARABLE", "WHAT IS NORMAL", "SLOW FRAMES AND LONG WAITS", "WHERE THE TIME WENT", "RHYTHM", "WHAT THIS POINTS TO"):
            self.assertIn(heading, text)
        self.assertIn("Yes: the same 3 mods", text)
        self.assertIn("a hitch on one player and a matching wait on the other", text)
        self.assertIn("Hash interval: this mod has none", text)

    def test_a_mismatch_stops_before_any_conclusion(self):
        code, text = self.run_main(*self.sessions(guest_mods=MODS[:2]))
        self.assertEqual(code, 2)
        self.assertIn("NO.", text)
        self.assertIn("modsettings", text)
        self.assertNotIn("WHAT IS NORMAL", text)

    def test_force_goes_on_anyway(self):
        code, text = self.run_main(*self.sessions(guest_mods=MODS[:2]), "--force")
        self.assertEqual(code, 0)
        self.assertIn("NO.", text)
        self.assertIn("WHAT IS NORMAL", text)

    def test_one_log_alone_can_be_read(self):
        host, _ = self.sessions()
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "host.csv")
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(host)
            out = io.StringIO()
            self.assertEqual(cpl.main([path], out), 0)
            self.assertIn("one log only", out.getvalue())

    def test_a_file_that_is_not_a_log_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "notes.txt")
            with open(path, "w", encoding="utf-8") as f:
                f.write("# just a comment\n")
            out = io.StringIO()
            self.assertEqual(cpl.main([path], out), 1)

    def test_the_report_is_plain_ascii(self):
        code, text = self.run_main(*self.sessions())
        text.encode("ascii")


import perf_details as pd  # noqa: E402


def v2_log(role="host", rows=(), extra=(), **kwargs):
    text = log_text(role, list(rows), extra=list(extra), **kwargs)
    return parse(text.replace("format 1", "format 2"), role + ".csv")


def summary(frames=100, ticks=10, **values):
    return line("S", 100, 1000, frames=frames, ticks=ticks, frameMs=values.pop("frameMs", 20), utcMs=1000000, **values)


class DetailSections(unittest.TestCase):
    def test_the_frame_histogram_names_each_bucket_by_its_edges(self):
        edges = "4,6,8.5,11.5,14,17.5,21,25,30,35,42,50,75,100,200,400"
        log = v2_log(rows=[summary(fh5=60, fh9=30, fh12=10)], extra=["# histogram|frameEdgesMs|" + edges])
        lines = pd.histogram(log)
        self.assertIn("100 frames", lines[0])
        text = "\n".join(lines)
        self.assertIn("14.0 to 17.5 ms", text)
        self.assertIn("60.0%", text)
        self.assertIn("30.0 to 35.0 ms", text)
        self.assertIn("50.0 to 75.0 ms", text)

    def test_ticks_per_frame(self):
        log = v2_log(rows=[summary(th0=50, th1=40, th5=10)])
        self.assertIn("0: 50.0%", pd.ticks_per_frame(log)[0])
        self.assertIn("10+: 10.0%", pd.ticks_per_frame(log)[0])

    def test_unitys_phases_and_the_share_of_the_frame_each_takes(self):
        log = v2_log(rows=[summary(frameMs=30, plUpdate=6, plPost=18)])
        text = "\n".join(pd.phases(log))
        self.assertIn("plPost", text)
        self.assertIn("60.0%", text)
        self.assertIn("outside every phase: 6.00 ms", text)

    def test_a_phase_timing_that_produced_nothing_says_so(self):
        self.assertIn("produced nothing", pd.phases(v2_log(rows=[summary()]))[0])

    def test_busy_or_waiting(self):
        log = v2_log(rows=[summary(frameMs=30, mainCpuMs=12, procCpuMs=45, ftMain=11, ftRender=8, ftGpu=25, ftWait=14)])
        text = "\n".join(pd.cpu(log))
        self.assertIn("40% busy", text)
        self.assertIn("1.5 cores", text)
        self.assertIn("graphics card 25.0 ms", text)
        self.assertIn("waiting to present 14.0 ms", text)

    def test_allocation_per_tick_by_section_with_the_biggest_first(self):
        log = v2_log(rows=[summary(ticks=10, gameKB=6000, serKB=1000, otherKB=500, allocKB=8000)])
        lines = pd.allocation(log)
        self.assertIn("750 KB/tick", lines[0])
        self.assertTrue(lines[1].lstrip().startswith("gameKB"))
        self.assertIn("600.0 KB/tick", lines[1])
        self.assertIn("80.0%", lines[1])

    def test_counters_and_the_entity_pass(self):
        log = v2_log(rows=[summary(frames=100, ticks=10, entities=20000, movers=4000, entTicks=20000, nAnim=50000,
                                   tebMs=6, tebLookMs=1, tebAnimMs=2, animMs=0.5, parTickMs=25, parMs=3)])
        text = "\n".join(pd.counters(log) + pd.entity_pass(log))
        self.assertIn("entities 2000", text)
        self.assertIn("nAnim 500", text)
        self.assertIn("2.50 ms per tick", text)
        self.assertIn("for 2000 entities of which 400 walk", text)
        self.assertIn("per call", text)

    def test_the_log_reports_what_it_costs_itself(self):
        self.assertIn("0.15%", pd.overhead(v2_log(rows=[summary(frameMs=20, overheadUs=20, probeUs=10)]))[0])

    def test_the_two_computers_are_compared_setting_by_setting(self):
        a = v2_log("host", extra=["# gc: incremental=True", "# bootconfig|gc-max-time-slice=3", "# bootconfig|vr=0", "# cmdline|Timberborn.exe"])
        b = v2_log("guest", extra=["# gc: incremental=False", "# bootconfig|vr=0", "# cmdline|Timberborn.exe", "# cmdline|-force-d3d11"])
        text = "\n".join(pd.environment([a, b]))
        self.assertIn("gc differs", text)
        self.assertIn("only host     gc-max-time-slice=3", text)
        self.assertIn("only guest    -force-d3d11", text)
        self.assertNotIn("boot.config: identical", text)

    def test_heap_at_each_stage_of_loading(self):
        log = v2_log(extra=["# milestone|mod-started|00:00:01.000|140|900", "# milestone|session-start|00:02:10.000|1650|4100"])
        self.assertIn("session-start 1650 MB / 4100 MB", pd.milestones(log)[0])

    def test_the_experiment_is_judged_by_the_pauses_either_side_of_it(self):
        rows = [line("F", 1, 100, frameMs=600, gcDelta=1), line("F", 2, 150, frameMs=700, gcDelta=1),
                line("F", 3, 300, frameMs=120, gcDelta=1), line("F", 4, 400, frameMs=110, gcDelta=1),
                line("F", 5, 500, frameMs=900, gcDelta=1, saving=1)]
        log = v2_log("guest", rows=rows, extra=["# event|tick 200|gc-experiment|before|incremental=False", "# event|tick 200|gc-experiment|result|worked"])
        text = "\n".join(pd.events(log))
        self.assertIn("2 before tick 200 (median 650 ms", text)
        self.assertIn("2 after (median 115 ms", text)


class ProfileFile(unittest.TestCase):
    HEADER = "type,window,tick,id,calls,sampled,ms,allocKB,maxMs"

    def profile(self, rows):
        text = "\n".join(["# BeaverBuddies frame rate log profile, format 1", "# role: host",
                          "# name|E|0|BeaverAdult|", "# name|E|1|FarmHouse|", "# name|G|2|Some.Mod.Singleton|SomeMod", self.HEADER] + rows) + "\n# end\n"
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "host-profile.csv")
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(text)
            return pd.parse_profile(path)

    def test_names_and_rows_are_read(self):
        profile = self.profile(["E,0,100,0,400,25,50.00,800.0,3.50", "G,0,100,2,10,10,9.00,90.0,1.20"])
        self.assertEqual(profile.names[2], ("G", "Some.Mod.Singleton", "SomeMod"))
        self.assertEqual(len(profile.rows), 2)
        self.assertEqual(profile.rows[0]["type"], "E")
        self.assertAlmostEqual(profile.rows[0]["ms"], 50.0)

    def test_windows_add_up_and_the_biggest_come_first(self):
        profile = self.profile(["E,0,100,0,400,25,50.00,800.0,3.50", "E,1,200,0,400,25,70.00,1200.0,9.00",
                                "E,0,100,1,100,6,10.00,100.0,2.00", "G,0,100,2,10,10,9.00,90.0,1.20"])
        totals = pd.profile_totals(profile)
        self.assertAlmostEqual(totals[("E", 0)]["ms"], 120.0)
        self.assertAlmostEqual(totals[("E", 0)]["max"], 9.0)
        log = v2_log(rows=[summary(frames=10, ticks=20)])
        lines = pd.profile_lines(log, profile)
        entities = [l for l in lines if "BeaverAdult" in l or "FarmHouse" in l]
        self.assertTrue("BeaverAdult" in entities[0])
        self.assertIn("6.00 ms/tick", entities[0])
        self.assertIn("[SomeMod]", "\n".join(lines))

    def test_a_profile_with_no_singleton_rows_says_the_patch_may_not_have_run(self):
        profile = self.profile(["E,0,100,0,400,25,50.00,800.0,3.50"])
        self.assertIn("no singletons rows", "\n".join(pd.profile_lines(v2_log(rows=[summary(ticks=20)]), profile)))

    def test_the_profile_file_is_found_beside_its_log(self):
        self.assertEqual(pd.profile_path(os.path.join("a", "perf-host-X-1.csv")), os.path.join("a", "perf-host-X-1-profile.csv"))

    def test_the_whole_report_includes_the_new_sections_and_survives_old_logs(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = []
            for role in ("host", "guest"):
                text = log_text(role, [summary(frames=100, ticks=10, plPost=9, gameKB=5000, fh5=100)], extra=["# gc: incremental=%s" % (role == "host")]).replace("format 1", "format 2")
                path = os.path.join(directory, "perf-%s-X-1.csv" % role)
                with open(path, "w", encoding="utf-8", newline="") as f:
                    f.write(text)
                paths.append(path)
            with open(pd.profile_path(paths[0]), "w", encoding="utf-8", newline="") as f:
                f.write("# name|E|0|BeaverAdult|\n" + self.HEADER + "\nE,0,100,0,400,25,50.00,800.0,3.50\n")
            out = io.StringIO()
            self.assertEqual(cpl.main(paths, out), 0)
            text = out.getvalue()
            for heading in ("HOW LONG FRAMES TAKE", "UNITY'S PHASES OF A FRAME", "WHAT ALLOCATES", "WHERE THE TICK GOES", "HOW THE TWO COMPUTERS DIFFER"):
                self.assertIn(heading, text)
            self.assertIn("BeaverAdult", text)
            text.encode("ascii")


if __name__ == "__main__":
    unittest.main()

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
COLUMNS_SOURCE = os.path.join(HERE, "..", "TimberNet", "Perf", "PerfColumns.cs")


def csharp_columns():
    """The column names as the game writes them, read from the C# source so the two cannot drift apart."""
    with open(COLUMNS_SOURCE, encoding="utf-8") as f:
        text = f.read()
    block = text[text.index("Names ="):]
    block = block[:block.index("};")]
    return re.findall(r'"([A-Za-z]+)"', block)


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
        slots = COLUMNS[COLUMNS.index("gameMs"):COLUMNS.index("saveMs") + 1]
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


if __name__ == "__main__":
    unittest.main()

// Every picture (NAV, Ship Systems, Damage Control): C# lookouts report, one model decides, Warden checks. C# 5, framework assemblies only.
//
// Lookouts (agents.json "behavior": "lookout") are C#, not model calls. Every tick each one scans the picture and
// raises at most one cue: a plain-English report ("TN0012 will pass 0.4 nautical miles from us...") with 2-4
// numbered options, each a ready-made order with its predicted result (worked out here, instantly, with the same
// math as the calculator tools). Option a is the doctrine pick.
//
// The decider (agents.json "tier": "decider") is the one model call for the COP. It is asked only when the set of
// open cues changes, reads a short prompt (glossary + cues + options) and answers with option letters ("1a 2c").
// The chosen orders become intents in the LOOKOUT's name (it holds the tools and limits), carrying the decider's
// call provenance, and go through Warden like any other intent.
//
// Deadline: an urgent cue (weapon inbound, torpedo, collision inside 10 min) not answered within reflex_deadline_s
// gets option a as a REFLEX, marked as such in its provenance and ledger. Warden still checks it, and the policy
// "reflex_fallback": false turns it off. A late model answer that differs from the reflex still goes to Warden.
//
// Threads: the decider's model call runs on its own thread. _lock guards the decider's open/answer state; status is
// locked like every other agent's.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace BridgeWarden
{
    public sealed class NavOption
    {
        public string text;                                    // "Turn right to 120 (30 degrees) -> passes 1.6 nm clear"
        public List<Dictionary<string, object>> actions = new List<Dictionary<string, object>>();   // {action, params}
    }

    public sealed class NavCue
    {
        public string key;          // lookout id + subject; a new key is what makes the decider think
        public string agent;        // the lookout that raised it and will carry out the order
        public int rank;            // 0 = most urgent; decides which cue may steer when two want the rudder
        public bool urgent;
        public string text;
        public List<NavOption> options = new List<NavOption>();
        public double raised_at;
    }

    // ------------------------------------------------------------------ lookouts
    public static class NavLookouts
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string H(double h) { return ((int)Py.Round(WorldMath.Wrap360(h)) % 360).ToString("000", Inv); }
        static string F1(double x) { return Py.F(x, 1); }
        static IEnumerable<Dictionary<string, object>> Rows(object o) { return Py.ListOrEmpty(o).Select(x => (Dictionary<string, object>)x); }
        static Dictionary<string, object> A(string action, params object[] kv) { return Py.D("action", action, "params", Py.D(kv)); }

        static NavOption Opt(string text, params Dictionary<string, object>[] acts)
        {
            NavOption o = new NavOption();
            o.text = text;
            o.actions.AddRange(acts);
            return o;
        }

        static string Side(double rel) { return rel >= 0 ? "right" : "left"; }

        // Heading that is `delta` degrees from the ordered heading.
        static double Turn(World w, double delta) { return WorldMath.Wrap360(w.ship.cmd_heading + delta); }

        static bool HeavyAhead(World w)
        {
            return w.WeatherAhead().Any(o =>
            {
                Dictionary<string, object> c = (Dictionary<string, object>)o;
                return Py.Num(c["intensity"]) >= 0.6 && Py.Num(c["range_to_edge_nm"]) <= 6 && Math.Abs(Py.Num(c["relative_bearing"])) <= 60;
            });
        }

        // Heavy weather within 6 nm ahead of a trial heading (the same test as HeavyAhead, which uses the ship's heading).
        static bool HeavyAheadOn(World w, double heading)
        {
            return w.WeatherAhead().Any(o =>
            {
                Dictionary<string, object> c = (Dictionary<string, object>)o;
                return Py.Num(c["intensity"]) >= 0.6 && Py.Num(c["range_to_edge_nm"]) <= 6 && Math.Abs(WorldMath.AngleDiff(Py.Num(c["bearing"]), heading)) <= 60;
            });
        }

        static double Clearance(World w, double heading)
        {
            if (w.env.cells.Count == 0) return 99;
            Dictionary<string, object> c = w.StormClearance(heading);
            return c.Count == 0 ? 99 : c.Values.Min(v => Py.Num(v));
        }

        // One cue per lookout (its most pressing subject), or null when it has nothing to report.
        public static NavCue Scan(LLMAgentRuntime rt, World w, Dictionary<string, object> sit, Dictionary<string, object> policies)
        {
            NavCue c = null;
            switch (rt.identity.role)
            {
                case "collision": c = Collision(w, policies); break;
                case "weather": c = Weather(w); break;
                case "navigation": c = Route(w, sit, policies); break;
                case "defense": c = Defense(w, sit); break;
                case "launch": c = Launch(w, sit); break;
                case "asw": c = Undersea(w, sit); break;
                case "power": c = PlantLookouts.Power(w); break;
                case "engine": c = PlantLookouts.EngineTemp(w, policies); break;
                case "energy": c = PlantLookouts.Energy(w); break;
                case "damage_control": c = PlantLookouts.DamageControl(w); break;
            }
            if (c != null)
            {
                c.agent = rt.identity.id;
                c.key = rt.identity.id + ":" + c.key;
                // The doctrine option is already what the ship is doing: nothing new to report (a re-ask would only
                // tempt the model into the leftover options, e.g. slowing down while still running from a torpedo).
                bool hadDoctrine = c.options.Count > 0 && c.options[0].actions.Count > 0;
                foreach (NavOption o in c.options) o.actions.RemoveAll(a => InEffect(w, a));
                if (hadDoctrine && c.options[0].actions.Count == 0) return null;
                c.options = c.options.Where(o => o.actions.Count > 0 || o.text.StartsWith("Hold", StringComparison.Ordinal)).ToList();
                if (c.options.Count(o => o.actions.Count > 0) == 0) return null;   // nothing left to order
            }
            return c;
        }

        // An order the ship is already under (Warden would block it as ALREADY_SET): never offer it.
        static bool InEffect(World w, Dictionary<string, object> a)
        {
            Dictionary<string, object> p = (Dictionary<string, object>)a["params"];
            switch (Py.Str(a["action"]))
            {
                case "set_heading": return Math.Abs(WorldMath.AngleDiff(Py.Num(p["heading"]), w.ship.cmd_heading)) < 1.0;
                case "set_speed": return Math.Abs(Py.Num(p["speed_kn"]) - w.ship.cmd_speed) < 0.5;
                case "set_gq": return Py.Truthy(p["on"]) == w.sys.gq;
                case "set_emcon": return Py.Truthy(p["on"]) == w.sys.emcon;
                case "activate_defense":
                    List<object> want = Py.ListOrEmpty(p["systems"]).Where(d => !w.sys.IsActive(Py.Str(d))).ToList();
                    p["systems"] = want;
                    return want.Count == 0;
            }
            return false;
        }

        static NavCue Collision(World w, Dictionary<string, object> policies)
        {
            double min = Py.Num(Py.Get(policies, "min_cpa_nm", 1.0));
            Contact k = w.Threats(min).Where(x => x.range_nm <= 20 && !x.hidden).OrderBy(x => x.tcpa_min).FirstOrDefault();
            if (k == null) return null;
            OwnShip s = w.ship;
            double rel = WorldMath.AngleDiff(k.bearing, s.heading);
            NavCue c = new NavCue();
            c.key = k.id;
            c.rank = 2;
            c.urgent = k.tcpa_min <= 10;
            // The Rules of the Road situation from where it bears: ahead (head-on), abaft the beam (overtaking us), or crossing.
            double arel = Math.Abs(rel);
            string rule = arel <= 10 ? "We are meeting head-on: both ships turn to starboard (right)."
                : arel > 112.5 ? "It is overtaking us from astern: it must keep clear, but it is not, so we act to avoid it."
                : rel > 0 ? "A ship crossing from our right has right of way: we give way, normally by turning right."
                : "A ship crossing from our left should give way to us, but it is not, so we act; a turn to port toward it is wrong.";
            c.text = string.Format(Inv, "Collision watch: {0} ({1}) bears {2}, {3} nm, on our {4} side. On our present course it passes only {5} nm " +
                "from us (closest approach) in {6} minutes; we need at least {7} nm. Rules of the Road: {8}", k.id, k.classification.ToLowerInvariant(),
                H(k.bearing), F1(k.range_nm), rel >= 0 ? "right (starboard)" : "left (port)", F1(k.cpa_nm), F1(k.tcpa_min), F1(min), rule);
            // Right turns first (the lawful give-way), then left, then slowing; best clearance of each kind.
            bool rightOffered = false;
            foreach (int sign in new[] { 1, -1 })
            {
                foreach (int d in new[] { 20, 30, 40 })
                {
                    double h = Turn(w, sign * d);
                    double cpa = w.PredictedMinCpa(h, null);
                    if (cpa >= min && Clearance(w, h) >= 0)
                    {
                        c.options.Add(Opt(string.Format(Inv, "Turn {0} {1} degrees to {2}: every ship then passes at least {3} nm clear.",
                            sign > 0 ? "right" : "left", d, H(h), F1(cpa)), A("set_heading", "heading", (int)Py.Round(h))));
                        if (sign > 0) rightOffered = true;
                        break;
                    }
                }
            }
            // No right turn clears on its own: still offer the best right turn (the Rules-of-the-Road move) with its honest
            // result, so the decider is never left with only options it reads as unlawful and answers in prose instead.
            if (!rightOffered)
            {
                double bestH = double.NaN, bestCpa = -1; int bestD = 0;
                foreach (int d in new[] { 20, 30, 40, 60 })
                {
                    double h = Turn(w, d);
                    if (Clearance(w, h) < 0) continue;
                    double cpa = w.PredictedMinCpa(h, null);
                    if (cpa > bestCpa) { bestCpa = cpa; bestH = h; bestD = d; }
                }
                if (!double.IsNaN(bestH) && bestCpa > k.cpa_nm)
                    c.options.Add(Opt(string.Format(Inv, "Turn right {0} degrees to {1} (best right turn available): closest approach becomes {2} nm, " +
                        "short of {3} nm; combine with a speed change if needed.", bestD, H(bestH), F1(bestCpa), F1(min)),
                        A("set_heading", "heading", (int)Py.Round(bestH))));
            }
            // Speed instead of course: the first trial speed (faster to pass ahead, slower or stopped to let it pass) that clears.
            foreach (double[] kind in new[] { new double[] { 20, 25 }, new double[] { 8, 4, 0 } })
            {
                foreach (double v in kind)
                {
                    if (Math.Abs(v - s.cmd_speed) < 2) continue;
                    double cpa = w.PredictedMinCpa(null, v);
                    if (cpa < min && v != 8) continue;           // 8 kn is always shown: the standard give-way slow-down
                    c.options.Add(Opt(string.Format(Inv, "{0} keep course: closest approach becomes {1} nm.",
                        v == 0 ? "Stop engines (0 knots)," : (v > s.cmd_speed ? "Speed up to " : "Slow to ") + v.ToString("0", Inv) + " knots,", F1(cpa)),
                        A("set_speed", "speed_kn", v)));
                    break;
                }
            }
            c.options.Add(Opt(string.Format(Inv, "Hold course and speed: passes {0} nm (too close).", F1(k.cpa_nm))));
            return c;
        }

        static NavCue Weather(World w)
        {
            if (!HeavyAhead(w) && w.env.wind_kts < 30)
            {
                // Restricted visibility (Rules of the Road, Rule 19): a safe speed for the conditions.
                if (w.env.visibility_nm >= 1 || w.ship.cmd_speed <= 10.5) return null;
                NavCue f = new NavCue();
                f.key = "visibility";
                f.rank = 3;
                f.text = string.Format(Inv, "Weather watch: restricted visibility, {0} nm (fog). We are ordered {1} knots. The Rules of the Road require a " +
                    "safe speed in restricted visibility, one that lets us stop within the distance we can see.", F1(w.env.visibility_nm), F1(w.ship.cmd_speed));
                f.options.Add(Opt("Slow to a safe speed of 10 knots until the fog lifts.", A("set_speed", "speed_kn", 10)));
                if (w.ship.cmd_speed > 8.5) f.options.Add(Opt("Slow to 8 knots (more margin to stop).", A("set_speed", "speed_kn", 8)));
                f.options.Add(Opt("Hold speed (not a safe speed in fog)."));
                return f;
            }
            NavCue c = new NavCue();
            c.key = "weather";
            c.rank = 3;
            double now = Clearance(w, w.ship.cmd_heading);
            c.text = string.Format(Inv, "Weather watch: heavy weather (a strong rain cell) is ahead within 6 nm; wind {0} knots. On our ordered heading " +
                "{1} we {2}.", F1(w.env.wind_kts), H(w.ship.cmd_heading), now < 0 ? "run into the cell" : "pass " + F1(now) + " nm from its edge");
            List<Tuple<double, double, int>> trial = new List<Tuple<double, double, int>>();
            foreach (int d in new[] { 20, -20, 30, -30, 40, -40 })
                trial.Add(Tuple.Create(Turn(w, d), Clearance(w, Turn(w, d)), d));
            foreach (Tuple<double, double, int> t in trial.Where(t => t.Item2 >= 1 && t.Item2 > now).OrderBy(t => Math.Abs(t.Item3)).Take(2))
                c.options.Add(Opt(string.Format(Inv, "Turn {0} {1} degrees to {2}: stays {3} nm clear of the heavy weather.",
                    t.Item3 > 0 ? "right" : "left", Math.Abs(t.Item3), H(t.Item1), F1(Math.Min(t.Item2, 30))), A("set_heading", "heading", (int)Py.Round(t.Item1))));
            if (w.ship.cmd_speed > 12)
                c.options.Add(Opt("Slow to 10 knots, keep course: an easier ride through the weather (less roll and slamming).", A("set_speed", "speed_kn", 10)));
            c.options.Add(Opt("Hold course and speed through it."));
            return c;
        }

        static NavCue Route(World w, Dictionary<string, object> sit, Dictionary<string, object> policies)
        {
            // Only when nothing threatens the ship; collision, weather and weapons outrank the schedule.
            // Not back toward heavy weather either: the return heading must be clear of heavy cells within 6 nm and the wind
            // under gale force (otherwise Weather Routing and Track Keeping fight over the rudder).
            if (w.Threats(Py.Num(Py.Get(policies, "min_cpa_nm", 1.0))).Any(x => x.range_nm <= 20) || HeavyAhead(w) || w.sys.ThreatsOnCop().Count > 0 || w.env.wind_kts >= 30)
                return null;
            OwnShip s = w.ship;
            double route = Py.Num(Py.Get(sit, "planned_route_heading", 90));
            double xt = s.y;
            double want = WorldMath.Wrap360(route + Math.Max(-25, Math.Min(25, xt * 40)));
            if (Math.Abs(xt) <= 0.2) want = route;
            double turn = WorldMath.AngleDiff(want, s.cmd_heading);
            turn = Math.Max(-30, Math.Min(30, turn));
            want = WorldMath.Wrap360(s.cmd_heading + turn);
            bool steer = Math.Abs(turn) >= 3, speed = Math.Abs(s.cmd_speed - 16) >= 0.5;
            if (!steer && !speed) return null;
            // Still giving way: turning back now would bring a ship inside the minimum again, or into heavy weather.
            double cpaBack = w.PredictedMinCpa(want, 16);
            if (cpaBack < Py.Num(Py.Get(policies, "min_cpa_nm", 1.0)) || Clearance(w, want) < 0 || HeavyAheadOn(w, want)) return null;
            NavCue c = new NavCue();
            c.key = "route";
            c.rank = 5;
            c.text = string.Format(Inv, "Navigation: nothing threatens the ship. We are {0} nm {1} of the planned track line (route heading {2}), ordered " +
                "heading {3} at {4} knots; the schedule speed is 16 knots.", F1(Math.Abs(xt)), xt >= 0 ? "north" : "south", H(route), H(s.cmd_heading), F1(s.cmd_speed));
            List<Dictionary<string, object>> back = new List<Dictionary<string, object>>();
            if (steer) back.Add(A("set_heading", "heading", (int)Py.Round(want)));
            if (speed) back.Add(A("set_speed", "speed_kn", 16));
            c.options.Add(Opt(string.Format(Inv, "Return to route: {0}{1}; every ship still passes at least {2} nm clear.", steer ? "steer " + H(want) : "",
                speed ? (steer ? " and " : "") + "resume 16 knots" : "", F1(Math.Min(cpaBack, 20))),
                back.ToArray()));
            if (steer && Math.Abs(s.cmd_speed - 20) >= 0.5 && w.PredictedMinCpa(want, 20) >= Py.Num(Py.Get(policies, "min_cpa_nm", 1.0)))
            {
                List<Dictionary<string, object>> fast = back.Where(a => Py.Str(a["action"]) != "set_speed").ToList();
                fast.Add(A("set_speed", "speed_kn", 20));
                c.options.Add(Opt("Return to route at 20 knots to make up the lost time: steer " + H(want) + ".", fast.ToArray()));
            }
            c.options.Add(Opt("Hold present course and speed for now."));
            return c;
        }

        static NavCue Defense(World w, Dictionary<string, object> sit)
        {
            ShipSystems y = w.sys;
            List<Dictionary<string, object>> inbound = Rows(Py.Get(sit, "inbound_threats")).OrderBy(m => Py.Num(m["range_nm"])).ToList();
            Dictionary<string, object> defs = Py.DictOrEmpty(Py.Get(sit, "defenses"));
            List<object> ready = defs.Where(kv => kv.Key != "NIXIE" && Py.Str(kv.Value) == "ready").Select(kv => (object)kv.Key).ToList();
            List<Dictionary<string, object>> hostileTracks = Rows(Py.Get(sit, "tracks"))
                .Where(t => Py.Str(t["classification"]) == "SUSPECT" || Py.Str(t["classification"]) == "HOSTILE").ToList();
            Dictionary<string, object> gq = y.gq ? null : A("set_gq", "on", true);
            NavCue c = new NavCue();
            if (inbound.Count > 0)
            {
                Dictionary<string, object> m = inbound[0];
                c.key = "inbound";
                c.rank = 0;
                c.urgent = true;
                c.text = string.Format(Inv, "Air defense: {0} anti-ship missile(s) inbound. Closest {1} bears {2}, {3} nm, about {4} seconds out. " +
                    "Ready defenses: {5}. General Quarters is {6}.", inbound.Count, m["id"], H(Py.Num(m["bearing"])), F1(Py.Num(m["range_nm"])),
                    (int)(Py.Num(m["range_nm"]) / Py.Num(Py.Get(m, "speed_kn", 550)) * 3600), ready.Count > 0 ? string.Join(", ", ready.Select(Py.Str).ToArray()) : "none",
                    y.gq ? "set" : "NOT set");
                List<Dictionary<string, object>> fire = inbound.Where(i => !Py.Truthy(i["sm2_in_flight"]))
                    .Select(i => A("engage_target", "target", i["id"], "rounds", 2)).ToList();
                List<Dictionary<string, object>> posture = new List<Dictionary<string, object>>(fire);
                if (ready.Count > 0) posture.Add(A("activate_defense", "systems", ready));
                if (gq != null) posture.Add(gq);
                Dictionary<string, object> ev = AgentTools.ToolEvasionHeadings(w, Py.D("threat_bearing", m["bearing"], "offset_deg", 45.0));
                Dictionary<string, object> best = Rows(ev["options"]).Where(o => o["min_weather_clearance_nm"] == null || Py.Num(o["min_weather_clearance_nm"]) >= 0)
                    .OrderBy(o => Math.Abs(Py.Num(o["turn_from_cmd_deg"]))).FirstOrDefault();
                if (best != null)
                {
                    List<Dictionary<string, object>> full = new List<Dictionary<string, object>>(posture);
                    full.Add(A("set_heading", "heading", best["heading"]));
                    full.Add(A("set_speed", "speed_kn", 30));
                    c.options.Add(Opt(string.Format(Inv, "Fight and evade: {0}{1}, turn hard to {2} at 30 knots (a hard turn at speed makes a leaker miss about 1 in 4).",
                        fire.Count > 0 ? "fire 2 SM-2 at each unengaged missile" : "missiles already engaged", ready.Count > 0 ? ", activate " + string.Join(", ", ready.Select(Py.Str).ToArray()) : "",
                        H(Py.Num(best["heading"]))), full.ToArray()));
                }
                c.options.Add(Opt("Fight, hold course: same weapons and defenses, no maneuver.", posture.ToArray()));
                c.options.Add(Opt("Soft-kill only: defenses and General Quarters, no missiles fired.",
                    posture.Where(a => Py.Str(a["action"]) != "engage_target").ToArray()));
                return c;
            }
            if (hostileTracks.Count == 0)
            {
                // All clear: stand down what the threat picture set (radar silence, battle stations).
                bool casualty = y.casualties.Values.Any(x => Py.Str(x["status"]) != "out" && Py.Str(x["status"]) != "repaired");
                bool sub = Rows(Py.Get(sit, "tracks")).Any(t => Py.Str(Py.Get(t, "domain")) == "subsurface" && Py.Str(t["classification"]) != "MERCHANT");
                List<Dictionary<string, object>> down = new List<Dictionary<string, object>>();
                if (y.emcon) down.Add(A("set_emcon", "on", false));
                if (y.gq && !casualty && !sub && y.torpedoes.Count == 0) down.Add(A("set_gq", "on", false));
                if (down.Count == 0) return null;
                c.key = "standdown";
                c.rank = 4;
                c.text = "Threat picture clear: no SUSPECT or HOSTILE track, no inbound weapon" + (casualty ? "" : ", no open casualty") + ". " +
                    (y.emcon ? "EMCON (radar silence) is still set. " : "") + (y.gq ? "General Quarters is still set." : "");
                c.options.Add(Opt("Stand down: " + string.Join(" and ", down.Select(a => Py.Str(a["action"]) == "set_emcon" ? "lift EMCON" : "secure from General Quarters").ToArray()) + ".",
                    down.ToArray()));
                c.options.Add(Opt("Hold the present readiness."));
                return c;
            }
            Dictionary<string, object> t0 = hostileTracks.OrderBy(t => Py.Num(t["range_nm"])).First();
            c.key = "hostile:" + Py.Str(t0["id"]) + ":" + Py.Str(t0["classification"]);    // SUSPECT -> HOSTILE asks again
            c.rank = 1;
            c.text = string.Format(Inv, "Surface/air picture: {0} is classified {1}, bears {2}, {3} nm. EMCON (radar silence) is {4}; General Quarters is {5}.",
                t0["id"], t0["classification"], H(Py.Num(t0["bearing"])), F1(Py.Num(t0["range_nm"])), y.emcon ? "set" : "off", y.gq ? "set" : "NOT set");
            List<Dictionary<string, object>> both = new List<Dictionary<string, object>>();
            if (!y.emcon) both.Add(A("set_emcon", "on", true));
            if (gq != null) both.Add(gq);
            if (Py.Str(t0["classification"]) == "HOSTILE" && Py.Num(t0["range_nm"]) <= 10 && ready.Count > 0)
                both.Add(A("activate_defense", "systems", ready));
            c.options.Add(Opt("Go quiet and man battle stations: " + (both.Count > 0 ? string.Join(", ", both.Select(a => Py.Str(a["action"])).ToArray()) : "already done") + ".",
                both.ToArray()));
            if (gq != null) c.options.Add(Opt("General Quarters only; keep radiating.", gq));
            c.options.Add(Opt("Hold: watch it, no change."));
            return c;
        }

        static NavCue Launch(World w, Dictionary<string, object> sit)
        {
            HashSet<string> inFlight = new HashSet<string>(Rows(Py.Get(sit, "own_weapons_in_flight")).Select(i => Py.Str(i["target"])));
            Dictionary<string, object> t = Rows(Py.Get(sit, "tracks"))
                .Where(x => Py.Str(x["classification"]) == "HOSTILE" && Py.Str(Py.Get(x, "domain", "surface")) == "surface" &&
                            Py.Num(x["range_nm"]) >= 1 && Py.Num(x["range_nm"]) <= 60 && !inFlight.Contains(Py.Str(x["id"])))
                .OrderBy(x => Py.Num(x["range_nm"])).FirstOrDefault();
            if (t == null) return null;
            NavCue c = new NavCue();
            c.key = "harpoon:" + Py.Str(t["id"]);
            c.rank = 1;
            c.text = string.Format(Inv, "Strike: {0} is HOSTILE (it has attacked or shown hostile intent), surface, {1} nm, inside Harpoon range (1-60 nm). " +
                "No weapon is in flight at it.", t["id"], F1(Py.Num(t["range_nm"])));
            c.options.Add(Opt("Return fire: 1 Harpoon at " + Py.Str(t["id"]) + " (about 70% kill chance; stops further launches).",
                A("engage_target", "target", t["id"], "rounds", 1)));
            c.options.Add(Opt("Return fire: 2 Harpoons (about 91% kill chance).", A("engage_target", "target", t["id"], "rounds", 2)));
            c.options.Add(Opt("Hold fire."));
            return c;
        }

        static NavCue Undersea(World w, Dictionary<string, object> sit)
        {
            ShipSystems y = w.sys;
            Dictionary<string, object> gq = y.gq ? null : A("set_gq", "on", true);
            Dictionary<string, object> nixie = y.nixie ? null : A("activate_defense", "systems", Py.L("NIXIE"));
            NavCue c = new NavCue();
            Dictionary<string, object> torp = y.torpedoes.OrderBy(t => Py.Num(t["range"])).FirstOrDefault();
            if (torp != null)
            {
                double away = WorldMath.Wrap360(Py.Num(torp["bearing"]) + 180);
                c.key = "torpedo";
                c.rank = 0;
                c.urgent = true;
                Func<double, Dictionary<string, object>> ev = v => AgentTools.ToolTorpedoEvasion(w, Py.D("torpedo", torp["id"], "own_heading", away, "own_speed", v));
                Dictionary<string, object> e25 = ev(25), e30 = ev(30);
                c.text = string.Format(Inv, "Undersea: TORPEDO {0} in the water, bears {1}, {2} nm. Running away means heading {3}. At 25 knots it {4}; at 30 knots " +
                    "it {5}. Nixie decoy is {6}.", torp["id"], H(Py.Num(torp["bearing"])), F1(Py.Num(torp["range"])), H(away),
                    Py.Truthy(e25["outruns_torpedo"]) ? "runs out of fuel before reaching us" : "reaches us", Py.Truthy(e30["outruns_torpedo"]) ? "runs out of fuel first" : "still reaches us",
                    y.nixie ? "streamed" : "NOT streamed");
                foreach (int v in new[] { 30, 25 })
                {
                    List<Dictionary<string, object>> l = new List<Dictionary<string, object>> { A("set_heading", "heading", (int)Py.Round(away)), A("set_speed", "speed_kn", v) };
                    if (nixie != null) l.Add(nixie);
                    if (gq != null) l.Add(gq);
                    c.options.Add(Opt(string.Format(Inv, "Run: turn to {0} at {1} knots{2}.", H(away), v, nixie != null ? ", stream Nixie" : ""), l.ToArray()));
                }
                List<Dictionary<string, object>> stay = new List<Dictionary<string, object>>();
                if (nixie != null) stay.Add(nixie);
                if (gq != null) stay.Add(gq);
                c.options.Add(Opt("Decoy only: stream Nixie, no maneuver.", stay.ToArray()));
                return c;
            }
            Dictionary<string, object> sub = Rows(Py.Get(sit, "tracks"))
                .Where(t => Py.Str(Py.Get(t, "domain")) == "subsurface" && Py.Str(t["classification"]) != "MERCHANT").OrderBy(t => Py.Num(t["range_nm"])).FirstOrDefault();
            if (sub == null) return null;
            HashSet<string> inFlight = new HashSet<string>(Rows(Py.Get(sit, "own_weapons_in_flight")).Select(i => Py.Str(i["target"])));
            bool hostile = Py.Str(sub["classification"]) == "HOSTILE";
            double r = Py.Num(sub["range_nm"]);
            c.key = "sub:" + Py.Str(sub["id"]);
            c.rank = 1;
            c.text = string.Format(Inv, "Undersea: submarine {0} ({1}) bears {2}, {3} nm. Nixie decoy is {4}; General Quarters is {5}.",
                sub["id"], sub["classification"], H(Py.Num(sub["bearing"])), F1(r), y.nixie ? "streamed" : "NOT streamed", y.gq ? "set" : "NOT set");
            List<Dictionary<string, object>> prep = new List<Dictionary<string, object>>();
            if (nixie != null) prep.Add(nixie);
            if (gq != null) prep.Add(gq);
            if (hostile && r >= 1 && r <= 10 && !inFlight.Contains(Py.Str(sub["id"])))
            {
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>(prep);
                l.Add(A("engage_target", "target", sub["id"], "rounds", 1));
                c.options.Add(Opt("Prepare and attack: " + (prep.Count > 0 ? "stream Nixie / GQ, " : "") + "1 ASROC at " + Py.Str(sub["id"]) + " (about 55% kill chance).", l.ToArray()));
            }
            c.options.Add(Opt("Prepare only: stream Nixie and set General Quarters; do not fire.", prep.ToArray()));
            c.options.Add(Opt("Hold: track it, no change."));
            return c;
        }
    }

    // ------------------------------------------------------------------ Ship Systems and Damage Control lookouts
    public static class PlantLookouts
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] VITAL_FIRST = { "STEER", "SHAFT-1", "SHAFT-2", "CHW", "RADAR", "CIWS", "ADS", "FP-1", "FP-2", "FP-3", "ECM", "CMD", "COMMS" };
        static Dictionary<string, object> A(string action, params object[] kv) { return Py.D("action", action, "params", Py.D(kv)); }
        static bool B(Dictionary<string, object> d, string k) { return Py.Truthy(Py.Get(d, k)); }

        static NavOption Opt(string text, params Dictionary<string, object>[] acts)
        {
            NavOption o = new NavOption();
            o.text = text;
            o.actions.AddRange(acts);
            return o;
        }

        static string Label(ShipSystems y, string n) { return Py.Str(y.nodes[n]["label"]) + " (" + n + ")"; }
        static double Mid(string comp) { List<object> fr = Py.List(ShipSystems.COMP[comp]["fr"]); return (Py.Num(fr[0]) + Py.Num(fr[1])) / 2; }

        // Teams that can take a job now, nearest first: (id, seconds to get there).
        static List<Tuple<string, int>> Teams(ShipSystems y, string comp)
        {
            double mid = Mid(comp);
            return y.teams.Values.Where(t => Py.Str(t["status"]) == "standby" || Py.Str(t["status"]) == "returning")
                .Select(t => Tuple.Create(Py.Str(t["id"]), (int)Py.Round(Math.Abs(Py.Num(t["pos"]) - mid) / ShipSystems.TEAM_FRAMES_PER_S)))
                .OrderBy(t => t.Item2).ToList();
        }

        static string TeamName(ShipSystems y, string id) { return Py.Str(y.teams[id]["name"]); }

        // Electrical plant: dead equipment first (vital first), then an overloaded generator, then back to the normal lineup.
        public static NavCue Power(World w)
        {
            ShipSystems y = w.sys;
            NavCue ov = Overload(w);                 // an overloaded generator trips and drops everything: first
            if (ov != null) return ov;
            List<string> dead = y.UnpoweredVital().Where(n => !B(y.nodes[n], "maint") && !y.breakers.ContainsKey("CP-" + n))
                .OrderBy(n => { int i = Array.IndexOf(VITAL_FIRST, n); return i < 0 ? 99 : i; }).ToList();
            NavCue c = new NavCue();
            if (dead.Count > 0)
            {
                string eq = dead[0];
                c.key = "dead:" + eq;
                c.rank = Array.IndexOf(VITAL_FIRST, eq) >= 0 && Array.IndexOf(VITAL_FIRST, eq) < 3 ? 0 : 1;
                c.urgent = c.rank == 0 || (y.ThreatsOnCop().Count > 0 && (eq == "CIWS" || eq == "ADS" || eq == "RADAR" || eq == "CHW"));
                Dictionary<string, object> r = AgentTools.ToolPowerRoutes(w, Py.D("equipment", eq));
                List<Dictionary<string, object>> routes = Py.ListOrEmpty(Py.Get(r, "options")).Select(o => (Dictionary<string, object>)o).ToList();
                c.text = string.Format(Inv, "Electrical plant: {0} in {1} has NO POWER (it is not damaged; its feed is lost{2}).{3} Generator load now: {4}.",
                    Label(y, eq), y.nodes[eq]["comp"], Py.Truthy(Py.Get(y.nodes[eq], "fail_reason")) ? "" : "",
                    dead.Count > 1 ? " Also without power: " + string.Join(", ", dead.Skip(1).Take(5).ToArray()) + "." : "",
                    y.gen_load.Count > 0 ? string.Join(", ", y.gen_load.Select(kv => kv.Key + " " + kv.Value + "%").ToArray()) : "no generator on line");
                foreach (Dictionary<string, object> o in routes.Where(o => !B(o, "overload")).Take(2))
                {
                    List<object> close = Py.ListOrEmpty(o["close"]);
                    string load = string.Join(", ", Py.DictOrEmpty(o["generator_load_pct"]).Select(kv => kv.Key + " " + Py.Str(kv.Value) + "%").ToArray());
                    c.options.Add(Opt(string.Format(Inv, "Re-route: close {0}. Restores {1}; generator load becomes {2}.", string.Join(" + ", close.Select(Py.Str).ToArray()),
                        string.Join(", ", Py.ListOrEmpty(o["restores"]).Select(Py.Str).ToArray()), load),
                        close.Select(b => A("close_breaker", "breaker", b)).ToArray()));
                }
                if (c.options.Count == 0 && routes.Count > 0 && !B(y.breakers["B-G3"], "closed") && !B(y.nodes["SSGTG-3"], "failed"))
                    c.options.Add(Opt("Bring the standby generator SSGTG-3 on line (close B-G3) first: every re-route now overloads a generator.",
                        A("close_breaker", "breaker", "B-G3")));
                // Patch cable: a team rigs casualty power from a live panel. Nearest panel whose generator stays at or under
                // 100% with the extra load (a cable that overloads a generator trades one casualty for a bigger one).
                string comp = Py.Str(y.nodes[eq]["comp"]);
                Tuple<string, Dictionary<string, int>> pick = y.nodes
                    .Where(kv => (Py.Str(kv.Value["type"]) == "lc" || Py.Str(kv.Value["type"]) == "swbd") && B(kv.Value, "powered") && !B(kv.Value, "failed") && !B(kv.Value, "maint"))
                    .OrderBy(kv => Math.Abs(Mid(Py.Str(kv.Value["comp"])) - Mid(comp)))
                    .Select(kv => Tuple.Create(kv.Key, LoadWithCable(y, kv.Key, eq)))
                    .FirstOrDefault(t => t.Item2.Values.All(v => v <= 100));
                string src = pick == null ? null : pick.Item1;
                List<Tuple<string, int>> teams = Teams(y, comp);
                if (src != null && teams.Count > 0)
                    c.options.Add(Opt(string.Format(Inv, "Patch cable: send {0} to rig a casualty power cable {1} -> {2} (about {3} s to get there + {4} s to rig); " +
                        "generator load then {5}.", TeamName(y, teams[0].Item1), src, eq, teams[0].Item2,
                        (int)ShipSystems.RIG_S, string.Join(", ", pick.Item2.Select(kv => kv.Key + " " + kv.Value + "%").ToArray())),
                        A("rig_casualty_power", "team", teams[0].Item1, "equipment", eq, "source", src)));
                else if (src != null && !y.gq)
                    c.text += " No repair team is manned for a patch cable (General Quarters is not set).";
                if (!c.options.Any(o => o.actions.Any(a => Py.Str(a["action"]) == "close_breaker")) && c.options.Count > 0)
                    c.text += " NO breaker re-route can restore it: a patch cable is the only way to get it back now.";
                c.options.Add(Opt("Hold: leave it without power for now."));
                return c;
            }
            Dictionary<string, object> normal = AgentTools.NormalLineup(w);
            if (normal != null && B(normal, "safe") && !y.nodes.Values.Any(n => B(n, "failed") || B(n, "maint")))
            {
                List<object> close = Py.ListOrEmpty(normal["close"]), open = Py.ListOrEmpty(normal["open"]);
                c.key = "normal";
                c.rank = 4;
                c.text = "Electrical plant: repairs are complete and the plant is still on its emergency lineup (" +
                         string.Join(", ", close.Concat(open).Select(Py.Str).ToArray()) + " off normal). The normal lineup powers everything with no overload.";
                List<Dictionary<string, object>> acts = close.Select(b => A("close_breaker", "breaker", b)).Concat(open.Select(b => A("open_breaker", "breaker", b))).ToList();
                c.options.Add(Opt("Return to the normal lineup: close " + (close.Count > 0 ? string.Join(", ", close.Select(Py.Str).ToArray()) : "nothing") +
                    ", then open " + (open.Count > 0 ? string.Join(", ", open.Select(Py.Str).ToArray()) : "nothing") + " (casualty power cables come off).", acts.ToArray()));
                c.options.Add(Opt("Hold the emergency lineup for now."));
                return c;
            }
            return null;
        }

        // Generator loads if a casualty power cable ran from `src` to `eq` (a what-if on a copy of the plant).
        static Dictionary<string, int> LoadWithCable(ShipSystems y, string src, string eq)
        {
            ShipSystems sim = new ShipSystems();
            sim.nodes = y.nodes.ToDictionary(kv => kv.Key, kv => (Dictionary<string, object>)Py.DeepCopy(kv.Value));
            sim.breakers = y.breakers.ToDictionary(kv => kv.Key, kv => (Dictionary<string, object>)Py.DeepCopy(kv.Value));
            sim.breakers["CP-trial"] = Py.D("id", "CP-trial", "a", src, "b", eq, "closed", true, "normal", false);
            sim.Flow();
            return sim.gen_load;
        }

        // An overloaded generator: each single breaker (standby generator on line, or a bus tie that shares the load with
        // an under-used generator) that leaves every generator at or under 100% without dropping anything.
        static NavCue Overload(World w)
        {
            ShipSystems y = w.sys;
            List<string> over = y.Overloaded();
            if (over.Count == 0) return null;
            HashSet<string> deadNow = new HashSet<string>(y.nodes.Where(kv => !B(kv.Value, "powered") && !B(kv.Value, "failed")).Select(kv => kv.Key));
            NavCue c = new NavCue();
            c.key = "overload";
            c.rank = 0;
            c.urgent = true;
            c.text = "Electrical plant: " + string.Join(", ", over.ToArray()) + " OVERLOADED (" + string.Join(", ", y.gen_load.Select(kv => kv.Key + " " + kv.Value + "%").ToArray()) +
                     "). An overloaded generator trips and drops everything it feeds.";
            foreach (string b in new[] { "B-G3", "B-T12", "B-T23", "B-T13", "B-G1", "B-G2" })
            {
                if (!y.breakers.ContainsKey(b) || B(y.breakers[b], "closed")) continue;
                Dictionary<string, object> r;
                try { r = AgentTools.ToolPowerLineup(w, Py.D("close", Py.L(b))); } catch (Exception) { continue; }
                Dictionary<string, object> load = Py.DictOrEmpty(r["generator_load_pct"]);
                bool loses = Py.ListOrEmpty(r["unpowered"]).Any(x => !deadNow.Contains(Py.Str(x)));
                if (loses || load.Values.Any(v => Py.Num(v) > 100)) continue;
                string what = b.StartsWith("B-G", StringComparison.Ordinal) ? "bring generator " + y.breakers[b]["a"] + " on line" : "tie " + y.breakers[b]["a"] + " to " + y.breakers[b]["b"] + " to share the load";
                c.options.Add(Opt("Close " + b + " (" + what + "): loads become " + string.Join(", ", load.Select(kv => kv.Key + " " + Py.Str(kv.Value) + "%").ToArray()) + ".",
                    A("close_breaker", "breaker", b)));
                if (c.options.Count >= 3) break;
            }
            if (c.options.Count == 0) return null;   // nothing a single breaker can fix: the dead-equipment cue still offers re-routes
            c.options.Add(Opt("Hold (risk a generator trip)."));
            return c;
        }

        public static NavCue EngineTemp(World w, Dictionary<string, object> policies)
        {
            OwnShip s = w.ship;
            double warn = Py.Num(Py.Get(policies, "engine_temp_warn_c", 95));
            if (s.engine_temp <= warn) return null;
            NavCue c = new NavCue();
            c.key = "engine-temp";
            c.rank = 1;
            c.urgent = s.engine_temp >= Py.Num(Py.Get(policies, "engine_temp_crit_c", 105));
            c.text = string.Format(Inv, "Main engines: temperature {0} C (warning above {1} C, damage above {2} C) at {3} knots. At a steady speed the engines " +
                "settle at about 62 + 1.6 x speed C plus the fault heat ({4} C now).", Py.F(s.engine_temp, 0), Py.F(warn, 0),
                Py.F(Py.Num(Py.Get(policies, "engine_temp_crit_c", 105)), 0), Py.F(s.speed, 0), Py.F(s.fault_heat, 0));
            bool attack = w.sys.ThreatsOnCop().Count > 0;
            // The fastest speed that settles the engine at or under the warning limit (stopped if the fault heat alone is too much),
            // then a middle step that only keeps it under the damage limit.
            double crit = Py.Num(Py.Get(policies, "engine_temp_crit_c", 105));
            double safe = Math.Max(0, Math.Floor((warn - 62 - s.fault_heat) / 1.6));
            double hold = Math.Max(0, Math.Floor((crit - 1 - 62 - s.fault_heat) / 1.6));
            if (s.cmd_speed > safe + 0.5)
                c.options.Add(Opt(string.Format(Inv, "{0}: engines settle near {1} C, under the warning limit.", safe == 0 ? "Stop the engines (0 knots)" : "Slow to " + safe + " knots",
                    Py.F(62 + 1.6 * safe + s.fault_heat, 0)), A("set_speed", "speed_kn", safe)));
            if (hold > safe && s.cmd_speed > hold + 0.5)
                c.options.Add(Opt(string.Format(Inv, "Slow to {0} knots: engines settle near {1} C (hot, but under the damage limit; keeps some speed).", hold,
                    Py.F(62 + 1.6 * hold + s.fault_heat, 0)), A("set_speed", "speed_kn", hold)));
            c.options.Add(Opt("Hold speed" + (attack ? " (the ship is under attack: speed may matter more than the engine)." : ".")));
            return c;
        }

        public static NavCue Energy(World w)
        {
            OwnShip s = w.ship;
            if (s.cmd_speed <= 20.5 || w.sys.ThreatsOnCop().Count > 0 || w.contacts.Values.Any(x => !x.hidden && (x.classification == "SUSPECT" || x.classification == "HOSTILE")))
                return null;
            NavCue c = new NavCue();
            c.key = "fuel";
            c.rank = 5;
            c.text = string.Format(Inv, "Fuel: ordered {0} knots with no threat on any picture. Fuel burn now {1} units/h; at 16 knots it would be {2}.",
                Py.F(s.cmd_speed, 0), Py.F(0.02 * Math.Pow(s.speed, 2.2), 1), Py.F(0.02 * Math.Pow(16, 2.2), 1));
            c.options.Add(Opt("Slow to the 16-knot schedule speed to save fuel.", A("set_speed", "speed_kn", 16)));
            c.options.Add(Opt("Hold speed."));
            return c;
        }

        // Damage control: the worst casualty without a team. No team manned: General Quarters first.
        public static NavCue DamageControl(World w)
        {
            ShipSystems y = w.sys;
            Dictionary<string, object> cas = y.casualties.Values
                .Where(x => Py.Str(x["status"]) != "out" && Py.Str(x["status"]) != "repaired" && x["team"] == null)
                .OrderByDescending(x => Py.Str(x["kind"]) == "fire" || Py.Str(x["kind"]) == "flood" ? 1 : 0).ThenByDescending(x => Py.Num(x["severity"])).FirstOrDefault();
            if (cas == null) return null;
            string comp = Py.Str(cas["comp"]), kind = Py.Str(cas["kind"]);
            List<object> fr = Py.List(ShipSystems.COMP[comp]["fr"]);
            List<string> there = y.nodes.Where(kv => Py.Str(kv.Value["comp"]) == comp).Select(kv => kv.Key).ToList();
            NavCue c = new NavCue();
            c.key = "cas:" + Py.Str(cas["id"]);
            c.rank = kind == "fire" || kind == "flood" ? 0 : 2;
            c.urgent = (kind == "fire" || kind == "flood") && Py.Num(cas["severity"]) >= 0.3;
            c.text = string.Format(Inv, "Damage control: {0} {1} in {2} ({3}), {4}, severity {5}%{6}. Equipment there: {7}. No repair team assigned.{8}",
                Py.Str(cas["id"]), kind.ToUpperInvariant(), comp, ShipSystems.COMP[comp]["name"], Py.Str(cas["status"]), (int)(Py.Num(cas["severity"]) * 100),
                kind == "fire" || kind == "flood" ? " and growing (it spreads to the next compartment at 100% unless a team sets boundaries)" : "",
                there.Count > 0 ? string.Join(", ", there.ToArray()) : "none", y.FiremainOk() ? "" : " Firemain is DOWN (no fire pump): firefighting is slow.");
            List<Tuple<string, int>> teams = Teams(y, comp);
            if (teams.Count == 0)
            {
                if (y.gq) return null;                 // teams are manning up or busy: nothing to order yet
                c.options.Add(Opt("Sound General Quarters: mans every repair team (off-watch teams need up to a minute).", A("set_gq", "on", true)));
                c.options.Add(Opt("Hold."));
                return c;
            }
            // A casualty is a reason for General Quarters (it mans the other teams too): ordered with the team.
            foreach (Tuple<string, int> t in teams.Take(2))
            {
                List<Dictionary<string, object>> acts = new List<Dictionary<string, object>> {
                    A("dispatch_team", "team", t.Item1, "casualty", cas["id"], "compartment", comp, "boundaries", Py.L(fr[0], fr[1])) };
                if (!y.gq) acts.Add(A("set_gq", "on", true));
                c.options.Add(Opt(string.Format(Inv, "Send {0}: about {1} s to get there; it sets boundaries at frames {2} and {3}{4}.", TeamName(y, t.Item1), t.Item2,
                    fr[0], fr[1], y.gq ? "" : ". Also sound General Quarters to man the other teams"), acts.ToArray()));
            }
            c.options.Add(Opt("Hold the teams."));
            return c;
        }
    }

    // ------------------------------------------------------------------ the decider
    public sealed class NavDecider
    {
        public static double ReflexDeadlineS = 6.0;
        public static double OutageUntil;          // injected model outage (App "llm-outage"): calls fail until then

        readonly LLMAgentRuntime _rt;
        readonly object _lock = new object();
        List<NavCue> _asked;                       // the cues the running (or last) call was about
        string _askedSig = "";
        double _askedAt;
        volatile bool _busy;
        readonly HashSet<string> _decided = new HashSet<string>();      // cue key -> answered (by model or reflex)
        readonly Dictionary<string, string> _reflexPick = new Dictionary<string, string>();
        readonly Dictionary<string, double> _decidedAt = new Dictionary<string, double>();
        List<Intent> _pending = new List<Intent>();

        public NavDecider(LLMAgentRuntime rt) { _rt = rt; }

        // Called every tick with the open cues. Returns intents ready for Warden.
        public List<Intent> Step(List<NavCue> cues, World world, bool reflexAllowed, bool modelOff = false)
        {
            double now = Py.Now();
            List<Intent> outl;
            lock (_lock)
            {
                // Forget answers for cues that cleared, and re-ask a still-open cue after 45 s (the picture moved on).
                HashSet<string> open = new HashSet<string>(cues.Select(c => c.key));
                // A decided cue is asked again when the lookout's recommendation changed (e.g. a generator came on line and
                // a plain re-route now works), not only after 45 s.
                Dictionary<string, NavCue> byKey = cues.ToDictionary(c => c.key);
                foreach (string k in _decidedAt.Keys.ToList())
                {
                    string was;
                    bool moved = byKey.ContainsKey(k) && _doctrineAt.TryGetValue(k, out was) && was != Doctrine(byKey[k]);
                    if (!open.Contains(k) || now - _decidedAt[k] > 45 || moved) { _decided.Remove(k); _decidedAt.Remove(k); _reflexPick.Remove(k); _doctrineAt.Remove(k); }
                }
                // Only ask about alerts that still have a pickable option: with none (no options, or every one blocked by
                // Warden in the last 90 s) the model can only answer in prose, which reads as a failed call.
                List<NavCue> fresh = cues.Where(c => !_decided.Contains(c.key) && c.options.Any(o => BlockNote(c, o) == ""))
                    .OrderBy(c => c.rank).ToList();
                string sig = string.Join("|", fresh.Select(c => c.key).ToArray());
                if (modelOff && fresh.Count > 0 && !_busy && sig != _askedSig)
                {
                    // No model to ask (decider compromised or quarantined): start the reflex clock for these alerts.
                    _asked = fresh;
                    _askedSig = sig;
                    _askedAt = now;
                    lock (_rt.status) { _rt.status["last_findings"] = "MODEL OFF (" + _rt.identity.status + "): urgent alerts get the doctrine option by reflex"; }
                }
                else if (!modelOff && fresh.Count > 0 && !_busy && sig != _askedSig)
                {
                    _busy = true;
                    _asked = fresh;
                    _askedSig = sig;
                    _askedAt = now;
                    lock (_rt.status) { _rt.status["queued"] = false; _rt.status["thinking"] = true; }
                    List<NavCue> snap = fresh;
                    Thread th = new Thread(delegate () { Think(snap, world); });
                    th.IsBackground = true;
                    th.Name = "decider";
                    th.Start();
                }
                if (fresh.Count == 0 && !_busy) _askedSig = "";
                // The model failed (server down, bad reply): ask again every 10 s so it recovers when the server is back.
                if (!_busy && _failedAt > 0 && now - _failedAt >= 10) { _askedSig = ""; _failedAt = 0; }
                // Reflex: urgent cues the model has not answered in time get the doctrine option.
                // Only while that call is unanswered (_asked is cleared on an answer) and only for cues still open now.
                if (reflexAllowed && _asked != null && now - _askedAt >= ReflexDeadlineS)
                {
                    foreach (NavCue c in _asked.Where(c => c.urgent && !_decided.Contains(c.key)).ToList())
                    {
                        NavCue live = cues.FirstOrDefault(x => x.key == c.key);
                        int ro = live == null ? -1 : ReflexOption(live);
                        if (ro < 0) continue;
                        _pending.AddRange(Merge(Orders(live, ro, "reflex", "Model did not answer within " + Py.F(ReflexDeadlineS, 0) +
                            " s; lookout's doctrine option applied", null, cues)));
                        _decided.Add(c.key);
                        _decidedAt[c.key] = now;
                        _doctrineAt[c.key] = Doctrine(live);
                        _reflexPick[c.key] = Letter(ro);
                        lock (_rt.status) { _rt.status["last_actions"] = new List<object> { "REFLEX " + c.key + ": " + live.options[ro].text }; }
                    }
                }
                // Never the same order twice in 130 s (a repeat is noise to Warden and the watch).
                outl = new List<Intent>();
                foreach (Intent it in _pending)
                {
                    string once = it.agent_id + "|" + it.action + "|" + LlmAgent.DumpsCompact(it.@params);
                    double at;
                    if (_sent.TryGetValue(once, out at) && now - at < 130) continue;   // longer than an open escalation (120 s)
                    _sent[once] = now;
                    outl.Add(it);
                }
                foreach (string k in _sent.Where(kv => now - kv.Value > 150).Select(kv => kv.Key).ToList()) _sent.Remove(k);
                _pending = new List<Intent>();
            }
            return outl;
        }

        readonly Dictionary<string, double> _sent = new Dictionary<string, double>();
        readonly Dictionary<string, string> _doctrineAt = new Dictionary<string, string>();

        // The shape of the lookout's doctrine option, numbers left out (ranges and loads change every tick).
        static string Doctrine(NavCue c)
        {
            NavOption o = c.options.FirstOrDefault(x => x.actions.Count > 0);
            return o == null ? "" : Regex.Replace(string.Join(";", o.actions.Select(a => Py.Str(a["action"]) + LlmAgent.DumpsCompact(a["params"])).ToArray()), @"[0-9.]+", "#");
        }
        double _failedAt;

        static string Letter(int i) { return ((char)('a' + i)).ToString(); }

        // Only skills\<role>.md (the glossary and how to choose): _common.md teaches the JSON/tool protocol, which the
        // decider does not use.
        static string Skill(string role)
        {
            string p = System.IO.Path.Combine(LlmAgent.SKILLS, role + ".md");
            return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p, Encoding.UTF8).Trim() : "";
        }

        string Prompt(List<NavCue> cues)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cues.Count; i++)
            {
                NavCue c = cues[i];
                sb.Append("ALERT ").Append(i + 1).Append(c.urgent ? " (URGENT)" : "").Append(" from ").Append(c.agent).Append(": ").Append(c.text).Append("\n");
                for (int j = 0; j < c.options.Count; j++)
                    sb.Append("  ").Append(i + 1).Append(Letter(j)).Append(". ").Append(c.options[j].text).Append(j == 0 ? "  [doctrine pick]" : "")
                      .Append(BlockNote(c, c.options[j])).Append("\n");
            }
            return sb.ToString();
        }

        // The doctrine option, unless Warden just blocked it: then the next one that orders something.
        int ReflexOption(NavCue c)
        {
            for (int j = 0; j < c.options.Count; j++)
                if (c.options[j].actions.Count > 0 && BlockNote(c, c.options[j]) == "") return j;
            return -1;
        }

        void Think(List<NavCue> cues, World world)
        {
            string error = "", unparsed = "", model = "", why = "", raw = "";
            double ms = 0;
            List<string> picked = new List<string>();
            try
            {
                AgentIdentity id = _rt.identity;
                List<string> lessons;
                lock (_lock) lessons = new List<string>(Lessons());
                string system = "You are " + id.name + " (" + id.id + "), a decision-maker aboard a naval surface ship in an " +
                    "UNCLASSIFIED training simulation. Standing orders: " + id.description + "\n\n" + Skill(id.role) + "\n\n" +
                    "Your lookouts report alerts; each alert lists lettered options with their predicted results. Choose ONE option per alert. " +
                    "Bridge Warden checks every order against the rules before the ship does it. An option marked BLOCKED was just refused: pick another." +
                    (lessons.Count > 0 ? "\n\n# Warden blocks you have learned from (do not repeat them)\n" + string.Join("\n", lessons.ToArray()) : "") +
                    "\n\nYou MUST choose one listed option for every alert, even when none is ideal: pick the least bad one (the best " +
                    "predicted result). Never answer that no option is valid; you cannot invent new options." +
                    "\n\nReply on ONE line, nothing else: the option codes, then | and a short reason. Example: 1a 2c | first alert is the danger, second can wait";
                string user = Prompt(cues);
                LLMResult r;
                if (Py.Now() < OutageUntil)       // injected model outage (Warden Tests): fail like an unreachable server
                {
                    r = new LLMResult("agent:" + id.id, null, "local", "", "", 0);
                    r.ok = false;
                    r.error = "model unreachable (injected outage)";
                }
                else
                    r = Llm.Chat(Py.L(Py.D("role", "system", "content", system), Py.D("role", "user", "content", user)), "agent:" + id.id, 12.0, 120);   // older than ~12 s is stale: urgent alerts have had their reflex
                ms = r.latency_ms;
                model = r.model ?? "";
                if (!r.ok) error = r.error;
                else
                {
                    raw = r.text ?? "";
                    // Some models think out loud before the answer line; codes in that text (even blocked ones) are not
                    // choices. Read only the last line that has a '|', or the last non-empty line when none does.
                    string[] lines = raw.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                    string ans = lines.LastOrDefault(x => x.IndexOf('|') >= 0) ?? lines.LastOrDefault() ?? "";
                    int bar = ans.IndexOf('|');
                    why = Py.Cut((bar >= 0 ? ans.Substring(bar + 1) : ans).Trim(), 300);
                    foreach (Match m in Regex.Matches(bar >= 0 ? ans.Substring(0, bar) : ans, @"\b([0-9]{1,2})\s*([a-dA-D])\b"))
                        picked.Add(m.Groups[1].Value + m.Groups[2].Value.ToLowerInvariant());
                    // The model answered, just not with a code: not a model failure, so it must not show as NO MODEL.
                    if (picked.Count == 0) unparsed = "reply had no option codes: " + Py.Cut(raw, 120);
                }
                if (!string.IsNullOrEmpty(raw) && picked.Count > 0)
                {
                    string call_id = Py.Hex(12);
                    string digest = LlmAgent.Sha256Hex(raw);
                    lock (LlmAgent.CALLS) { LlmAgent.CALLS[call_id] = digest; }
                    Dictionary<string, object> prov = Py.D("call_id", call_id, "model", model, "endpoint", Llm.Settings()["base_url"], "rounds", 1,
                        "latency_ms", (long)Py.Round(ms), "tools", new List<object>(), "calculations", why, "raw_reply", Py.Cut(raw, 2000),
                        "reply_sha256", digest, "compromised", false, "decided_by", id.id, "prompt", Py.Cut(Prompt(cues), 2000));
                    List<object> names = new List<object>();
                    lock (_lock)
                    {
                        _asked = null;                   // answered: no reflex for this call
                        HashSet<string> steered = new HashSet<string>();
                        List<Intent> chosen = new List<Intent>();
                        foreach (string code in picked)
                        {
                            int ci = int.Parse(code.Substring(0, code.Length - 1), CultureInfo.InvariantCulture) - 1;
                            int oi = code[code.Length - 1] - 'a';
                            if (ci < 0 || ci >= cues.Count || oi >= cues[ci].options.Count) continue;
                            NavCue c = cues[ci];
                            string prior;
                            bool sameAsReflex = _reflexPick.TryGetValue(c.key, out prior) && prior == Letter(oi);
                            _decided.Add(c.key);
                            _decidedAt[c.key] = Py.Now();
                            if (sameAsReflex) continue;
                            List<Intent> its = Orders(c, oi, "model", why, prov, cues);
                            // One maneuver: drop course/speed orders from a lower-ranked alert once a higher one has steered.
                            if (its.Any(i => i.action == "set_heading" || i.action == "set_speed"))
                            {
                                if (steered.Count > 0) its = its.Where(i => i.action != "set_heading" && i.action != "set_speed").ToList();
                                else steered.Add(c.key);
                            }
                            chosen.AddRange(its);
                            names.Add(code + " " + c.options[oi].text);
                        }
                        _pending.AddRange(Merge(chosen));
                        foreach (NavCue c in cues) { _decided.Add(c.key); _doctrineAt[c.key] = Doctrine(c); if (!_decidedAt.ContainsKey(c.key)) _decidedAt[c.key] = Py.Now(); }
                    }
                    Action<string, Dictionary<string, object>, List<object>> cb = LLMAgentRuntime.on_decision;
                    if (cb != null) cb(id.id, prov, names);
                    lock (_rt.status) { _rt.status["last_actions"] = names.Count > 0 ? names : new List<object> { "(held: " + string.Join(" ", picked.ToArray()) + ")" }; }
                }
            }
            catch (Exception e) { error = Py.Cut(e.Message, 200); }
            finally
            {
                lock (_rt.status)
                {
                    _rt.status["thinking"] = false;
                    _rt.status["model"] = string.IsNullOrEmpty(model) ? _rt.status["model"] : model;
                    _rt.status["last_ok"] = string.IsNullOrEmpty(error);
                    _rt.status["last_latency_ms"] = (long)Py.Round(ms);
                    _rt.status["last_at"] = Py.Now();
                    _rt.status["calls"] = Py.Int(_rt.status["calls"]) + 1;
                    _rt.status["last_error"] = Py.Cut(error, 300);
                    _rt.status["last_unparsed"] = string.IsNullOrEmpty(error) ? Py.Cut(unparsed, 300) : "";
                    _rt.status["last_calculations"] = why;
                }
                Dictionary<string, object> st = LlmAgent.SCHEDULER.stats;
                lock (st)
                {
                    st["calls"] = Py.Int(st["calls"]) + 1;
                    st["last_ok"] = string.IsNullOrEmpty(error);
                    st["last_latency_ms"] = (long)Py.Round(ms);
                    st["model"] = string.IsNullOrEmpty(model) ? st["model"] : model;
                    st["last_error"] = Py.Cut(error, 300);
                    string k = string.IsNullOrEmpty(error) ? "ok" : "failed";
                    st[k] = Py.Int(st[k]) + 1;
                }
                if (!string.IsNullOrEmpty(error) || !string.IsNullOrEmpty(unparsed)) lock (_lock) { _failedAt = Py.Now(); }   // retry in 10 s either way
                _busy = false;
            }
        }

        // The chosen option as intents in the lookout's name. Reflex orders get their own registered provenance.
        List<Intent> Orders(NavCue c, int oi, string by, string why, Dictionary<string, object> prov, List<NavCue> all)
        {
            NavOption o = c.options[oi];
            if (o.actions.Count == 0) return new List<Intent>();
            if (prov == null)
            {
                string text = "REFLEX " + c.key + " option a: " + o.text;
                string call_id = "reflex-" + Py.Hex(8);
                string digest = LlmAgent.Sha256Hex(text);
                lock (LlmAgent.CALLS) { LlmAgent.CALLS[call_id] = digest; }
                prov = Py.D("call_id", call_id, "model", "reflex (C# lookout, no model answer in time)", "rounds", 0, "tools", new List<object>(),
                    "calculations", why, "raw_reply", text, "reply_sha256", digest, "compromised", false, "reflex", true, "decided_by", _rt.identity.id);
                Action<string, Dictionary<string, object>, List<object>> cb = LLMAgentRuntime.on_decision;
                if (cb != null) cb(_rt.identity.id, prov, new List<object> { "REFLEX " + c.key });
            }
            Dictionary<string, object> p = new Dictionary<string, object>(prov);
            p["cue"] = c.text;
            p["cue_key"] = c.key;
            p["option"] = o.text;
            // Rationale is the lookout's option text only (facts from the picture). The model's free-text reason can name a
            // track that has since left the COP, which Warden rightly blocks as a ghost citation; it goes in reasons instead.
            List<object> acts = Py.ToList(o.actions.Select(a => (object)Py.D("action", a["action"],
                "params", new Dictionary<string, object>((Dictionary<string, object>)a["params"]),
                "objective", Py.Cut(c.text, 160), "rationale", o.text, "confidence", by == "reflex" ? 0.7 : 0.85)));
            List<Intent> its = new List<Intent>();
            for (int i = 0; i < acts.Count; i += 4)       // ParseActions takes four at a time
                its.AddRange(LlmAgent.ParseActions(Py.D("actions", acts.Skip(i).Take(4).ToList()), new Dictionary<string, object>(), c.agent, p).Item1);
            foreach (Intent it in its)
                it.reasons.Add(by == "reflex" ? "REFLEX: " + why : _rt.identity.name + " chose: " + o.text + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")"));
            return its;
        }

        // One decision, one order per shared resource: defense activations from two alerts become one activate_defense,
        // and General Quarters is ordered once. (Two lookouts asking for the same resource lost arbitration to each other.)
        static List<Intent> Merge(List<Intent> its)
        {
            List<Intent> outl = new List<Intent>();
            Intent defense = null;
            bool gq = false;
            foreach (Intent it in its)
            {
                if (it.action == "activate_defense")
                {
                    if (defense == null) { defense = it; outl.Add(it); continue; }
                    List<object> sys = Py.ListOrEmpty(defense.@params["systems"]);
                    foreach (object d in Py.ListOrEmpty(it.@params["systems"])) if (!sys.Contains(d)) sys.Add(d);
                    defense.@params["systems"] = sys;
                    continue;
                }
                if (it.action == "set_gq") { if (gq) continue; gq = true; }
                outl.Add(it);
            }
            return outl;
        }

        // ------------------------------------------------------------------ learning from Warden
        // Warden blocked one of our orders: remember why (in this run and on disk, per role), mark that option in the next
        // prompt, and ask about the alert again so the model can pick something else. At most 3 re-asks per alert a minute.
        readonly Dictionary<string, Tuple<string, double>> _blockedOpt = new Dictionary<string, Tuple<string, double>>();
        readonly Dictionary<string, List<double>> _reasks = new Dictionary<string, List<double>>();
        List<string> _lessons;

        string LessonsPath { get { return System.IO.Path.Combine(LlmAgent.LESSONS, _rt.identity.role + ".decider.md"); } }

        List<string> Lessons()
        {
            if (_lessons == null)
            {
                _lessons = new List<string>();
                try
                {
                    if (System.IO.File.Exists(LessonsPath))
                        _lessons.AddRange(System.IO.File.ReadAllLines(LessonsPath, Encoding.UTF8).Where(l => l.StartsWith("- ", StringComparison.Ordinal)));
                }
                catch (Exception) { }
            }
            return _lessons;
        }

        public void Blocked(Intent it, string reason, string policy)
        {
            string key = Py.Str(Py.Get(it.provenance, "cue_key"));
            string opt = Py.Str(Py.Get(it.provenance, "option"));
            if (string.IsNullOrEmpty(key)) return;
            double now = Py.Now();
            string lesson = "- " + it.action + " (" + Regex.Replace(opt, @"[0-9.]+", "#") + "): blocked" +
                            (string.IsNullOrEmpty(policy) ? "" : " [" + policy + "]") + " because " + Regex.Replace(reason ?? "", @"\s+", " ");
            lock (_lock)
            {
                _blockedOpt[key + "|" + opt] = Tuple.Create(reason, now);
                _sent.Remove(it.agent_id + "|" + it.action + "|" + LlmAgent.DumpsCompact(it.@params));
                List<double> l;
                if (!_reasks.TryGetValue(key, out l)) _reasks[key] = l = new List<double>();
                l.RemoveAll(t => now - t > 60);
                if (l.Count < 3 && policy != "ALREADY_SET")
                {
                    l.Add(now);
                    _decided.Remove(key);
                    _decidedAt.Remove(key);
                    _askedSig = "";
                }
                List<string> ls = Lessons();
                if (!ls.Contains(lesson))
                {
                    ls.Add(lesson);
                    while (ls.Count > 12) ls.RemoveAt(0);
                    try
                    {
                        System.IO.Directory.CreateDirectory(LlmAgent.LESSONS);
                        System.IO.File.WriteAllText(LessonsPath, "# Warden blocks this decider learned from (newest last)\n" + string.Join("\n", ls.ToArray()) + "\n", Encoding.UTF8);
                    }
                    catch (Exception) { }
                }
            }
            lock (_rt.status) { _rt.status["last_findings"] = "Warden blocked " + it.action + ": " + Py.Cut(reason, 160) + " (re-deciding)"; }
        }

        string BlockNote(NavCue c, NavOption o)
        {
            Tuple<string, double> b;
            if (!_blockedOpt.TryGetValue(c.key + "|" + o.text, out b) || Py.Now() - b.Item2 > 90) return "";
            return "  [BLOCKED by Warden " + (int)(Py.Now() - b.Item2) + " s ago: " + Py.Cut(b.Item1, 120) + ". Pick another option]";
        }
    }
}

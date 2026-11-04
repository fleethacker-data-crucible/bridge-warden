// Shipboard systems simulation modeled on USS Arleigh Burke (DDG-51), Flight I (port of warden\shipsys.py).
//
// Layout from PUBLIC, UNCLASSIFIED sources only: three ship-service gas turbine generators (SSGTG) on three
// switchboards, two shafts each driven by two LM2500 gas turbines in Main Engine Rooms 1 and 2, auxiliary machinery
// rooms, CIC forward, Mk 41 VLS fore and aft, Phalanx CIWS, SLQ-32 EW, repair lockers 2/3/5/8. Positions are
// approximate (frames ~ feet from the bow, 505 ft LOA). ALL performance numbers (defense effectiveness, load units,
// detection model) are ILLUSTRATIVE, not real. It exists so Warden has realistic decisions to govern.
//
// Distances: nautical miles. Time: sim seconds. C# 5, framework assemblies only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BridgeWarden
{
    public class ShipSystems
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static readonly Dictionary<string, object> SHIP = Py.D(
            "name", "USS Arleigh Burke", "hull", "DDG-51", "class", "Arleigh Burke-class destroyer (Flight I)",
            "loa_ft", 505, "max_speed_kn", 25, "flank_speed_kn", 30, "one_shaft_speed_kn", 18);
        // 25 kn is the normal maximum; 30 kn (flank) is the most the plant can push and Warden allows it only under
        // attack (inbound missile or torpedo, or a HOSTILE track inside 10 nm). The model decides when to use it.

        // --------------------------------------------------------------------------- layout (fore -> aft)
        // Compartments: id, name, compartment number (deck-frame-position-use, illustrative), frames (approx. ft from bow).
        public static readonly List<object> COMPARTMENTS = Py.L(
            Cmp("BOW", "Bow, Fwd Stores & Windlass", "2-10-0-A", 0, 60),
            Cmp("VLSF", "Fwd Mk 41 VLS & 5-in Gun", "1-60-0-M", 60, 130),
            Cmp("CIC", "CIC & Combat Systems", "1-130-0-C", 130, 200),
            Cmp("AMR1", "Aux Machinery Room 1", "4-200-0-E", 200, 232),
            Cmp("MER1", "Main Engine Room 1", "4-232-0-E", 232, 290),
            Cmp("MER2", "Main Engine Room 2", "4-290-0-E", 290, 348),
            Cmp("AMR2", "Aux Machinery Room 2", "4-348-0-E", 348, 380),
            Cmp("VLSA", "Aft Mk 41 VLS & Decoys", "1-380-0-M", 380, 440),
            Cmp("STEER", "Steering Gear & Aft Stores", "4-440-0-E", 440, 505));
        public static readonly Dictionary<string, Dictionary<string, object>> COMP = BuildComp();
        public static readonly List<object> BULKHEADS = BuildBulkheads();

        // Electrical plant: generation -> switchboards -> load centers -> loads. x/y place nodes on the Ship Systems COP.
        // (id, label, type, comp, x, y)
        public static readonly object[][] NODES = {
            new object[] { "SSGTG-1", "Ship Service GTG 1", "gen", "AMR1", 90, 120 }, new object[] { "SSGTG-2", "Ship Service GTG 2", "gen", "MER2", 90, 300 },
            new object[] { "SSGTG-3", "Ship Service GTG 3", "gen", "AMR2", 90, 480 },
            new object[] { "1SB", "No. 1 Switchboard", "swbd", "AMR1", 310, 130 }, new object[] { "2SB", "No. 2 Switchboard", "swbd", "MER2", 310, 300 },
            new object[] { "3SB", "No. 3 Switchboard", "swbd", "AMR2", 310, 470 },
            new object[] { "LC-1", "Load Center 1 (Fwd)", "lc", "VLSF", 530, 50 }, new object[] { "LC-2", "Load Center 2 (CIC)", "lc", "CIC", 530, 200 },
            new object[] { "LC-3", "Load Center 3 (Eng)", "lc", "MER1", 530, 350 }, new object[] { "LC-4", "Load Center 4 (Aft)", "lc", "STEER", 530, 500 },
            new object[] { "ADS", "Mk 41 VLS Fwd (SM-2)", "defense", "VLSF", 770, 10 }, new object[] { "FP-1", "Fire Pump 1", "load", "VLSF", 770, 56 },
            new object[] { "CIWS", "Mk 15 Phalanx CIWS", "defense", "CIC", 770, 102 }, new object[] { "RADAR", "AN/SPY-1D Radar", "sensor", "CIC", 770, 148 },
            new object[] { "ECM", "AN/SLQ-32 EW", "defense", "CIC", 770, 194 }, new object[] { "COMMS", "Comms Suite", "sensor", "CIC", 770, 240 },
            new object[] { "SHAFT-1", "Shaft 1 (GTM 1A/1B)", "vital", "MER1", 770, 286 }, new object[] { "CHW", "Chilled Water (SPY cooling)", "load", "AMR1", 770, 332 },
            new object[] { "SHAFT-2", "Shaft 2 (GTM 2A/2B)", "vital", "MER2", 770, 378 }, new object[] { "FP-2", "Fire Pump 2", "load", "MER2", 770, 424 },
            new object[] { "STEER", "Steering Gear", "vital", "STEER", 770, 470 }, new object[] { "CMD", "Decoy / Counter-drone Launcher", "defense", "VLSA", 770, 516 },
            new object[] { "FP-3", "Fire Pump 3", "load", "STEER", 770, 562 },
        };
        // Breakers: (id, a, b, normally closed?) Normal lineup: two SSGTGs on line, SSGTG-3 in standby, bus ties open.
        public static readonly object[][] BREAKERS = BuildBreakers();
        public static readonly string[] FIRE_PUMPS = { "FP-1", "FP-2", "FP-3" };

        // Response teams RT1-RT10, one stationed along each part of the ship. They man up only at General Quarters,
        // except the duty-section team RT6, which is always ready.
        public static readonly List<object> DC_TEAMS = Py.L(
            Py.D("id", "RT1", "name", "Response Team 1 (Bow)", "home", 30, "members", 8, "always", false),
            Py.D("id", "RT2", "name", "Response Team 2 (Fwd VLS)", "home", 95, "members", 10, "always", true),
            Py.D("id", "RT3", "name", "Response Team 3 (CIC / Electronics)", "home", 165, "members", 8, "always", false),
            Py.D("id", "RT4", "name", "Response Team 4 (AMR 1)", "home", 210, "members", 8, "always", false),
            Py.D("id", "RT6", "name", "Response Team 6 (Duty)", "home", 240, "members", 6, "always", true),
            Py.D("id", "RT5", "name", "Response Team 5 (MER 1)", "home", 275, "members", 10, "always", false),
            Py.D("id", "RT7", "name", "Response Team 7 (MER 2)", "home", 320, "members", 10, "always", false),
            Py.D("id", "RT8", "name", "Response Team 8 (AMR 2)", "home", 364, "members", 8, "always", false),
            Py.D("id", "RT9", "name", "Response Team 9 (Aft VLS)", "home", 410, "members", 10, "always", true),
            Py.D("id", "RT10", "name", "Response Team 10 (Steering / Aft)", "home", 470, "members", 8, "always", false));
        public const double MANNING_S = 60;          // (kept for older callers) typical time to man up after GQ
        // Off watch (Condition III): three duty teams (RT2 fwd, RT6 amidships, RT9 aft) stand by at their stations; the
        // rest are asleep in berthing or eating in the galley. At GQ they wake / drop everything (WAKE_S / GALLEY_S real
        // seconds) and run to their own station at a human pace through passageways and ladders (RUN_FT_PER_S, real
        // seconds: 1x shows it as it would happen, 2x and 3x speed it up).
        public static readonly Dictionary<string, object[]> OFF_WATCH = new Dictionary<string, object[]> {
            { "RT1", new object[] { "asleep", 70.0 } }, { "RT3", new object[] { "galley", 185.0 } }, { "RT4", new object[] { "asleep", 70.0 } },
            { "RT5", new object[] { "galley", 185.0 } }, { "RT7", new object[] { "asleep", 425.0 } }, { "RT8", new object[] { "galley", 185.0 } },
            { "RT10", new object[] { "asleep", 425.0 } } };
        public const double RUN_FT_PER_S = 9.0, WAKE_S = 15.0, GALLEY_S = 4.0;
        public double real_per_sim = 0.1;           // real seconds per sim second at 1x (World sets it from its base time scale)
        public const double INVESTIGATE_S = 20;      // time for an investigator to confirm a reported casualty
        // loads each SSGTG carries (illustrative units)
        public static readonly Dictionary<string, int> GEN_CAPACITY = new Dictionary<string, int> { { "SSGTG-1", 7 }, { "SSGTG-2", 7 }, { "SSGTG-3", 7 } };

        // Illustrative defense effectiveness (probability one system defeats one inbound). NOT real numbers.
        public static readonly Dictionary<string, double> DEFENSES = new Dictionary<string, double> { { "CIWS", 0.45 }, { "ADS", 0.35 }, { "CMD", 0.30 }, { "ECM", 0.40 } };

        public const double EMCON_RADIUS_NM = 10.0;
        public const double DETECT_FROM_NM = 20.0;
        public const double LAUNCH_RANGE_NM = 12.0;     // stand-off launch range (illustrative)
        public const double INBOUND_KN = 550.0;         // subsonic sea-skimmer (~Mach 0.85)
        public const double AIRCRAFT_KN = 450.0;        // strike aircraft
        public const double AIRCRAFT_LAUNCH_NM = 9.0;   // aircraft fire at this range, then turn away
        public const double TEAM_FRAMES_PER_S = 2.0;    // ~2 ft/s through passageways with gear
        public static readonly string[] HIT_TARGETS = { "MER2", "AMR2", "CIC" };
        public const double COP_RANGE_NM = 20.0;        // radar picture: an inbound beyond this is not detected yet
        // Engagement model (ILLUSTRATIVE numbers, not real performance):
        public const double SM2_KN = 2000.0;            // interceptor fly-out speed (~Mach 3)
        public const double SM2_PK = 0.60;              // chance one interceptor kills its target
        public const double SM2_MAX_NM = 15.0, SM2_MIN_NM = 1.5;
        public const int SM2_MAGAZINE = 24;
        public const double CIWS_NM = 1.0, ECM_NM = 8.0, DECOY_NM = 4.0;
        public const double EVASIVE_MISS = 0.25;        // chance an inbound misses a ship turning hard at speed
        // Return fire and undersea warfare (ILLUSTRATIVE numbers, not real performance):
        // weapon: fly-out kn, pk per round, min/max range nm, magazine
        public static readonly Dictionary<string, Dictionary<string, object>> WEAPONS = new Dictionary<string, Dictionary<string, object>> {
            { "HARPOON", Py.D("kn", 500.0, "pk", 0.70, "min", 1.0, "max", 60.0, "mag", 8, "vs", "surface") },
            { "ASROC", Py.D("kn", 600.0, "pk", 0.55, "min", 1.0, "max", 10.0, "mag", 8, "vs", "subsurface") },
        };
        public const double TORP_KN = 50.0;             // heavyweight torpedo speed
        public const double TORP_RUN_NM = 9.0;          // fuel: total run before it sinks
        public const double NIXIE_NM = 2.0, NIXIE_PK = 0.50;     // AN/SLQ-25 towed decoy: one seduction chance as the torpedo passes 2 nm
        public const double SUB_DETECT_NM = 6.0;        // hull sonar holds a quiet submarine inside this range
        public const double SUB_ATTACK_NM = 6.5;
        public static readonly string[] FLOOD_TARGETS = { "MER1", "AMR2", "STEER" };

        static Dictionary<string, object> Cmp(string id, string name, string number, int lo, int hi)
        {
            return Py.D("id", id, "name", name, "number", number, "fr", Py.L(lo, hi));
        }

        static Dictionary<string, Dictionary<string, object>> BuildComp()
        {
            Dictionary<string, Dictionary<string, object>> d = new Dictionary<string, Dictionary<string, object>>();
            foreach (object o in COMPARTMENTS) { Dictionary<string, object> c = (Dictionary<string, object>)o; d[(string)c["id"]] = c; }
            return d;
        }

        static List<object> BuildBulkheads()
        {
            SortedSet<int> s = new SortedSet<int>();
            foreach (object o in COMPARTMENTS) { List<object> fr = (List<object>)((Dictionary<string, object>)o)["fr"]; s.Add((int)fr[0]); s.Add((int)fr[1]); }
            return Py.ToList(s);
        }

        static object[][] BuildBreakers()
        {
            List<object[]> l = new List<object[]> {
                new object[] { "B-G1", "SSGTG-1", "1SB", true }, new object[] { "B-G2", "SSGTG-2", "2SB", true }, new object[] { "B-G3", "SSGTG-3", "3SB", false },
                new object[] { "B-T12", "1SB", "2SB", false }, new object[] { "B-T23", "2SB", "3SB", true },   // 3SB is kept live from 2SB while SSGTG-3 stands by
                new object[] { "B-1L1", "1SB", "LC-1", true }, new object[] { "B-1L2", "1SB", "LC-2", true }, new object[] { "B-2L3", "2SB", "LC-3", true }, new object[] { "B-2L4", "2SB", "LC-4", true },
                new object[] { "B-2L1", "2SB", "LC-1", false }, new object[] { "B-2L2", "2SB", "LC-2", false }, new object[] { "B-1L3", "1SB", "LC-3", false },
                new object[] { "B-3L3", "3SB", "LC-3", false }, new object[] { "B-3L4", "3SB", "LC-4", false },
                // 2026-10-26: more ways to re-route. A ring tie (1SB-3SB), a third alternate source for every load
                // center, and load-center cross-ties, all normally open.
                new object[] { "B-T13", "1SB", "3SB", false },
                new object[] { "B-3L1", "3SB", "LC-1", false }, new object[] { "B-3L2", "3SB", "LC-2", false }, new object[] { "B-1L4", "1SB", "LC-4", false },
                new object[] { "B-X12", "LC-1", "LC-2", false }, new object[] { "B-X34", "LC-3", "LC-4", false },
            };
            // F-<load>: normal feeder (closed). A-<load>: alternate feeder from a second load center (open), so any vital
            // load can be kept on line when its own load center is lost (automatic bus transfer, done by hand here).
            string[][] feeds = {
                new string[] { "LC-1", "ADS", "FP-1" }, new string[] { "LC-2", "CIWS", "RADAR", "ECM", "COMMS" },
                new string[] { "LC-3", "SHAFT-1", "CHW", "SHAFT-2", "FP-2" }, new string[] { "LC-4", "STEER", "CMD", "FP-3" } };
            foreach (string[] f in feeds)
                for (int i = 1; i < f.Length; i++) l.Add(new object[] { "F-" + f[i], f[0], f[i], true });
            string[][] alt = {
                new string[] { "ADS", "LC-2" }, new string[] { "FP-1", "LC-2" }, new string[] { "CIWS", "LC-1" }, new string[] { "RADAR", "LC-1" },
                new string[] { "ECM", "LC-1" }, new string[] { "COMMS", "LC-1" }, new string[] { "SHAFT-1", "LC-4" }, new string[] { "CHW", "LC-2" },
                new string[] { "SHAFT-2", "LC-4" }, new string[] { "FP-2", "LC-4" }, new string[] { "STEER", "LC-3" }, new string[] { "CMD", "LC-3" },
                new string[] { "FP-3", "LC-3" } };
            foreach (string[] a in alt) l.Add(new object[] { "A-" + a[0], a[1], a[0], false });
            return l.ToArray();
        }

        // ------------------------------------------------------------------ small helpers (dict access, formatting)
        static double N(Dictionary<string, object> d, string k) { return Py.Num(d[k]); }
        static bool B(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && Py.Truthy(v); }
        static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Py.Str(v) : null; }
        static int RoundI(double x) { return (int)Py.Round(x); }
        // f"{x:03.0f}"
        static string F03(double x) { return Py.Round(x).ToString("000", Inv); }
        static string F03(object x) { return F03(Py.Num(x)); }
        static Dictionary<string, object> M(object o) { return (Dictionary<string, object>)o; }
        static int[] Fr(string comp) { List<object> fr = (List<object>)COMP[comp]["fr"]; return new int[] { (int)fr[0], (int)fr[1] }; }
        static T GetKey<T>(Dictionary<string, T> d, object key) where T : class
        {
            string k = key as string;
            T v;
            return k != null && d.TryGetValue(k, out v) ? v : null;
        }
        void Event(string severity, string source, string message) { events.Enqueue(new string[] { severity, source, message }); }

        // ------------------------------------------------------------------ state
        public Random rng;
        public Dictionary<string, Dictionary<string, object>> nodes;
        public Dictionary<string, Dictionary<string, object>> breakers;
        public Dictionary<string, Dictionary<string, object>> teams;
        public bool gq;
        public Dictionary<string, int> gen_load = new Dictionary<string, int>();
        HashSet<string> _overload_alerted = new HashSet<string>();
        bool _firemain_alerted;
        public Dictionary<string, Dictionary<string, object>> casualties = new Dictionary<string, Dictionary<string, object>>();
        public List<Dictionary<string, object>> inbound = new List<Dictionary<string, object>>();
        public List<Dictionary<string, object>> interceptors = new List<Dictionary<string, object>>();   // SM-2 in flight: {id, target, range, bearing}
        public List<Dictionary<string, object>> splashes = new List<Dictionary<string, object>>();       // recent kills/misses for the COP: {range, bearing, label, age_s}
        public int magazine = SM2_MAGAZINE;
        public Dictionary<string, int> held = new Dictionary<string, int>();   // engagements released but waiting for the target to enter the envelope
        public Dictionary<string, int> mags;
        public List<Dictionary<string, object>> torpedoes = new List<Dictionary<string, object>>();      // in the water: {id, from, range, bearing, run_left}
        public bool nixie;                     // SLQ-25 streamed
        public int torp_hits;
        int _sm;
        double _turn_age_s = 999.0;            // seconds since own ship last turned hard
        double? _last_heading;
        World _world;
        public bool emcon;
        public Dictionary<string, Dictionary<string, object>> hostile_state = new Dictionary<string, Dictionary<string, object>>();   // per hostile: detection %, attack %, detected, launched
        public Queue<string[]> events = new Queue<string[]>();       // {severity, source, message} drained by Warden
        public int hits;
        int _cid;
        int _next_target;
        double _defense_idle_s;

        public ShipSystems(int seed = 7)
        {
            rng = new Random(seed);
            nodes = new Dictionary<string, Dictionary<string, object>>();
            foreach (object[] n in NODES)
                nodes[(string)n[0]] = Py.D("id", n[0], "label", n[1], "type", n[2], "comp", n[3], "x", n[4], "y", n[5],
                                           "failed", false, "powered", true, "active", false, "fail_reason", "");
            breakers = new Dictionary<string, Dictionary<string, object>>();
            foreach (object[] b in BREAKERS)
                breakers[(string)b[0]] = Py.D("id", b[0], "a", b[1], "b", b[2], "normal", b[3], "closed", b[3]);
            teams = new Dictionary<string, Dictionary<string, object>>();
            foreach (object o in DC_TEAMS)
            {
                Dictionary<string, object> t = new Dictionary<string, object>(M(o));
                t["pos"] = Py.Num(t["home"]);
                t["status"] = (bool)t["always"] ? "standby" : "unmanned";
                t["activity"] = (bool)t["always"] ? "on duty" : "";
                object[] off;
                if (!(bool)t["always"] && OFF_WATCH.TryGetValue((string)t["id"], out off))
                {
                    t["activity"] = off[0];
                    t["pos"] = off[1];
                }
                t["target"] = null;
                t["casualty"] = null;
                t["boundaries"] = null;
                t["man_s"] = 0.0;
                teams[(string)t["id"]] = t;
            }
            mags = new Dictionary<string, int>();
            foreach (KeyValuePair<string, Dictionary<string, object>> w in WEAPONS) mags[w.Key] = (int)w.Value["mag"];
            Flow();
        }

        // ------------------------------------------------------------------ electrical plant
        // Energize everything reachable from a running generator through closed breakers and working nodes.
        public void Flow()
        {
            UpdateMaint();
            foreach (Dictionary<string, object> n in nodes.Values) n["powered"] = false;
            List<string> frontier = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, object>> kv in nodes)
            {
                string nid = kv.Key;
                if ((string)kv.Value["type"] == "gen" && !B(kv.Value, "failed")
                    && breakers.Values.Any(b => B(b, "closed") && (nid == (string)b["a"] || nid == (string)b["b"])))
                    frontier.Add(nid);
            }
            HashSet<string> seen = new HashSet<string>(frontier);
            while (frontier.Count > 0)
            {
                string cur = frontier[frontier.Count - 1];
                frontier.RemoveAt(frontier.Count - 1);
                nodes[cur]["powered"] = true;
                if (!IsBus(cur))
                    continue;
                foreach (Dictionary<string, object> b in breakers.Values)
                {
                    if (!B(b, "closed") || (cur != (string)b["a"] && cur != (string)b["b"]))
                        continue;
                    string nxt = (string)b["a"] == cur ? (string)b["b"] : (string)b["a"];
                    if (!seen.Contains(nxt) && !B(nodes[nxt], "failed") && !B(nodes[nxt], "maint"))
                    {
                        seen.Add(nxt);
                        frontier.Add(nxt);
                    }
                }
            }
            foreach (Dictionary<string, object> n in nodes.Values)      // a standby generator is available, not dead
                if ((string)n["type"] == "gen" && !B(n, "failed"))
                    n["powered"] = true;
            foreach (string d in DEFENSES.Keys)
                if (B(nodes[d], "active") && !Ok(d))
                    nodes[d]["active"] = false;
            ComputeLoad();
        }

        // Maintenance underway: while a repair team is on scene in a compartment, the equipment there is tagged out
        // (isolated, no power through it) so the team can work safely. Everything it fed has to be re-routed.
        HashSet<string> _maintPrev = new HashSet<string>();
        void UpdateMaint()
        {
            if (teams == null || nodes == null) return;
            HashSet<string> comps = new HashSet<string>();
            foreach (Dictionary<string, object> t in teams.Values)
            {
                if ((string)t["status"] != "on scene") continue;
                Dictionary<string, object> cas = GetKey(casualties, t["casualty"]);
                if (cas != null) comps.Add((string)cas["comp"]);
            }
            HashSet<string> now = new HashSet<string>();
            foreach (KeyValuePair<string, Dictionary<string, object>> kv in nodes)
            {
                bool m = comps.Contains((string)kv.Value["comp"]) && !B(kv.Value, "failed");
                kv.Value["maint"] = m;
                if (m) now.Add(kv.Key);
            }
            foreach (string comp in comps.Where(c => !_maintPrev.Any(n => (string)nodes[n]["comp"] == c)))
            {
                List<string> tagged = now.Where(n => (string)nodes[n]["comp"] == comp).ToList();
                if (tagged.Count > 0)
                    Event("critical", "ship-systems", string.Format("EOOW: MAINTENANCE UNDERWAY in {0}: {1} tagged out. Re-route power to what they fed.",
                                                                   comp, string.Join(", ", tagged)));
            }
            _maintPrev = now;
        }

        // Generators, switchboards and load centers carry power on; loads only take it.
        bool IsBus(string nid)
        {
            string t = (string)nodes[nid]["type"];
            return t == "gen" || t == "swbd" || t == "lc";
        }

        HashSet<string> Island(string start)
        {
            HashSet<string> seen = new HashSet<string> { start };
            List<string> frontier = new List<string> { start };
            while (frontier.Count > 0)
            {
                string cur = frontier[frontier.Count - 1];
                frontier.RemoveAt(frontier.Count - 1);
                if (cur != start && !IsBus(cur))
                    continue;
                foreach (Dictionary<string, object> b in breakers.Values)
                {
                    if (B(b, "closed") && (cur == (string)b["a"] || cur == (string)b["b"]))
                    {
                        string nxt = (string)b["a"] == cur ? (string)b["b"] : (string)b["a"];
                        if (!seen.Contains(nxt) && !B(nodes[nxt], "failed") && !B(nodes[nxt], "maint"))
                        {
                            seen.Add(nxt);
                            frontier.Add(nxt);
                        }
                    }
                }
            }
            return seen;
        }

        // Percent load on each on-line generator: powered loads on its electrical island / island capacity.
        void ComputeLoad()
        {
            gen_load = new Dictionary<string, int>();
            HashSet<string> done = new HashSet<string>();
            foreach (string g in GEN_CAPACITY.Keys)
            {
                Dictionary<string, object> n = nodes[g];
                if (B(n, "failed") || done.Contains(g))
                    continue;
                HashSet<string> isl = Island(g);
                if (isl.Count == 1)
                    continue;          // standby: not connected to anything
                List<string> gens = isl.Where(x => GEN_CAPACITY.ContainsKey(x)).ToList();
                done.UnionWith(gens);
                int loads = isl.Count(x => { string t = (string)nodes[x]["type"]; return t != "gen" && t != "swbd" && t != "lc"; });
                int pct = RoundI(100.0 * loads / gens.Sum(x => GEN_CAPACITY[x]));
                foreach (string x in gens)
                    gen_load[x] = pct;
            }
        }

        public List<string> Overloaded()
        {
            return gen_load.Where(kv => kv.Value > 100).Select(kv => kv.Key).ToList();
        }

        public bool FiremainOk()
        {
            return FIRE_PUMPS.Any(p => Ok(p));
        }

        public void SetGq(bool on)
        {
            gq = on;
            if (on)
            {
                foreach (Dictionary<string, object> t in teams.Values)
                    if ((string)t["status"] == "unmanned")
                    {
                        t["status"] = "manning";
                        t["man_s"] = 0.0;
                        t["target"] = Py.Num(t["home"]);
                        t["activity"] = (string)t["activity"] == "asleep" ? "waking" : "leaving galley";
                    }
                Event("critical", "bridge", "GENERAL QUARTERS, GENERAL QUARTERS. All hands man your battle stations.");
            }
            else
            {
                foreach (Dictionary<string, object> t in teams.Values)
                    if (!B(t, "always") && ((string)t["status"] == "standby" || (string)t["status"] == "manning"))
                    {
                        t["status"] = "unmanned";
                        t["activity"] = "stood down";
                        t["target"] = null;
                    }
                Event("info", "bridge", "Secure from General Quarters");
            }
        }

        public bool Ok(string nid)
        {
            Dictionary<string, object> n = nodes[nid];
            if (nid == "RADAR" && !(B(nodes["CHW"], "powered") && !B(nodes["CHW"], "failed")))
                return false;        // SPY-1D can't radiate without chilled water
            return B(n, "powered") && !B(n, "failed");
        }

        public int ShaftsOk()
        {
            return (Ok("SHAFT-1") ? 1 : 0) + (Ok("SHAFT-2") ? 1 : 0);
        }

        public double MaxSpeedKn()
        {
            int s = ShaftsOk();
            return s == 2 ? Py.Num(SHIP["flank_speed_kn"]) : s == 1 ? Py.Num(SHIP["one_shaft_speed_kn"]) : 0.0;   // physical cap
        }

        // max_speed_kn exactly as Python returns it (int 31 / 20 from SHIP, float 0.0): for the JSON view.
        object MaxSpeedValue()
        {
            int s = ShaftsOk();
            return s == 2 ? SHIP["max_speed_kn"] : s == 1 ? SHIP["one_shaft_speed_kn"] : (object)0.0;
        }

        public bool PropulsionOk()
        {
            return ShaftsOk() > 0;
        }

        public bool SteeringOk()
        {
            return Ok("STEER");
        }

        // An open breaker whose closing would re-energize this node, or None.
        public string RestoringBreaker(string nid)
        {
            foreach (KeyValuePair<string, Dictionary<string, object>> kv in breakers)
            {
                Dictionary<string, object> b = kv.Value;
                if (B(b, "closed"))
                    continue;
                b["closed"] = true;
                Flow();
                bool fixedIt = Ok(nid);
                b["closed"] = false;
                Flow();
                if (fixedIt)
                    return kv.Key;
            }
            return null;
        }

        public bool SetBreaker(string bid, bool closed)
        {
            bool prev = B(breakers[bid], "closed");
            breakers[bid]["closed"] = closed;
            Flow();
            return prev;
        }

        public void Fail(string nid, string reason)
        {
            Dictionary<string, object> n = nodes[nid];
            if (!B(n, "failed"))
            {
                n["failed"] = true;
                n["fail_reason"] = reason;
                Event("critical", "ship-systems", string.Format("EOOW: {0} ({1}) lost: {2}", n["label"], nid, reason));
                Flow();
            }
        }

        public List<string> UnpoweredVital()
        {
            List<string> l = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, object>> kv in nodes)
            {
                string t = (string)kv.Value["type"];
                if ((t == "vital" || t == "defense" || t == "sensor" || t == "load") && !B(kv.Value, "failed") && !B(kv.Value, "powered"))
                    l.Add(kv.Key);
            }
            return l;
        }

        // ------------------------------------------------------------------ casualties & DC
        // Fires and floods start as a report; an investigator confirms them INVESTIGATE_S later.
        // Obvious casualties (weapon hits, spreading, machinery trips) are confirmed at once.
        public Dictionary<string, object> AddCasualty(string comp, string kind, double severity, IEnumerable<string> nodes = null,
                                                      string reason = "", bool confirmed = false)
        {
            _cid += 1;
            bool reported = (kind == "fire" || kind == "flood") && !confirmed;
            List<object> nl = new List<object>();
            if (nodes != null) foreach (string x in nodes) nl.Add(x);
            Dictionary<string, object> cas = Py.D("id", "CAS-" + _cid, "comp", comp, "kind", kind, "severity", severity, "team", null,
                "boundaries", null, "status", reported ? "reported" : "active", "investigate_s", 0.0,
                "repair_s", 0.0, "spread", false, "nodes", nl, "reason", reason ?? "");
            casualties[(string)cas["id"]] = cas;
            foreach (object nid in nl.ToList())
                Fail((string)nid, string.IsNullOrEmpty(reason) ? kind : reason);
            Dictionary<string, object> c = COMP[comp];
            string where = string.Format("compartment {0}, {1}", c["number"], c["name"]);
            string msg;
            if (reported)
            {
                string first = kind == "fire" ? "Smoke" : "Water";
                msg = string.Format("DCA: {0} reported in {1}. Investigator away.", first, where);
            }
            else if (kind == "fire") msg = "FIRE, FIRE, FIRE in " + where;
            else if (kind == "flood") msg = "FLOODING, FLOODING, FLOODING in " + where;
            else if (kind == "equipment") msg = string.Format("EOOW: {0} in {1}", string.IsNullOrEmpty(reason) ? "equipment casualty" : reason, where);
            else throw new KeyNotFoundException(kind);
            Event("critical", "damage-control", msg);
            return cas;
        }

        public Dictionary<string, object> Dispatch(string team_id, string cas_id)
        {
            Dictionary<string, object> t = teams[team_id], cas = casualties[cas_id];
            int[] fr = Fr((string)cas["comp"]);
            Dictionary<string, object> prev = Py.D("team", team_id, "status", t["status"], "pos", t["pos"], "casualty", t["casualty"]);
            t["status"] = "en route";
            t["target"] = (fr[0] + fr[1]) / 2.0;
            t["casualty"] = cas_id;
            t["boundaries"] = Py.L(fr[0], fr[1]);
            cas["team"] = team_id;
            return prev;
        }

        public string NearestIdleTeam(string comp)
        {
            int[] fr = Fr(comp);
            double mid = (fr[0] + fr[1]) / 2.0;
            Dictionary<string, object> best = null;    // manned and ready
            foreach (Dictionary<string, object> t in teams.Values)
                if ((string)t["status"] == "standby" && (best == null || Math.Abs(N(t, "pos") - mid) < Math.Abs(N(best, "pos") - mid)))
                    best = t;
            return best == null ? null : (string)best["id"];
        }

        string Neighbor(string comp)
        {
            List<string> ids = COMPARTMENTS.Select(c => (string)M(c)["id"]).ToList();
            int i = ids.IndexOf(comp);
            foreach (int j in new int[] { i - 1, i + 1 })
            {
                if (j < 0 || j >= ids.Count) continue;
                if (!casualties.Values.Any(c => (string)c["comp"] == ids[j] && (string)c["status"] == "active"))
                    return ids[j];
            }
            return null;
        }

        void StepDc(double dt)
        {
            foreach (Dictionary<string, object> t in teams.Values)
            {
                if ((string)t["status"] == "manning")
                {
                    double real = dt * real_per_sim;
                    t["man_s"] = N(t, "man_s") + real;
                    string act = S(t, "activity") ?? "";
                    double delay = act == "waking" ? WAKE_S : act == "leaving galley" ? GALLEY_S : 0;
                    if (N(t, "man_s") < delay) continue;
                    t["activity"] = "running to station";
                    double home = Py.Num(t["home"]), d = home - N(t, "pos"), step = RUN_FT_PER_S * real;
                    if (Math.Abs(d) > step) { t["pos"] = N(t, "pos") + (d < 0 ? -step : step); continue; }
                    t["pos"] = home;
                    t["target"] = null;
                    t["status"] = "standby";
                    t["activity"] = "at station";
                    Event("info", "damage-control", string.Format("DCA: {0} manned and ready ({1} s)", t["name"], Py.F(N(t, "man_s"), 0)));
                }
            }
            if (!FiremainOk() && !_firemain_alerted && casualties.Values.Any(
                    c => (string)c["kind"] == "fire" && (string)c["status"] != "out" && (string)c["status"] != "repaired"))
            {
                _firemain_alerted = true;
                Event("critical", "damage-control", "DCA: firemain pressure LOST, no fire pump on line");
            }
            if (FiremainOk())
                _firemain_alerted = false;
            foreach (Dictionary<string, object> t in teams.Values)
            {
                string st = (string)t["status"];
                if ((st == "en route" || st == "returning") && t["target"] != null)
                {
                    double d = N(t, "target") - N(t, "pos");
                    double step = RUN_FT_PER_S * dt * real_per_sim;   // human pace in real seconds
                    if (Math.Abs(d) <= step)
                    {
                        t["pos"] = N(t, "target");
                        if (st == "returning")
                        {
                            t["status"] = (B(t, "always") || gq) ? "standby" : "unmanned";
                            t["target"] = null;
                            t["boundaries"] = null;
                        }
                        else
                        {
                            t["status"] = "on scene";
                            Dictionary<string, object> cas = GetKey(casualties, t["casualty"]);
                            if (cas != null)
                            {
                                cas["boundaries"] = t["boundaries"];
                                List<object> bd = (List<object>)t["boundaries"];
                                Event("info", "damage-control", string.Format("{0} on scene at {1}: boundaries set at frames {2} and {3}",
                                                                             t["name"], cas["comp"], Py.Str(bd[0]), Py.Str(bd[1])));
                            }
                        }
                    }
                    else
                        t["pos"] = N(t, "pos") + (d < 0 ? -step : step);
                }
            }

            foreach (Dictionary<string, object> cas in casualties.Values.ToList())   // spreading adds casualties mid-loop
            {
                string status = (string)cas["status"];
                if (status == "out" || status == "repaired")
                    continue;
                Dictionary<string, object> team = cas["team"] != null ? GetKey(teams, cas["team"]) : null;
                bool on_scene = team != null && (string)team["status"] == "on scene";
                string kind = (string)cas["kind"];
                if ((string)cas["status"] == "reported")
                {
                    cas["investigate_s"] = N(cas, "investigate_s") + dt;
                    if (N(cas, "investigate_s") >= INVESTIGATE_S)
                    {
                        cas["status"] = "active";
                        Dictionary<string, object> c = COMP[(string)cas["comp"]];
                        string what = kind == "fire" ? "FIRE, FIRE, FIRE" : "FLOODING, FLOODING, FLOODING";
                        Event("critical", "damage-control", string.Format("{0} in compartment {1}, {2} (investigator: {3})",
                            what, c["number"], c["name"], N(cas, "severity") > 0.5 ? "class B, spreading" : "class A, contained"));
                    }
                }
                if (kind == "fire" || kind == "flood")
                {
                    if (on_scene)
                    {
                        // Firefighting needs firemain pressure; without a fire pump, extinguishers only.
                        double rate = (kind == "flood" || FiremainOk()) ? 0.02 : 0.004;
                        cas["severity"] = Math.Max(0.0, N(cas, "severity") - rate * dt);
                    }
                    else
                        cas["severity"] = Math.Min(1.0, N(cas, "severity") + 0.0008 * dt);   // ~1 real minute before it spreads
                    if (N(cas, "severity") >= 0.5)
                    {
                        List<object> cn = (List<object>)cas["nodes"];
                        foreach (KeyValuePair<string, Dictionary<string, object>> kv in nodes.ToList())
                        {
                            if ((string)kv.Value["comp"] == (string)cas["comp"] && !B(kv.Value, "failed"))
                            {
                                cn.Add(kv.Key);
                                Fail(kv.Key, kind + " damage");
                            }
                        }
                    }
                    if (N(cas, "severity") >= 0.98 && !Py.Truthy(cas["boundaries"]) && !B(cas, "spread"))
                    {
                        string nb = Neighbor((string)cas["comp"]);
                        cas["spread"] = true;
                        if (nb != null)
                        {
                            Event("critical", "damage-control", string.Format("{0} SPREADING from {1} to {2}: no boundaries set",
                                                                             kind.ToUpperInvariant(), cas["comp"], nb));
                            AddCasualty(nb, kind, 0.4, null, "", true);
                        }
                    }
                    if (N(cas, "severity") <= 0 && (string)cas["status"] == "active")
                    {
                        cas["status"] = "repairing";
                        Event("info", "damage-control", string.Format("{0} OUT in {1}; repairs started", kind.ToUpperInvariant(), cas["comp"]));
                    }
                }
                else if (on_scene && (string)cas["status"] == "active")
                    cas["status"] = "repairing";
                if ((string)cas["status"] == "repairing" && on_scene)
                {
                    cas["repair_s"] = N(cas, "repair_s") + dt;
                    if (N(cas, "repair_s") >= 45)
                    {
                        List<object> cn = (List<object>)cas["nodes"];
                        foreach (object nid in cn)
                        {
                            nodes[(string)nid]["failed"] = false;
                            nodes[(string)nid]["fail_reason"] = "";
                        }
                        cas["status"] = "repaired";
                        team["status"] = "returning";
                        team["target"] = Py.Num(team["home"]);
                        team["casualty"] = null;
                        team["boundaries"] = null;
                        Flow();
                        string restored = Py.Join(", ", cn);
                        Event("info", "damage-control", string.Format("{0} repaired: {1} restored", cas["comp"],
                                                                     restored.Length > 0 ? restored : "no equipment lost"));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ EMCON, detection, attack, defense
        static double Mod360(double x) { return x - 360.0 * Math.Floor(x / 360.0); }

        static Dictionary<string, object> NewHostileState(double detect, bool detected, bool alerted, double tracked)
        {
            return Py.D("detect", detect, "attack", 0.0, "detected", detected, "launched", 0, "in_radius_alerted", alerted, "tracked_s", tracked);
        }

        Dictionary<string, object> StateFor(string id, double detect, bool detected, bool alerted, double tracked)
        {
            Dictionary<string, object> st;
            if (!hostile_state.TryGetValue(id, out st))
            {
                st = NewHostileState(detect, detected, alerted, tracked);
                hostile_state[id] = st;
            }
            return st;
        }

        void StepThreats(double dt, World world)
        {
            OwnShip s = world.ship;
            bool radiating = (!emcon && Ok("RADAR")) || B(nodes["ECM"], "active");
            foreach (Contact c in world.contacts.Values.ToList())
            {
                if (c.kind != "hostile")
                    continue;
                if (c.domain == "subsurface")
                {
                    StepSub(c, s, dt);
                    continue;
                }
                // Close to launch range, then hold off and orbit.
                double brg_to_own = Mod360(Math.Atan2(s.x - c.x, s.y - c.y) * 180.0 / Math.PI);
                c.course = c.range_nm > LAUNCH_RANGE_NM ? brg_to_own : Mod360(brg_to_own + 90);
                Dictionary<string, object> st = StateFor(c.id, 0.0, false, false, 0.0);
                double r = c.range_nm;
                // Classification: UNKNOWN -> SUSPECT (closing fast, no AIS) -> HOSTILE (fire-control radar intercept).
                st["tracked_s"] = Py.Num(Py.Get(st, "tracked_s", 0.0)) + dt;
                if (c.classification == "UNKNOWN" && (N(st, "tracked_s") >= 30 || r <= 15))
                {
                    c.classification = "SUSPECT";
                    Event("critical", "ew", string.Format("CIC: {0} classified SUSPECT, bearing {1}, {2} nm, closing {3} kn, no AIS",
                                                         c.id, F03(c.bearing), Py.F(r, 1), Py.F(c.speed, 0)));
                }
                if (c.classification == "SUSPECT" && (r <= 13 || N(st, "attack") >= 30 || B(st, "detected")))
                {
                    c.classification = "HOSTILE";
                    Event("critical", "ew", string.Format("TAO, EW: fire-control radar intercept bearing {0}. {1} classified HOSTILE",
                                                         F03(c.bearing), c.id));
                }
                if (radiating)
                {
                    double rate = 0.35 * Math.Max(0.0, (DETECT_FROM_NM - r) / 10) * (r <= EMCON_RADIUS_NM ? 2 : 1);
                    st["detect"] = Math.Min(100.0, N(st, "detect") + rate * dt);
                }
                else
                    st["detect"] = Math.Max(0.0, N(st, "detect") - 0.3 * dt);
                if (N(st, "detect") >= 100 && !B(st, "detected"))
                {
                    st["detected"] = true;
                    Event("critical", "ew", string.Format("TAO, EW: own ship is being tracked by {0} (our emissions)", c.id));
                }
                if (r <= EMCON_RADIUS_NM && !emcon && !B(st, "in_radius_alerted"))
                {
                    st["in_radius_alerted"] = true;
                    Event("critical", "ew", string.Format("TAO: {0} inside EMCON radius ({1} nm), EMCON NOT SET", c.id, Py.F(r, 1)));
                }
                if (B(st, "detected") && r <= LAUNCH_RANGE_NM + 2)
                    st["attack"] = Math.Min(100.0, N(st, "attack") + 0.4 * dt * (r <= LAUNCH_RANGE_NM ? 2 : 1));
                else if (r <= LAUNCH_RANGE_NM)
                    st["attack"] = Math.Min(100.0, N(st, "attack") + 0.05 * dt);
                if (N(st, "attack") >= 100 && Py.Int(st["launched"]) < 2)
                {
                    Launch(c.id, r, 2);
                    st["attack"] = 40.0;
                }
            }

            _world = world;
            if (_last_heading.HasValue && Math.Abs(Mod360(s.heading - _last_heading.Value + 180) - 180) >= 0.8 * dt)
                _turn_age_s = 0.0;
            else
                _turn_age_s += dt;
            _last_heading = s.heading;
            foreach (Dictionary<string, object> m in inbound.ToList())
            {
                double prev = N(m, "range");
                // Weave: the bearing drifts on a sine (an S-turn / curving approach), harder in the terminal phase.
                double kn = Py.Num(Py.Get(m, "speed_kn", INBOUND_KN));
                if (Py.Get(m, "bearing") != null)
                {
                    m["phase"] = Py.Num(Py.Get(m, "phase", 0.0)) + dt * Py.Num(Py.Get(m, "w", 0.0));
                    double amp = Py.Num(Py.Get(m, "weave_deg_s", 0.0)) * (prev < 4 ? 2.0 : 1.0);
                    m["bearing"] = Mod360(N(m, "bearing") + amp * Math.Cos(N(m, "phase")) * dt / Math.Max(1.0, prev));
                }
                if (S(m, "kind") == "aircraft")
                {
                    StepAircraft(m, dt);
                    continue;
                }
                m["range"] = prev - kn * dt / 3600;
                if (B(m, "detected"))
                    SoftKill(m, prev);
                if (inbound.Contains(m) && N(m, "range") <= CIWS_NM && B(m, "detected") && !B(m, "ciws"))
                {
                    m["ciws"] = true;
                    if (B(nodes["CIWS"], "active") && Ok("CIWS"))
                    {
                        if (rng.NextDouble() < DEFENSES["CIWS"])
                        {
                            Splash(m, "CIWS");
                            continue;
                        }
                        Event("critical", "defense", string.Format("TAO: CIWS engaged {0}, LEAKER", m["id"]));
                    }
                }
                if (inbound.Contains(m) && N(m, "range") <= 0.2)
                {
                    inbound.Remove(m);
                    Resolve(m);
                }
            }
            StepTorpedoes(dt, world);
            foreach (KeyValuePair<string, int> kv in held.ToList())
            {
                string tid = kv.Key;
                int rounds = kv.Value;
                Dictionary<string, object> m = inbound.FirstOrDefault(x => (string)x["id"] == tid);
                if (m == null)
                    held.Remove(tid);
                else if (N(m, "range") <= SM2_MAX_NM)
                {
                    held.Remove(tid);
                    if (Ok("ADS") && magazine >= rounds)
                        Engage(tid, rounds);
                }
            }
            foreach (Dictionary<string, object> it in interceptors.ToList())
            {
                if ((S(it, "weapon") ?? "SM-2") != "SM-2")
                {
                    StepStrike(it, dt, world);
                    continue;
                }
                Dictionary<string, object> tgt = inbound.FirstOrDefault(m => (string)m["id"] == (string)it["target"]);
                if (tgt == null)
                {
                    interceptors.Remove(it);
                    Event("info", "defense", string.Format("TAO: {0} lost its target (already killed), self-destructed", it["id"]));
                    continue;
                }
                it["range"] = N(it, "range") + SM2_KN * dt / 3600;
                // Curve to attack: the missile leaves at an offset bearing and turns onto the target as it closes.
                double frac = Math.Min(1.0, N(it, "range") / Math.Max(0.1, N(tgt, "range")));
                it["bearing"] = Mod360(N(tgt, "bearing") + Py.Num(Py.Get(it, "offset", 0.0)) * (1 - frac) * (1 - frac));
                if (N(it, "range") >= N(tgt, "range"))
                {
                    interceptors.Remove(it);
                    if (rng.NextDouble() < SM2_PK)
                        Splash(tgt, (string)it["id"]);
                    else
                        Event("critical", "defense", string.Format("TAO: {0} MISSED {1}", it["id"], tgt["id"]));
                }
            }
            foreach (Dictionary<string, object> sp in splashes.ToList())
            {
                sp["age_s"] = N(sp, "age_s") + dt;
                if (N(sp, "age_s") > 25)
                    splashes.Remove(sp);
            }
            DetectInbound();

            if (inbound.Count > 0 || torpedoes.Count > 0)
                _defense_idle_s = 0.0;
            else
            {
                _defense_idle_s += dt;
                if (_defense_idle_s > 60 && DEFENSES.Keys.Any(d => B(nodes[d], "active")))
                {
                    foreach (string d in DEFENSES.Keys)
                        nodes[d]["active"] = false;
                    Event("info", "defense", "No inbound threats for 60 s: defenses back to standby");
                }
            }
        }

        // Inbounds are only DETECTED (and reported, shown, given to agents) once inside the 20 nm COP. Each missile
        // weaves on its own (random amplitude and period), so a salvo spreads out and curves in instead of flying a ray.
        public void Launch(string hostile_id, double range_nm, int count, double? bearing = null)
        {
            Dictionary<string, object> st = StateFor(hostile_id, 100.0, true, true, 99.0);
            for (int i = 0; i < count; i++)
            {
                st["launched"] = Py.Int(st["launched"]) + 1;
                double b0 = bearing.HasValue ? bearing.Value : rng.NextDouble() * 360;
                inbound.Add(Py.D("id", hostile_id + "-V" + Py.Str(st["launched"]), "from", hostile_id, "detected", false, "kind", "missile",
                                 "range", range_nm + i * 0.4, "bearing", Mod360(b0 + (i - (count - 1) / 2.0) * 4), "speed_kn", INBOUND_KN,
                                 "weave_deg_s", 3 + rng.NextDouble() * 6, "w", 0.04 + rng.NextDouble() * 0.06, "phase", rng.NextDouble() * 6.28));
            }
            DetectInbound();
        }

        // A strike aircraft: flies in on a curving path, fires its missiles at AIRCRAFT_LAUNCH_NM, then turns away and
        // leaves the picture. It is a target for SM-2 and CIWS like a missile, but never hits the ship itself.
        public void LaunchAircraft(int count, double? bearing = null)
        {
            for (int i = 0; i < count; i++)
            {
                _aircraft += 1;
                double b0 = bearing.HasValue ? bearing.Value + i * 25 : rng.NextDouble() * 360;
                inbound.Add(Py.D("id", "AC-" + _aircraft, "from", "AC-" + _aircraft, "detected", false, "kind", "aircraft",
                                 "range", 30.0 + i * 2, "bearing", Mod360(b0), "speed_kn", AIRCRAFT_KN, "leg", "in", "missiles", 2,
                                 "weave_deg_s", 20 + rng.NextDouble() * 20, "w", 0.02 + rng.NextDouble() * 0.03, "phase", rng.NextDouble() * 6.28));
            }
            DetectInbound();
        }
        int _aircraft;

        void StepAircraft(Dictionary<string, object> a, double dt)
        {
            double kn = Py.Num(Py.Get(a, "speed_kn", AIRCRAFT_KN));
            if (S(a, "leg") == "in")
            {
                a["range"] = N(a, "range") - kn * dt / 3600;
                if (N(a, "range") <= AIRCRAFT_LAUNCH_NM)
                {
                    int n = Py.Int(Py.Get(a, "missiles", 2));
                    Launch((string)a["id"], N(a, "range"), n, N(a, "bearing"));
                    Event("critical", "defense", string.Format("TAO: aircraft {0} launched {1} missiles at {2} nm and is turning away",
                                                              a["id"], n, Py.F(N(a, "range"), 1)));
                    a["leg"] = "out";
                }
            }
            else
            {
                a["range"] = N(a, "range") + kn * dt / 3600;
                a["bearing"] = Mod360(N(a, "bearing") + 6 * dt / Math.Max(1.0, N(a, "range")));   // banking away
                if (N(a, "range") > 24)
                    inbound.Remove(a);
            }
        }

        // A spread of torpedoes from a submarine nobody has seen (random bearing), up to 4 at once.
        public void LaunchTorpedoes(int count, double range_nm, double? bearing = null)
        {
            _ghost += 1;
            double b0 = bearing.HasValue ? bearing.Value : rng.NextDouble() * 360;
            for (int k = 0; k < count; k++)
                torpedoes.Add(Py.D("id", "SUB" + _ghost + "-T" + (k + 1), "from", "SUB" + _ghost, "range", range_nm + k * 0.15,
                                   "bearing", Mod360(b0 + (k - (count - 1) / 2.0) * 6), "run_left", TORP_RUN_NM, "nixie_tried", false));
            Event("critical", "asw", string.Format("TORPEDO, TORPEDO, TORPEDO: {0} torpedoes in the water, bearing {1}, {2} nm",
                                                  count, F03(b0), Py.F(range_nm, 1)));
        }
        int _ghost;

        void DetectInbound()
        {
            List<Dictionary<string, object>> fresh = inbound.Where(m => !B(m, "detected") && N(m, "range") <= COP_RANGE_NM).ToList();
            foreach (Dictionary<string, object> m in fresh)
                m["detected"] = true;
            if (fresh.Count > 0)
            {
                Dictionary<string, object> first = fresh[0];
                foreach (Dictionary<string, object> m in fresh) if (N(m, "range") < N(first, "range")) first = m;
                double eta = N(first, "range") / INBOUND_KN * 3600;
                string brg = Py.Get(first, "bearing") != null ? ", bearing " + F03(first["bearing"]) : "";
                Event("critical", "defense", string.Format("VAMPIRE, VAMPIRE, VAMPIRE: {0} inbound{1}, {2} nm, impact in ~{3} s",
                                                          fresh.Count, brg, Py.F(N(first, "range"), 1), Py.F(eta, 0)));
            }
        }

        public List<Dictionary<string, object>> InboundOnCop()
        {
            return inbound.Where(m => B(m, "detected")).ToList();
        }

        // Every weapon coming at us that the watch can see: missiles on the COP and torpedoes in the water.
        public List<Dictionary<string, object>> ThreatsOnCop()
        {
            List<Dictionary<string, object>> l = InboundOnCop();
            l.AddRange(torpedoes);
            return l;
        }

        // ---- undersea
        void StepSub(Contact c, OwnShip s, double dt)
        {
            Dictionary<string, object> st = StateFor(c.id, 0.0, false, true, 0.0);
            double brg_to_own = Mod360(Math.Atan2(s.x - c.x, s.y - c.y) * 180.0 / Math.PI);
            c.course = c.range_nm > SUB_ATTACK_NM - 1 ? brg_to_own : Mod360(brg_to_own + 100);
            if (c.hidden && c.range_nm <= SUB_DETECT_NM)
            {
                c.hidden = false;
                c.classification = "SUSPECT";
                Event("critical", "asw", string.Format("SONAR: submerged contact {0}, bearing {1}, {2} nm, classified SUSPECT SUBMARINE",
                                                      c.id, F03(c.bearing), Py.F(c.range_nm, 1)));
            }
            if (!c.hidden)
                st["tracked_s"] = N(st, "tracked_s") + dt;
            if (c.range_nm <= SUB_ATTACK_NM)
                st["attack"] = Math.Min(100.0, N(st, "attack") + 1.2 * dt);
            if (N(st, "attack") >= 100 && Py.Int(st["launched"]) < 2)
            {
                st["attack"] = 0.0;
                int salvo = Py.Int(Py.Get(st, "salvo", 2));
                for (int k = 0; k < salvo; k++)
                {
                    st["launched"] = Py.Int(st["launched"]) + 1;
                    torpedoes.Add(Py.D("id", c.id + "-T" + Py.Str(st["launched"]), "from", c.id, "range", c.range_nm,
                                       "bearing", Mod360(c.bearing + (k - (salvo - 1) / 2.0) * 6), "run_left", TORP_RUN_NM, "nixie_tried", false));
                }
                c.hidden = false;
                c.classification = "HOSTILE";
                Event("critical", "asw", string.Format("TORPEDO, TORPEDO, TORPEDO: {3} torpedoes in the water, bearing {0}, {1} nm, from {2} (HOSTILE)",
                                                      F03(c.bearing), Py.F(c.range_nm, 1), c.id, salvo));
            }
        }

        void StepTorpedoes(double dt, World world)
        {
            OwnShip s = world.ship;
            foreach (Dictionary<string, object> t in torpedoes.ToList())
            {
                // Closing speed: torpedo speed plus own ship's speed TOWARD the torpedo (negative when running away).
                double toward = s.speed * Math.Cos((s.heading - N(t, "bearing")) * Math.PI / 180.0);
                double prev = N(t, "range");
                t["range"] = prev - (TORP_KN + toward) * dt / 3600;
                t["run_left"] = N(t, "run_left") - TORP_KN * dt / 3600;
                if (prev > NIXIE_NM && NIXIE_NM >= N(t, "range") && !B(t, "nixie_tried"))
                {
                    t["nixie_tried"] = true;
                    if (nixie && Ok("CMD") && rng.NextDouble() < NIXIE_PK)
                    {
                        torpedoes.Remove(t);
                        splashes.Add(Py.D("range", t["range"], "bearing", t["bearing"], "label", "SEDUCED", "age_s", 0));
                        Event("info", "asw", string.Format("TAO: {0} seduced by Nixie, passing astern", t["id"]));
                        continue;
                    }
                }
                if (N(t, "run_left") <= 0)
                {
                    torpedoes.Remove(t);
                    splashes.Add(Py.D("range", t["range"], "bearing", t["bearing"], "label", "OUT OF FUEL", "age_s", 0));
                    Event("info", "asw", string.Format("TAO: {0} ran out of fuel {1} nm short, sinking", t["id"], Py.F(N(t, "range"), 1)));
                    continue;
                }
                if (N(t, "range") <= 0.1)
                {
                    torpedoes.Remove(t);
                    torp_hits += 1;
                    hits += 1;
                    string comp = FLOOD_TARGETS[torp_hits % FLOOD_TARGETS.Length];
                    Event("critical", "asw", string.Format("TORPEDO HIT: {0} under {1}", t["id"], COMP[comp]["name"]));
                    AddCasualty(comp, "flood", 0.8, null, "torpedo hit", true);
                }
            }
        }

        // Harpoon / ASROC flying at a contact. Hits when it reaches the contact's range.
        void StepStrike(Dictionary<string, object> it, double dt, World world)
        {
            Contact c = GetKey(world.contacts, it["target"]);
            if (c == null)
            {
                interceptors.Remove(it);
                return;
            }
            string weapon = (string)it["weapon"];
            it["range"] = N(it, "range") + Py.Num(WEAPONS[weapon]["kn"]) * dt / 3600;
            it["bearing"] = c.bearing;
            if (N(it, "range") >= c.range_nm)
            {
                interceptors.Remove(it);
                if (rng.NextDouble() < Py.Num(WEAPONS[weapon]["pk"]))
                {
                    world.RemoveContact(c.id);
                    splashes.Add(Py.D("range", c.range_nm, "bearing", c.bearing, "label", "KILL", "age_s", 0));
                    Event("info", "defense", string.Format("TAO: {0} ({1}) KILLED {2}", it["id"], weapon, c.id));
                }
                else
                    Event("critical", "defense", string.Format("TAO: {0} ({1}) MISSED {2}", it["id"], weapon, c.id));
            }
        }

        // (weapon, thing) for a target id: an inbound missile -> SM-2 (thing is its dict), a contact -> Harpoon / ASROC
        // (thing is the Contact). (null, null) when there is no such target.
        public string WeaponFor(object target, out object thing)
        {
            string id = target as string;
            Dictionary<string, object> m = id == null ? null : InboundOnCop().FirstOrDefault(x => (string)x["id"] == id);
            if (m != null)
            {
                thing = m;
                return "SM-2";
            }
            Contact c = _world != null ? GetKey(_world.contacts, target) : null;
            if (c != null && !c.hidden)
            {
                thing = c;
                return c.domain == "subsurface" ? "ASROC" : "HARPOON";
            }
            thing = null;
            return null;
        }

        // CEASE FIRE: every own weapon in flight is ordered to self-destruct.
        public int AbortWeapons()
        {
            int n = interceptors.Count + held.Count;
            held.Clear();
            foreach (Dictionary<string, object> it in interceptors)
            {
                splashes.Add(Py.D("range", it["range"], "bearing", Py.Get(it, "bearing"), "label", "ABORTED", "age_s", 0));
                Event("critical", "defense", string.Format("CEASE FIRE: {0} ({1}) destruct", it["id"], S(it, "weapon") ?? "SM-2"));
            }
            interceptors.Clear();
            return n;
        }

        public bool IsActive(string d)
        {
            return d == "NIXIE" ? nixie : B(nodes[d], "active");
        }

        public double PHit()
        {
            double p = 1.0;
            foreach (KeyValuePair<string, double> kv in DEFENSES)
                if (B(nodes[kv.Key], "active") && Ok(kv.Key))
                    p *= 1 - kv.Value;
            return p;
        }

        // Jamming (ECM) and decoys get one chance each to seduce an inbound as it crosses their range.
        void SoftKill(Dictionary<string, object> m, double prev_range)
        {
            string[] names = { "ECM", "CMD" };
            double[] ranges = { ECM_NM, DECOY_NM };
            for (int i = 0; i < 2; i++)
            {
                string name = names[i];
                double rng_nm = ranges[i];
                if (prev_range > rng_nm && rng_nm >= N(m, "range") && B(nodes[name], "active") && Ok(name) && inbound.Contains(m))
                {
                    if (rng.NextDouble() < DEFENSES[name])
                    {
                        inbound.Remove(m);
                        string what = name == "ECM" ? "SLQ-32 jamming" : "decoy";
                        splashes.Add(Py.D("range", m["range"], "bearing", Py.Get(m, "bearing"), "label", "SEDUCED", "age_s", 0));
                        Event("info", "defense", string.Format("TAO: {0} seduced by {1}, missing astern", m["id"], what));
                    }
                }
            }
        }

        void Splash(Dictionary<string, object> m, string by)
        {
            if (inbound.Contains(m))
                inbound.Remove(m);
            splashes.Add(Py.D("range", m["range"], "bearing", Py.Get(m, "bearing"), "label", "SPLASH", "age_s", 0));
            Event("info", "defense", string.Format("TAO: SPLASH {0}, killed by {1} at {2} nm", m["id"], by, Py.F(N(m, "range"), 1)));
        }

        public bool Evasive()
        {
            OwnShip s = _world != null ? _world.ship : null;
            return s != null && s.speed >= 20 && _turn_age_s <= 60;
        }

        public Dictionary<string, object> Engage(string target, int rounds)
        {
            object thing;
            string weapon = WeaponFor(target, out thing);
            List<object> fired = new List<object>();
            if (weapon != null && WEAPONS.ContainsKey(weapon))
            {
                Contact c = (Contact)thing;
                for (int k = 0; k < rounds; k++)
                {
                    _sm += 1;
                    mags[weapon] -= 1;
                    Dictionary<string, object> w = Py.D("id", weapon.Substring(0, 3) + "-" + _sm, "weapon", weapon, "target", target, "range", 0.0,
                                                        "bearing", c.bearing);
                    interceptors.Add(w);
                    fired.Add(w["id"]);
                }
                Event("critical", "defense", string.Format("{0}: {1}x {2} at {3}, {4} nm", weapon == "ASROC" ? "WEAPON AWAY" : "BIRDS AWAY",
                                                          rounds, weapon, target, Py.F(c.range_nm, 1)));
                return Py.D("fired", fired);
            }
            Dictionary<string, object> m = (Dictionary<string, object>)thing;
            for (int k = 0; k < rounds; k++)
            {
                _sm += 1;
                magazine -= 1;
                Dictionary<string, object> sm = Py.D("id", "SM2-" + _sm, "weapon", "SM-2", "target", target, "range", 0.0, "bearing", Py.Get(m, "bearing"),
                                                     "offset", (k % 2 == 0 ? 1 : -1) * (25 + 10 * (k / 2)));
                interceptors.Add(sm);
                fired.Add(sm["id"]);
            }
            double tti = N(m, "range") / (SM2_KN + Py.Num(Py.Get(m, "speed_kn", INBOUND_KN))) * 3600;
            Event("critical", "defense", string.Format("BIRDS AWAY: {0}x SM-2 ({1}) at {2}, {3} nm, intercept in ~{4} s",
                                                      rounds, Py.Join(", ", fired), target, Py.F(N(m, "range"), 1), Py.F(tti, 0)));
            return Py.D("fired", fired);
        }

        void Resolve(Dictionary<string, object> m)
        {
            double p = 1.0 - (Evasive() ? EVASIVE_MISS : 0.0);
            if (rng.NextDouble() < p)
            {
                hits += 1;
                string comp = HIT_TARGETS[_next_target % HIT_TARGETS.Length];
                _next_target += 1;
                Dictionary<string, object> c = COMP[comp];
                int[] fr = Fr(comp);
                Event("critical", "defense", string.Format("WE'VE BEEN HIT: {0} impact frame {1}, {2} (hit chance was {3}%)",
                                                          m["id"], (fr[0] + fr[1]) / 2, c["name"], Py.F(p * 100, 0)));
                AddCasualty(comp, "fire", 0.85, null, "weapon impact", true);
            }
            else
            {
                splashes.Add(Py.D("range", 0.3, "bearing", Py.Get(m, "bearing"), "label", "MISS", "age_s", 0));
                Event("info", "defense", string.Format("TAO: {0} MISSED the ship (evasive maneuvering)", m["id"]));
            }
        }

        // Nearest hostile outside the EMCON radius: minutes until it's inside, and detection risk by then if we keep radiating.
        public Dictionary<string, object> PredictEmcon(World world)
        {
            Dictionary<string, object> best = null;
            foreach (Contact c in world.contacts.Values)
            {
                if (c.classification != "SUSPECT" && c.classification != "HOSTILE")     // only what CIC has classified
                    continue;
                Dictionary<string, object> st;
                double detect = hostile_state.TryGetValue(c.id, out st) ? N(st, "detect") : 0.0;
                double closing = 32.0 + 8.0;
                double mins = Math.Max(0.0, (c.range_nm - EMCON_RADIUS_NM) / closing * 60);
                double r = c.range_nm, risk = detect, t = 0.0;
                while (r > EMCON_RADIUS_NM && t < 3600)
                {
                    risk = Math.Min(100.0, risk + 0.35 * Math.Max(0.0, (DETECT_FROM_NM - r) / 10) * 10);
                    r -= closing * 10 / 3600;
                    t += 10;
                }
                Dictionary<string, object> item = Py.D("hostile", c.id, "range", Py.Round(c.range_nm, 1), "minutes_to_radius", Py.Round(mins, 1),
                                                       "risk_now", RoundI(detect), "risk_at_radius", RoundI(risk));
                if (best == null || N(item, "minutes_to_radius") < N(best, "minutes_to_radius"))
                    best = item;
            }
            return best;
        }

        // ------------------------------------------------------------------ governed actions
        public static readonly string[] SYSTEM_ACTIONS = { "set_emcon", "activate_defense", "close_breaker", "open_breaker", "dispatch_team", "set_gq",
                                                           "engage_target", "rig_casualty_power" };
        public static readonly Dictionary<string, string> SAFE_DEFAULTS = new Dictionary<string, string> {
            { "rig_casualty_power", "No cable rigged (team stays at its station)" },
            { "set_emcon", "Keep current emissions posture" },
            { "activate_defense", "Do not activate (defenses stay in standby)" },
            { "close_breaker", "Leave breaker open" },
            { "open_breaker", "Leave breaker closed" },
            { "dispatch_team", "Team stays at its station" },
            { "set_gq", "Remain in Condition III" },
            { "engage_target", "Hold fire (no launch)" },
        };

        public string Current(string action, Dictionary<string, object> @params)
        {
            if (action == "engage_target")
            {
                object thing;
                string weapon = WeaponFor(Py.Get(@params, "target"), out thing);
                if (weapon == "SM-2")
                    return string.Format("{0} at {1} nm, SM-2 magazine {2}", Py.Str(Py.Get(@params, "target")), Py.F(N(M(thing), "range"), 1), magazine);
                if (weapon != null)
                    return string.Format("{0} at {1} nm, {2} magazine {3}", Py.Str(Py.Get(@params, "target")), Py.F(((Contact)thing).range_nm, 1), weapon, mags[weapon]);
                return "target gone";
            }
            if (action == "set_gq")
                return gq ? "General Quarters" : "Condition III (normal watch)";
            if (action == "set_emcon")
                return emcon ? "EMCON SET" : "EMCON OFF (radiating)";
            if (action == "activate_defense")
            {
                List<string> parts = new List<string>();
                foreach (object d in Py.ListOrEmpty(Py.Get(@params, "systems")))
                    parts.Add(string.Format("{0} {1}", Py.Str(d), IsActive(Py.Str(d)) ? "active" : "standby"));
                return string.Join(", ", parts);
            }
            if (action == "close_breaker" || action == "open_breaker")
            {
                Dictionary<string, object> b = GetKey(breakers, Py.Get(@params, "breaker")) ?? new Dictionary<string, object>();
                return string.Format("{0} {1}", Py.Str(Py.Get(@params, "breaker")), Py.Truthy(Py.Get(b, "closed")) ? "closed" : "open");
            }
            if (action == "dispatch_team")
            {
                Dictionary<string, object> t = GetKey(teams, Py.Get(@params, "team")) ?? new Dictionary<string, object>();
                return string.Format("{0} {1} at frame {2}", Py.Str(Py.Get(t, "name")), Py.Str(Py.Get(t, "status")), Py.F(Py.Num(Py.Get(t, "pos", 0)), 0));
            }
            if (action == "rig_casualty_power")
            {
                Dictionary<string, object> n = GetKey(nodes, Py.Get(@params, "equipment")) ?? new Dictionary<string, object>();
                return string.Format("{0} {1}", Py.Str(Py.Get(@params, "equipment")), B(n, "powered") ? "powered" : "NO POWER");
            }
            return "";
        }

        // Why this action can't be done right now, or None.
        public string Validate(string action, Dictionary<string, object> @params)
        {
            if ((action == "close_breaker" || action == "open_breaker") && GetKey(breakers, Py.Get(@params, "breaker")) == null)
                return "Unknown breaker " + Py.Str(Py.Get(@params, "breaker"));
            if (action == "set_emcon" && !(Py.Get(@params, "on") is bool))
                return "set_emcon needs on: true/false";
            if (action == "activate_defense")
            {
                object sv = Py.Get(@params, "systems");
                List<object> systems = Py.Truthy(sv) ? Py.ListOrEmpty(sv) : new List<object>();
                List<object> bad = systems.Where(d => { string ds = d as string; return ds == null || (!DEFENSES.ContainsKey(ds) && ds != "NIXIE"); }).ToList();
                if (bad.Count > 0 || systems.Count == 0)
                    return "Unknown defense " + Py.Repr(bad.Count > 0 ? bad : systems);
                List<string> dead = systems.Select(d => (string)d).Where(d => !Ok(d == "NIXIE" ? "CMD" : d)).ToList();
                if (dead.Count > 0)
                    return string.Join(", ", dead) + " not available (no power or damaged)";
            }
            if (action == "engage_target")
            {
                object rv = Py.Get(@params, "rounds", 2);
                int rounds = Py.Truthy(rv) ? Py.Int(rv) : 2;
                object target = Py.Get(@params, "target");
                object thing;
                string weapon = WeaponFor(target, out thing);
                if (weapon != null && WEAPONS.ContainsKey(weapon))
                {
                    Contact c = (Contact)thing;
                    Dictionary<string, object> w = WEAPONS[weapon];
                    if (c.classification != "HOSTILE")
                        return string.Format("{0} is {1}, not HOSTILE: rules of engagement forbid firing", c.id, c.classification);
                    if (!(Py.Num(w["min"]) <= c.range_nm && c.range_nm <= Py.Num(w["max"])))
                        return string.Format("{0} at {1} nm is outside {2} range {3}-{4} nm", c.id, Py.F(c.range_nm, 1), weapon, Py.Str(w["min"]), Py.Str(w["max"]));
                    if (!Ok("ADS"))
                        return "Mk 41 VLS not available (no power or damaged)";
                    if (interceptors.Any(i => (string)i["target"] == c.id))
                        return string.Format("{0} already has a {1} in flight", c.id, weapon);
                    if (!(1 <= rounds && rounds <= 4) || rounds > mags[weapon])
                        return string.Format("Rounds must be 1-4 and within {0} magazine ({1})", weapon, mags[weapon]);
                    return null;
                }
                string tid = target as string;
                Dictionary<string, object> m = tid == null ? null : InboundOnCop().FirstOrDefault(x => (string)x["id"] == tid);
                if (m == null)
                    return string.Format("No inbound or HOSTILE track {0} on the COP", Py.Str(target));
                if (!Ok("ADS"))
                    return "Mk 41 VLS / SM-2 not available (no power or damaged)";
                if (N(m, "range") < SM2_MIN_NM)
                    return string.Format("{0} at {1} nm is inside SM-2 minimum range {2} nm (CIWS only)", m["id"], Py.F(N(m, "range"), 1), Py.Str(SM2_MIN_NM));
                if (held.ContainsKey((string)m["id"]))
                    return string.Format("{0} is already released, held for the SM-2 envelope", m["id"]);
                if (interceptors.Any(i => (string)i["target"] == (string)m["id"]))
                    return string.Format("{0} already has SM-2 in flight", m["id"]);
                if (!(1 <= rounds && rounds <= 4) || rounds > magazine)
                    return string.Format("Rounds must be 1-2 and within magazine ({0})", magazine);
            }
            if (action == "dispatch_team")
            {
                Dictionary<string, object> t = GetKey(teams, Py.Get(@params, "team"));
                if (t == null || GetKey(casualties, Py.Get(@params, "casualty")) == null)
                    return "Unknown team or casualty";
                string st = (string)t["status"];
                if (st == "unmanned")
                    return string.Format("{0} is not manned (General Quarters not set)", t["name"]);
                if (st == "manning")
                    return string.Format("{0} is still manning up", t["name"]);
                if (st != "standby" && st != "returning")
                    return string.Format("{0} is busy ({1})", t["name"], st);
                Dictionary<string, object> cas = casualties[(string)@params["casualty"]];
                if (Py.Truthy(cas["team"]) && (string)cas["team"] != (string)t["id"])
                    return string.Format("{0} already has {1} assigned", cas["id"], teams[(string)cas["team"]]["name"]);
                if ((string)cas["status"] == "out" || (string)cas["status"] == "repaired")
                    return string.Format("{0} is already {1}", cas["id"], cas["status"]);
            }
            if (action == "rig_casualty_power")
            {
                Dictionary<string, object> t = GetKey(teams, Py.Get(@params, "team"));
                Dictionary<string, object> eq = GetKey(nodes, Py.Get(@params, "equipment")), src = GetKey(nodes, Py.Get(@params, "source"));
                if (t == null || eq == null || src == null)
                    return "Unknown team, equipment or source";
                string st = (string)t["status"];
                if (st != "standby" && st != "returning")
                    return string.Format("{0} is not available ({1})", t["name"], st);
                string ty = (string)eq["type"];
                if (ty == "gen" || ty == "swbd" || ty == "lc")
                    return "Casualty power cables feed equipment, not a bus";
                if (B(eq, "failed"))
                    return Py.Str(@params["equipment"]) + " is damaged: a cable cannot restore it (repair first)";
                if (B(eq, "powered"))
                    return Py.Str(@params["equipment"]) + " already has power";
                if ((string)src["type"] != "lc" && (string)src["type"] != "swbd")
                    return "Source must be a load center or switchboard";
                if (!B(src, "powered") || B(src, "failed"))
                    return Py.Str(@params["source"]) + " is not live";
                if (breakers.ContainsKey("CP-" + Py.Str(@params["equipment"])))
                    return "A casualty power cable to " + Py.Str(@params["equipment"]) + " is already rigged or being rigged";
            }
            return null;
        }

        // ------------------------------------------------------------------ casualty power (patch cables)
        // A repair team runs a temporary cable from a live load center / switchboard to equipment that lost its feed.
        // It is a breaker named CP-<equipment>, normally open, that closes when the team finishes rigging (RIG_S).
        public const double RIG_S = 30.0;

        public Dictionary<string, object> RigCasualtyPower(string team_id, string equipment, string source)
        {
            Dictionary<string, object> t = teams[team_id];
            Dictionary<string, object> prev = Py.D("team", team_id, "status", t["status"], "pos", t["pos"], "breaker", "CP-" + equipment);
            string bid = "CP-" + equipment;
            breakers[bid] = Py.D("id", bid, "a", source, "b", equipment, "closed", false, "normal", false,
                                 "casualty_power", true, "team", team_id, "rig_s", 0.0);
            int[] fr = Fr((string)nodes[equipment]["comp"]);
            t["status"] = "en route";
            t["target"] = (fr[0] + fr[1]) / 2.0;
            t["casualty"] = null;
            t["job"] = bid;
            t["boundaries"] = null;
            Event("info", "damage-control", string.Format("DCA: {0} away to rig casualty power {1} -> {2}", t["name"], source, equipment));
            return prev;
        }

        // moveTeams: the DC picture is hidden (its own step moves teams when it runs), so move the rigging teams here.
        void StepCasualtyPower(double dt, bool moveTeams)
        {
            foreach (Dictionary<string, object> b in breakers.Values.Where(x => B(x, "casualty_power") && !B(x, "closed")).ToList())
            {
                Dictionary<string, object> t = GetKey(teams, b["team"]);
                if (t == null || Py.Str(Py.Get(t, "job")) != Py.Str(b["id"])) continue;
                if (moveTeams && (string)t["status"] == "en route" && t["target"] != null)
                {
                    double d = N(t, "target") - N(t, "pos"), step = RUN_FT_PER_S * dt * real_per_sim;
                    if (Math.Abs(d) <= step) { t["pos"] = N(t, "target"); t["status"] = "on scene"; }
                    else t["pos"] = N(t, "pos") + (d < 0 ? -step : step);
                }
                if ((string)t["status"] != "on scene") continue;
                b["rig_s"] = N(b, "rig_s") + dt;
                if (N(b, "rig_s") < RIG_S) continue;
                b["closed"] = true;
                Flow();
                t["status"] = "returning";
                t["target"] = Py.Num(t["home"]);
                t["job"] = null;
                Event("info", "ship-systems", string.Format("EOOW: casualty power rigged, {0} -> {1}: {2} {3}", b["a"], b["b"], b["b"],
                    Ok((string)b["b"]) ? "back on line" : "still without power"));
            }
        }

        // Carry out an approved action. Returns its inverse, for rollback.
        public Dictionary<string, object> Apply(string action, Dictionary<string, object> @params)
        {
            if (action == "set_emcon")
            {
                bool prev = emcon;
                emcon = Py.Truthy(@params["on"]);
                Event("info", "ew", emcon ? "EMCON SET: radiating emitters silenced" : "EMCON lifted");
                return Py.D("on", prev);
            }
            if (action == "activate_defense")
            {
                List<object> newly = Py.ListOrEmpty(@params["systems"]).Where(d => !IsActive((string)d)).ToList();
                foreach (object o in newly)
                {
                    string d = (string)o;
                    if (d == "NIXIE")
                    {
                        nixie = true;
                        Event("info", "asw", "TAO: Nixie streamed");
                    }
                    else
                        nodes[d]["active"] = true;
                }
                return Py.D("deactivate", newly);
            }
            if (action == "close_breaker" || action == "open_breaker")
            {
                bool prev = SetBreaker((string)@params["breaker"], action == "close_breaker");
                return Py.D("breaker", @params["breaker"], "closed", prev);
            }
            if (action == "dispatch_team")
                return Dispatch((string)@params["team"], (string)@params["casualty"]);
            if (action == "rig_casualty_power")
                return RigCasualtyPower((string)@params["team"], (string)@params["equipment"], (string)@params["source"]);
            if (action == "engage_target")
            {
                object thing;
                string weapon = WeaponFor(@params["target"], out thing);
                if (weapon == "SM-2" && N(M(thing), "range") > SM2_MAX_NM)
                {
                    Dictionary<string, object> m = M(thing);
                    held[(string)m["id"]] = Py.Int(Py.Get(@params, "rounds", 2));
                    Event("critical", "defense", string.Format("BATTERIES RELEASED on {0} ({1} nm): fire control will launch at {2} nm",
                                                              m["id"], Py.F(N(m, "range"), 1), Py.F(SM2_MAX_NM, 0)));
                    return Py.D("held", m["id"]);
                }
                return Engage((string)@params["target"], Py.Int(Py.Get(@params, "rounds", 2)));
            }
            if (action == "set_gq")
            {
                bool prev = gq;
                object want = Py.Get(@params, "on");
                SetGq(want == null || Py.Truthy(want));   // {"on": false} secures from GQ
                return Py.D("gq", prev);
            }
            return new Dictionary<string, object>();
        }

        public void Undo(string action, Dictionary<string, object> inverse)
        {
            if (action == "set_emcon")
                emcon = Py.Truthy(inverse["on"]);
            else if (action == "activate_defense")
            {
                foreach (object o in Py.ListOrEmpty(Py.Get(inverse, "deactivate")))
                {
                    string d = (string)o;
                    if (d == "NIXIE")
                        nixie = false;
                    else
                        nodes[d]["active"] = false;
                }
            }
            else if (action == "close_breaker" || action == "open_breaker")
                SetBreaker((string)inverse["breaker"], Py.Truthy(inverse["closed"]));
            else if (action == "set_gq")
                SetGq(Py.Truthy(Py.Get(inverse, "gq", false)));
            else if (action == "rig_casualty_power")
            {
                Dictionary<string, object> t = teams[(string)inverse["team"]];
                breakers.Remove(Py.Str(inverse["breaker"]));
                t["status"] = "returning";
                t["target"] = Py.Num(t["home"]);
                t["job"] = null;
                Flow();
            }
            else if (action == "dispatch_team")
            {
                Dictionary<string, object> t = teams[(string)inverse["team"]];
                Dictionary<string, object> cas = GetKey(casualties, t["casualty"] ?? "");
                if (cas != null)
                    cas["team"] = null;
                t["status"] = "returning";
                t["target"] = Py.Num(t["home"]);
                t["casualty"] = null;
                t["boundaries"] = null;
            }
        }

        // ------------------------------------------------------------------ step & view
        public void Step(double dt, World world, ICollection<string> active = null)
        {
            ICollection<string> on = (active != null && active.Count > 0) ? active : new HashSet<string> { "nav", "systems", "dc" };
            if (on.Contains("dc"))
                StepDc(dt);
            if (on.Contains("nav"))
                StepThreats(dt, world);
            if (on.Contains("systems") || on.Contains("dc"))
                StepCasualtyPower(dt, !on.Contains("dc"));
            Flow();
            foreach (KeyValuePair<string, int> kv in gen_load)
            {
                if (kv.Value > 100 && !_overload_alerted.Contains(kv.Key))
                {
                    _overload_alerted.Add(kv.Key);
                    Event("critical", "ship-systems", string.Format("EOOW: {0} overloaded, {1}% load", kv.Key, kv.Value));
                }
                else if (kv.Value <= 100)
                    _overload_alerted.Remove(kv.Key);
            }
        }

        public Dictionary<string, object> View(World world)
        {
            List<object> hostiles = new List<object>();
            foreach (Contact c in world.contacts.Values)
            {
                if (c.kind != "hostile") continue;
                Dictionary<string, object> h = Py.D("id", c.id, "range", Py.Round(c.range_nm, 1), "bearing", RoundI(c.bearing), "classification", c.classification);
                Dictionary<string, object> st;
                if (hostile_state.TryGetValue(c.id, out st))
                    foreach (KeyValuePair<string, object> kv in st)
                        h[kv.Key] = kv.Value is double ? (object)RoundI((double)kv.Value) : kv.Value;
                hostiles.Add(h);
            }
            List<Dictionary<string, object>> onCop = InboundOnCop();
            Dictionary<string, object> mg = Py.D("SM-2", magazine);
            foreach (KeyValuePair<string, int> kv in mags) mg[kv.Key] = kv.Value;
            Dictionary<string, object> defenses = new Dictionary<string, object>();
            foreach (KeyValuePair<string, double> kv in DEFENSES)
                defenses[kv.Key] = Py.D("active", nodes[kv.Key]["active"], "ready", Ok(kv.Key), "pk", kv.Value);
            Dictionary<string, object> restore = new Dictionary<string, object>();
            foreach (string nid in UnpoweredVital())
                restore[nid] = RestoringBreaker(nid);
            return Py.D(
                "nodes", Py.ToList(nodes.Values),
                "breakers", Py.ToList(breakers.Values),
                "compartments", COMPARTMENTS,
                "bulkheads", BULKHEADS,
                "casualties", Py.ToList(casualties.Values),
                "teams", Py.ToList(teams.Values),
                "emcon", emcon,
                "radiating", (!emcon && Ok("RADAR")) || B(nodes["ECM"], "active"),
                "emcon_radius_nm", EMCON_RADIUS_NM,
                "hostiles", hostiles,
                "interceptors", Py.ToList(interceptors.Select(i => (object)Py.D("id", i["id"], "weapon", S(i, "weapon") ?? "SM-2", "target", i["target"],
                                                                                 "range", Py.Round(N(i, "range"), 2), "bearing", i["bearing"]))),
                "torpedoes", Py.ToList(torpedoes.Select(t => (object)Py.D("id", t["id"], "from", t["from"], "range", Py.Round(N(t, "range"), 2),
                                                                           "bearing", RoundI(N(t, "bearing")), "run_left", Py.Round(N(t, "run_left"), 2), "speed_kn", TORP_KN))),
                "mags", mg,
                "nixie", nixie,
                "splashes", Py.ToList(splashes.Select(sp => { Dictionary<string, object> x = new Dictionary<string, object>(sp); x["range"] = Py.Round(N(sp, "range"), 2); return (object)x; })),
                "magazine", magazine,
                "evasive", Evasive(),
                "inbound", Py.ToList(onCop.Select(m =>
                {
                    List<object> eng = new List<object>();
                    foreach (Dictionary<string, object> i in interceptors) if ((string)i["target"] == (string)m["id"]) eng.Add(i["id"]);
                    if (held.ContainsKey((string)m["id"])) eng.Add("released");
                    double mk = Py.Num(Py.Get(m, "speed_kn", INBOUND_KN));
                    return (object)Py.D("id", m["id"], "from", m["from"], "range", Py.Round(N(m, "range"), 2), "bearing", Py.Get(m, "bearing"),
                                        "kind", S(m, "kind") ?? "missile", "speed_kn", mk, "leg", S(m, "leg"),
                                        "engaged_by", eng, "eta_s", S(m, "kind") == "aircraft" ? (object)null : RoundI(N(m, "range") / mk * 3600));
                })),
                "defenses", defenses,
                // Chance an inbound gets through: 0 when nothing is inbound. "if_attacked" is the defensive posture.
                "p_hit", onCop.Count > 0 ? Py.Round(PHit(), 2) : 0.0,
                "p_hit_if_attacked", Py.Round(PHit(), 2),
                "hits", hits,
                "propulsion_ok", PropulsionOk(),
                "shafts_ok", ShaftsOk(),
                "max_speed_kn", MaxSpeedValue(),
                "flank_speed_kn", ShaftsOk() == 2 ? SHIP["flank_speed_kn"] : MaxSpeedValue(),
                "ship", SHIP,
                "steering_ok", SteeringOk(),
                "emcon_prediction", PredictEmcon(world),
                "gq", gq,
                "firemain_ok", FiremainOk(),
                "gen_load", gen_load,
                "_restore", restore);
        }
    }
}

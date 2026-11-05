// Simulated world: own ship, contacts, engine, weather, and the scenario runner (port of warden\world.py).
//
// Coordinates are nautical miles on a local flat plane (x east, y north).
// Headings are degrees true (0 = north, clockwise). Speeds are knots (nm/h).
// All data is synthetic and seeded. No real ship data. C# 5, framework assemblies only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BridgeWarden
{
    public static class WorldMath
    {
        // Python's float % (result has the sign of the divisor).
        public static double PyMod(double a, double b) { return a - b * Math.Floor(a / b); }

        public static double Wrap360(double deg)
        {
            return PyMod(deg, 360.0);
        }

        // Signed smallest difference a - b in degrees, in (-180, 180].
        public static double AngleDiff(double a, double b)
        {
            double d = PyMod(a - b + 180.0, 360.0) - 180.0;
            return d == -180.0 ? 180.0 : d;
        }

        public static double[] Velocity(double heading, double speed)
        {
            double r = heading * Math.PI / 180.0;
            return new double[] { speed * Math.Sin(r), speed * Math.Cos(r) };
        }

        // Closest point of approach (nm) and time to it (minutes). TCPA < 0 means opening.
        public static double[] CpaTcpa(double[] own_xy, double[] own_v, double[] c_xy, double[] c_v)
        {
            double rx = c_xy[0] - own_xy[0], ry = c_xy[1] - own_xy[1];
            double vx = c_v[0] - own_v[0], vy = c_v[1] - own_v[1];
            double v2 = vx * vx + vy * vy;
            if (v2 < 1e-9)
                return new double[] { Hypot(rx, ry), 0.0 };
            double t = -(rx * vx + ry * vy) / v2;           // hours
            if (t < 0)
                return new double[] { Hypot(rx, ry), t * 60 };
            return new double[] { Hypot(rx + vx * t, ry + vy * t), t * 60 };
        }

        public static double Hypot(double x, double y) { return Math.Sqrt(x * x + y * y); }
        public static double Degrees(double rad) { return rad * 180.0 / Math.PI; }
        public static double Radians(double deg) { return deg * Math.PI / 180.0; }

        // A line of storm cells 7-13 nm ahead and to starboard, drifting toward the ship's track.
        // One isolated thunderstorm ahead of the ship, somewhere off either bow (2026-10-26: was a five-cell squall
        // line). Strong core, drifting with the wind. Kept as SquallLine for its callers.
        public static List<object> SquallLine(OwnShip ship, int rng_seed = 3)
        {
            Random rnd = new Random(rng_seed + (int)(Math.Abs(ship.x * 7 + ship.y * 13)) % 1000);
            double brg = ship.heading + (-45 + 90 * rnd.NextDouble()), r = 8 + 5 * rnd.NextDouble();
            double b = Radians(brg);
            return new List<object> { Py.D("id", "WX" + (1 + rnd.Next(99)), "x", ship.x + r * Math.Sin(b), "y", ship.y + r * Math.Cos(b),
                                           "r", 2.6 + rnd.NextDouble(), "intensity", 0.92, "course", 290, "speed", 14) };
        }
    }

    public sealed class Contact : Model
    {
        public string id;
        public double x;
        public double y;
        public double course;
        public double speed;
        public double cpa_nm = 99.0;
        public double tcpa_min = 0.0;
        public double range_nm = 0.0;
        public double bearing = 0.0;
        public string kind = "neutral";            // neutral | hostile (ground truth; what the watch knows is "classification")
        public string classification = "UNKNOWN";  // MERCHANT | FISHING | UNKNOWN | SUSPECT | HOSTILE
        public string domain = "surface";          // surface | subsurface
        public bool hidden = false;                // a submerged submarine nobody holds on sonar yet: not on the COP
    }

    public sealed class OwnShip : Model
    {
        public double x = 0.0;
        public double y = 0.0;
        public double heading = 90.0;
        public double speed = 16.0;
        public double cmd_heading = 90.0;
        public double cmd_speed = 16.0;
        public double engine_temp = 84.0;
        public double fault_heat = 0.0;
        public double power_margin_kw = 0.0;
        public double fuel_rate = 0.0;
    }

    public sealed class Environment : Model
    {
        public double wind_kts = 12.0;
        public double visibility_nm = 8.0;
        public int sea_state = 2;
        public double wave_height_m = 0.8;
        public double wind_from = 200.0;
        // Weather-radar rain cells, world coordinates: {id, x, y, r (nm), intensity 0-1, course, speed}
        public List<object> cells = new List<object>();

        // model_copy(update=...): shallow copy with the given fields replaced (unknown keys ignored).
        public Environment ModelCopy(Dictionary<string, object> update)
        {
            Environment e = (Environment)MemberwiseClone();
            if (update == null) return e;
            foreach (KeyValuePair<string, object> kv in update)
            {
                switch (kv.Key)
                {
                    case "wind_kts": e.wind_kts = Py.Num(kv.Value); break;
                    case "visibility_nm": e.visibility_nm = Py.Num(kv.Value); break;
                    case "sea_state": e.sea_state = Py.Int(kv.Value); break;
                    case "wave_height_m": e.wave_height_m = Py.Num(kv.Value); break;
                    case "wind_from": e.wind_from = Py.Num(kv.Value); break;
                    case "cells": e.cells = Py.ListOrEmpty(kv.Value); break;
                }
            }
            return e;
        }
    }

    public class World
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const double TURN_RATE_DPS = 1.0;       // degrees per sim second
        public const double ACCEL_KN_PS = 0.3;         // knots per sim second: 16 to 30 kn in about 45 s (gas turbines)
        public const double DECEL_KN_PS = 0.25;        // slowing (backing down helps): 30 to 0 kn in about 2 min
        public const double ROUTE_HEADING = 90.0;      // planned track: due east along y = 0

        public double time_scale;
        public double base_scale;      // the 1x time scale (time_scale = base_scale x presenter speed)
        public double sim_time;
        public OwnShip ship;
        public Environment env;
        // Insertion-ordered like a Python dict. Remove contacts only through RemoveContact (it keeps the order right).
        public Dictionary<string, Contact> contacts;
        public string phase;
        public ShipSystems sys;
        int _tn;

        public World(double time_scale = 10.0)
        {
            this.time_scale = time_scale;
            base_scale = time_scale;
            Reset();
        }

        public void Reset()
        {
            sim_time = 0.0;
            ship = new OwnShip();
            env = new Environment();
            contacts = new Dictionary<string, Contact>();
            phase = "Standby";
            sys = new ShipSystems();
            _tn = 4000;
            UpdateDerived();
        }

        // del contacts[id] / contacts.pop(id, None). A .NET Dictionary reuses a removed slot for the next add, which
        // would break Python's insertion order, so the dictionary is rebuilt without the removed key.
        public bool RemoveContact(string id)
        {
            if (id == null || !contacts.ContainsKey(id)) return false;
            Dictionary<string, Contact> d = new Dictionary<string, Contact>();
            foreach (KeyValuePair<string, Contact> kv in contacts)
                if (kv.Key != id) d[kv.Key] = kv.Value;
            contacts = d;
            return true;
        }

        public string NextTrack()
        {
            _tn += 1;
            return "TN" + _tn;
        }

        // Back to normal conditions without moving the ship: routine traffic, calm weather, all systems restored.
        public void Normalize()
        {
            contacts = new Dictionary<string, Contact>();
            env = new Environment();
            ship.fault_heat = 0.0;
            sys = new ShipSystems();
            phase = "Ops normal";
            SpawnTraffic();
            UpdateDerived();
        }

        // Routine shipping: (bearing relative to bow, range nm, course, speed kn, classification).
        public static readonly object[][] TRAFFIC = {
            new object[] { 300.0, 16.0, 120.0, 13.0, "MERCHANT" }, new object[] { 60.0, 9.0, 205.0, 4.0, "FISHING" },
            new object[] { 20.0, 18.0, 255.0, 16.0, "MERCHANT" }, new object[] { 150.0, 11.0, 10.0, 5.0, "FISHING" },
            new object[] { 230.0, 14.0, 60.0, 9.0, "UNKNOWN" } };

        public const int MAX_TRACKS = 5;      // the Navigation COP shows at most 5 targets

        // Keep the Navigation COP at MAX_TRACKS: drop the farthest routine track (never a threat).
        public void MakeRoom()
        {
            while (contacts.Count >= MAX_TRACKS)
            {
                Contact far = null;
                foreach (Contact c in contacts.Values)
                    if (c.kind != "hostile" && (c.classification == "MERCHANT" || c.classification == "FISHING" || c.classification == "UNKNOWN")
                        && (far == null || c.range_nm > far.range_nm))
                        far = c;
                if (far == null)
                    break;
                RemoveContact(far.id);
            }
        }

        public void SpawnTraffic()
        {
            OwnShip s = ship;
            for (int i = 0; i < 3; i++)      // leave room for the presenter's injections
            {
                object[] t = TRAFFIC[i];
                double brg = (double)t[0], r = (double)t[1];
                double b = WorldMath.Radians(brg + s.heading);
                Contact c = new Contact { id = NextTrack(), x = s.x + r * Math.Sin(b), y = s.y + r * Math.Cos(b),
                                          course = (double)t[2], speed = (double)t[3], classification = (string)t[4] };
                contacts[c.id] = c;
            }
        }

        // --- physics ---
        // Advance only the COPs on screen: nav = ship motion, traffic, weather, threats; systems = machinery;
        // dc = fire, flooding and repair parties. A COP that isn't showing stays frozen.
        public void Step(double ticks = 1.0, ICollection<string> active = null)
        {
            double dt = time_scale * ticks;          // sim seconds
            sys.real_per_sim = 1.0 / Math.Max(0.001, base_scale);   // team running pace is real-time at 1x
            ICollection<string> on = (active != null && active.Count > 0) ? active : new HashSet<string> { "nav", "systems", "dc" };
            OwnShip s = ship;
            double target_temp;
            if (!on.Contains("nav"))
            {
                if (on.Contains("systems"))
                {
                    target_temp = 62 + s.speed * 1.6 + s.fault_heat;
                    s.engine_temp += (target_temp - s.engine_temp) * Math.Min(1.0, dt / 120);
                }
                sim_time += dt;
                sys.Step(dt, this, on);
                UpdateDerived();
                return;
            }
            // Steering lost: rudder stays where it is. Propulsion lost: the ship coasts down.
            double dh = sys.SteeringOk() ? WorldMath.AngleDiff(s.cmd_heading, s.heading) : 0.0;
            s.heading = WorldMath.Wrap360(s.heading + Math.Max(-TURN_RATE_DPS * dt, Math.Min(TURN_RATE_DPS * dt, dh)));
            double ds = Math.Min(s.cmd_speed, sys.MaxSpeedKn()) - s.speed;      // one shaft caps speed; none, the ship coasts
            s.speed = Math.Max(0.0, s.speed + Math.Max(-DECEL_KN_PS * dt, Math.Min(ACCEL_KN_PS * dt, ds)));
            double[] v = WorldMath.Velocity(s.heading, s.speed);
            s.x += v[0] * dt / 3600;
            s.y += v[1] * dt / 3600;
            target_temp = 62 + s.speed * 1.6 + s.fault_heat;
            s.engine_temp += (target_temp - s.engine_temp) * Math.Min(1.0, dt / 120);
            foreach (Contact c in contacts.Values)
            {
                double[] cv = WorldMath.Velocity(c.course, c.speed);
                c.x += cv[0] * dt / 3600;
                c.y += cv[1] * dt / 3600;
            }
            foreach (object o in env.cells)
            {
                Dictionary<string, object> cell = (Dictionary<string, object>)o;
                double[] wv = WorldMath.Velocity(Py.Num(cell["course"]), Py.Num(cell["speed"]));
                cell["x"] = Py.Num(cell["x"]) + wv[0] * dt / 3600;
                cell["y"] = Py.Num(cell["y"]) + wv[1] * dt / 3600;
            }
            sim_time += dt;
            sys.Step(dt, this, on);
            UpdateDerived();
        }

        public void UpdateDerived()
        {
            OwnShip s = ship;
            s.power_margin_kw = Py.Round(520 - s.speed * 22 - s.fault_heat * 2, 1);
            s.fuel_rate = Py.Round(0.02 * Math.Pow(s.speed, 2.2), 2);
            double[] own_v = WorldMath.Velocity(s.heading, s.speed);
            foreach (Contact c in contacts.Values)
            {
                double[] r = WorldMath.CpaTcpa(new double[] { s.x, s.y }, own_v, new double[] { c.x, c.y }, WorldMath.Velocity(c.course, c.speed));
                c.cpa_nm = r[0];
                c.tcpa_min = r[1];
                double dx = c.x - s.x, dy = c.y - s.y;
                c.range_nm = WorldMath.Hypot(dx, dy);
                c.bearing = WorldMath.Wrap360(WorldMath.Degrees(Math.Atan2(dx, dy)));
            }
            // Drop contacts that are far astern and opening.
            foreach (string cid in contacts.Where(kv => kv.Value.range_nm > 25 && kv.Value.tcpa_min < 0).Select(kv => kv.Key).ToList())
                RemoveContact(cid);
        }

        // Minimum CPA over approaching contacts if own ship ran at this heading/speed now.
        public double PredictedMinCpa(double? heading = null, double? speed = null)
        {
            OwnShip s = ship;
            double[] own_v = WorldMath.Velocity(heading.HasValue ? heading.Value : s.heading, speed.HasValue ? speed.Value : s.speed);
            double best = 99.0;
            foreach (Contact c in contacts.Values)
            {
                double[] r = WorldMath.CpaTcpa(new double[] { s.x, s.y }, own_v, new double[] { c.x, c.y }, WorldMath.Velocity(c.course, c.speed));
                if (0 <= r[1] && r[1] <= 45 && c.kind != "hostile" && !c.hidden)
                    best = Math.Min(best, r[0]);
            }
            return best;
        }

        public List<Contact> Threats(double min_cpa, double horizon_min = 30)
        {
            return contacts.Values.Where(c => c.kind != "hostile" && c.cpa_nm < min_cpa && 0 <= c.tcpa_min && c.tcpa_min <= horizon_min).ToList();
        }

        static string F(double x, int n) { return Py.F(x, n); }
        static string F03(double x) { return Py.Round(x).ToString("000", Inv); }

        // --- scenario events ---
        // Apply one scenario event. Returns a log line.
        public string ApplyEvent(Dictionary<string, object> ev)
        {
            if (ev.ContainsKey("phase"))
            {
                phase = Py.Str(ev["phase"]);
                return "Phase: " + phase;
            }
            if (ev.ContainsKey("spawn_contact"))
            {
                Dictionary<string, object> p = Py.DictOrEmpty(ev["spawn_contact"]);
                OwnShip s = ship;
                double t_h = Py.Num(Py.Get(p, "collision_in_min", 15)) / 60;
                // Put the contact on a course that meets own ship's projected position at t_h.
                double[] o = WorldMath.Velocity(s.heading, s.speed);
                // relative_course: the contact's course relative to own ship's heading (180 = reciprocal: head-on, dead ahead).
                double course = p.ContainsKey("relative_course") ? WorldMath.Wrap360(s.heading + Py.Num(p["relative_course"])) : Py.Num(p["course"]);
                double speed = Py.Num(p["speed"]);
                double[] cv = WorldMath.Velocity(course, speed);
                if (p.ContainsKey("start_range_nm"))
                {
                    // Collision drills start at a set range: the meeting time follows from the closing speed.
                    double rel = Math.Sqrt((cv[0] - o[0]) * (cv[0] - o[0]) + (cv[1] - o[1]) * (cv[1] - o[1]));
                    t_h = Py.Num(p["start_range_nm"]) / Math.Max(1.0, rel);
                }
                double mx = s.x + o[0] * t_h, my = s.y + o[1] * t_h;
                double off = Py.Num(Py.Get(p, "offset_nm", 0.0));
                MakeRoom();
                object pid = Py.Get(p, "id");
                Contact c = new Contact { id = Py.Truthy(pid) ? Py.Str(pid) : NextTrack(), x = mx - cv[0] * t_h + off, y = my - cv[1] * t_h,
                                          course = course, speed = speed, classification = Py.Str(Py.Get(p, "classification", "MERCHANT")) };
                contacts[c.id] = c;
                UpdateDerived();
                return string.Format("CIC: new track {0}, {1}, bearing {2}, {3} nm, CPA {4} nm",
                                     c.id, c.classification.ToLowerInvariant(), F03(c.bearing), F(c.range_nm, 1), F(c.cpa_nm, 2));
            }
            if (ev.ContainsKey("weather"))
            {
                Dictionary<string, object> upd = new Dictionary<string, object>(Py.DictOrEmpty(ev["weather"]));
                bool heavy = Py.Num(Py.Get(upd, "wind_kts", env.wind_kts)) >= 30;
                if (!upd.ContainsKey("wave_height_m")) upd["wave_height_m"] = heavy ? 3.8 : 0.8;
                upd["cells"] = heavy ? WorldMath.SquallLine(ship) : new List<object>();
                env = env.ModelCopy(upd);
                return heavy
                    ? string.Format("OOD: weather radar shows a squall line ahead. Wind {0} kts, waves {1} m, visibility {2} nm",
                                    F(env.wind_kts, 0), F(env.wave_height_m, 1), F(env.visibility_nm, 1))
                    : string.Format("OOD: weather moderating. Wind {0} kts, waves {1} m", F(env.wind_kts, 0), F(env.wave_height_m, 1));
            }
            if (ev.ContainsKey("engine_fault"))
            {
                ship.fault_heat = 25 * Py.Num(Py.Get(Py.DictOrEmpty(ev["engine_fault"]), "severity", 1.0));
                return "Engine fault injected: cooling degraded";
            }
            if (ev.ContainsKey("clear_fault"))
            {
                ship.fault_heat = 0.0;
                return "Engine fault cleared";
            }
            if (ev.ContainsKey("hostile"))
            {
                Dictionary<string, object> p = Py.DictOrEmpty(ev["hostile"]);
                OwnShip s = ship;
                double brg = WorldMath.Radians(Py.Num(Py.Get(p, "bearing", 40)) + s.heading);
                double r = Py.Num(Py.Get(p, "range_nm", 17.0));
                MakeRoom();
                Contact c = new Contact { id = NextTrack(), x = s.x + r * Math.Sin(brg), y = s.y + r * Math.Cos(brg), course = 0,
                                          speed = Py.Num(Py.Get(p, "speed", 32)), kind = "hostile", classification = "UNKNOWN" };
                contacts[c.id] = c;
                UpdateDerived();
                return string.Format("CIC: new surface track {0}, bearing {1}, {2} nm, {3} kn, unknown, no AIS",
                                     c.id, F03(c.bearing), F(c.range_nm, 1), F(c.speed, 0));
            }
            if (ev.ContainsKey("submarine"))
            {
                // Submerged and quiet: not on the COP until the sonar holds it (or it fires).
                Dictionary<string, object> p = Py.DictOrEmpty(ev["submarine"]);
                OwnShip s = ship;
                double brg = WorldMath.Radians(Py.Num(Py.Get(p, "bearing", -60)) + s.heading);
                double r = Py.Num(Py.Get(p, "range_nm", 9.0));
                MakeRoom();
                bool detected = Py.Truthy(Py.Get(p, "detected", false));   // sonar already holds it: on the COP now
                Contact c = new Contact { id = NextTrack(), x = s.x + r * Math.Sin(brg), y = s.y + r * Math.Cos(brg), course = 0,
                                          speed = Py.Num(Py.Get(p, "speed", 12)), kind = "hostile",
                                          classification = detected ? Py.Str(Py.Get(p, "classification", "SUSPECT")) : "UNKNOWN",
                                          domain = "subsurface", hidden = !detected };
                contacts[c.id] = c;
                UpdateDerived();
                return detected
                    ? string.Format("Sonar: submarine contact {0}, {1}, bearing {2}, {3} nm (detected)", c.id, c.classification.ToLowerInvariant(),
                                    F03(c.bearing), F(c.range_nm, 1))
                    : string.Format("Presenter: submarine {0} placed {1} nm, bearing {2} (submerged, not yet detected)",
                                    c.id, F(c.range_nm, 1), F03(c.bearing));
            }
            if (ev.ContainsKey("inbound"))
            {
                // Launched from over the horizon, beyond the 20 nm COP: nobody sees it until it crosses 20 nm.
                Dictionary<string, object> p = Py.DictOrEmpty(ev["inbound"]);
                double brg = Py.Truthy(Py.Get(p, "random")) ? new Random().NextDouble() * 360
                    : WorldMath.Wrap360(ship.heading + Py.Num(Py.Get(p, "relative_bearing", 40)));
                double rng = Py.Num(Py.Get(p, "range_nm", 28.0));
                int count = Py.Int(Py.Get(p, "count", 2));
                sys.Launch("OTH", rng, count, brg);
                return string.Format("Presenter: {0} inbound launched from {1} nm (beyond the COP), bearing {2}. Not yet detected: enters the COP in ~{3} s",
                                     count, F(rng, 0), F03(brg), F((rng - 20) / 500 * 3600, 0));
            }
            if (ev.ContainsKey("aircraft"))
            {
                Dictionary<string, object> p = Py.DictOrEmpty(ev["aircraft"]);
                int n = Py.Int(Py.Get(p, "count", 1));
                sys.LaunchAircraft(n);
                return string.Format("Presenter: {0} strike aircraft inbound from a random bearing (30 nm, not yet on the COP)", n);
            }
            if (ev.ContainsKey("torpedo_spread"))
            {
                Dictionary<string, object> p = Py.DictOrEmpty(ev["torpedo_spread"]);
                sys.LaunchTorpedoes(Py.Int(Py.Get(p, "count", 4)), Py.Num(Py.Get(p, "range_nm", 5.0)));
                return "Presenter: torpedo spread from an unseen submarine";
            }
            if (ev.ContainsKey("friendly"))
            {
                // A friendly warship on a collision course from a random direction (never more than two at once).
                int have = contacts.Values.Count(c => c.classification == "FRIENDLY");
                if (have >= 2) return "Presenter: already two friendly ships closing; not adding another";
                Random r = new Random();
                double course = r.NextDouble() * 360, speed = 12 + r.Next(0, 12), mins = 8 + r.Next(0, 10);
                return ApplyEvent(Py.D("spawn_contact", Py.D("course", course, "speed", speed, "collision_in_min", mins, "start_range_nm", 5.0,
                                                             "classification", "FRIENDLY", "offset_nm", (r.NextDouble() - 0.5) * 0.6)));
            }
            if (ev.ContainsKey("engine_loss"))
            {
                sys.AddCasualty("MER2", "equipment", 0.0, new string[] { "SHAFT-2" }, "GTM 2A/2B casualty, low lube oil pressure");
                return "Loss of Shaft 2 (GTM 2A/2B)";
            }
            if (ev.ContainsKey("fail_nodes"))         // several at once (one casualty per node, in its own compartment)
            {
                Dictionary<string, object> p = Py.DictOrEmpty(ev["fail_nodes"]);
                List<string> ids = Py.ListOrEmpty(p["nodes"]).Select(x => Py.Str(x)).ToList();
                foreach (string id in ids)
                    sys.AddCasualty(Py.Str(sys.nodes[id]["comp"]), "equipment", 0.0, new string[] { id }, Py.Str(Py.Get(p, "reason", "equipment casualty")));
                return "Equipment casualties: " + string.Join(", ", ids.ToArray()) + " (" + Py.Str(Py.Get(p, "reason", "")) + ")";
            }
            if (ev.ContainsKey("fail_node"))
            {
                Dictionary<string, object> p = Py.DictOrEmpty(ev["fail_node"]);
                sys.AddCasualty(Py.Str(sys.nodes[Py.Str(p["node"])]["comp"]), "equipment", 0.0, new string[] { Py.Str(p["node"]) },
                                Py.Str(Py.Get(p, "reason", "equipment casualty")));
                return "Equipment casualty: " + Py.Str(p["node"]) + " (" + Py.Str(Py.Get(p, "reason", "")) + ")";
            }
            if (ev.ContainsKey("casualty"))
            {
                Dictionary<string, object> p = Py.DictOrEmpty(ev["casualty"]);
                string kind = Py.Str(Py.Get(p, "kind", "fire"));
                sys.AddCasualty(Py.Str(p["comp"]), kind, Py.Num(Py.Get(p, "severity", 0.6)));
                return string.Format("Casualty injected: {0} in {1}", kind, Py.Str(p["comp"]));
            }
            return "";
        }

        // Closest the ship's straight track would come to each heavy rain cell's EDGE over the next `minutes`
        // (cells drift too). Negative clearance = the track runs into the cell.
        public Dictionary<string, object> StormClearance(double heading, double? speed = null, double minutes = 30, double min_intensity = 0.6)
        {
            OwnShip s = ship;
            double v = speed.HasValue ? speed.Value : s.speed;
            Dictionary<string, object> outd = new Dictionary<string, object>();
            foreach (object o in env.cells)
            {
                Dictionary<string, object> cell = (Dictionary<string, object>)o;
                if (Py.Num(cell["intensity"]) < min_intensity)
                    continue;
                double best = 1e9;
                for (int k = 0; k < (int)minutes + 1; k++)
                {
                    double t = k / 60.0;
                    double[] ov = WorldMath.Velocity(heading, v);
                    double[] cv = WorldMath.Velocity(Py.Num(cell["course"]), Py.Num(cell["speed"]));
                    double d = WorldMath.Hypot((Py.Num(cell["x"]) + cv[0] * t) - (s.x + ov[0] * t),
                                               (Py.Num(cell["y"]) + cv[1] * t) - (s.y + ov[1] * t)) - Py.Num(cell["r"]);
                    best = Math.Min(best, d);
                }
                outd[Py.Str(cell["id"])] = Py.Round(best, 2);
            }
            return outd;
        }

        // Rain cells as the bridge sees them: bearing, range to the cell edge, intensity.
        public List<object> WeatherAhead()
        {
            OwnShip s = ship;
            List<object> outl = new List<object>();
            foreach (object o in env.cells)
            {
                Dictionary<string, object> cell = (Dictionary<string, object>)o;
                double dx = Py.Num(cell["x"]) - s.x, dy = Py.Num(cell["y"]) - s.y;
                double deg = WorldMath.Degrees(Math.Atan2(dx, dy));
                outl.Add(Py.D("id", cell["id"], "bearing", (int)Py.Round(WorldMath.Wrap360(deg)),
                              "range_to_edge_nm", Py.Round(Math.Max(0.0, WorldMath.Hypot(dx, dy) - Py.Num(cell["r"])), 1),
                              "intensity", cell["intensity"],
                              "relative_bearing", (int)Py.Round(WorldMath.AngleDiff(deg, s.heading))));
            }
            return outl;
        }

        public Dictionary<string, object> Cop()
        {
            return Py.D(
                "sim_time", sim_time,
                "phase", phase,
                "ownship", ship.Dump(),
                "environment", env.DumpWith("cells_relative", WeatherAhead()),
                "contacts", Py.ToList(contacts.Values.Where(c => !c.hidden).Select(c => (object)c.Dump())),
                "route_heading", ROUTE_HEADING,
                "systems", sys.View(this));
        }
    }

    // Timed events from a scenario file (YAML converted to JSON). Times are in real (wall-clock) seconds from start.
    public class Scenario
    {
        public string name;
        public List<Dictionary<string, object>> events;
        public double elapsed;
        int _next;
        public bool running;

        public Scenario(string path)
        {
            Dictionary<string, object> data = Json.ParseObject(Paths.ReadText(path));
            name = Py.Str(data["name"]);
            events = Py.ListOrEmpty(data["events"]).Select(e => (Dictionary<string, object>)e).OrderBy(e => Py.Num(e["t"])).ToList();
            elapsed = 0.0;
            _next = 0;
            running = false;
        }

        // Scenario(BACKEND / "scenarios" / f"{name}.yaml") -> Scenario.Named(name), reading <Paths.Scenarios>\<name>.json
        public static Scenario Named(string name)
        {
            return new Scenario(System.IO.Path.Combine(Paths.Scenarios, name + ".json"));
        }

        public void Reset()
        {
            elapsed = 0.0;
            _next = 0;
            running = false;
        }

        public List<Dictionary<string, object>> Due(double dt)
        {
            List<Dictionary<string, object>> outl = new List<Dictionary<string, object>>();
            if (!running)
                return outl;
            elapsed += dt;
            while (_next < events.Count && Py.Num(events[_next]["t"]) <= elapsed)
            {
                outl.Add(events[_next]);
                _next += 1;
            }
            return outl;
        }

        public bool Finished()
        {
            return _next >= events.Count;
        }
    }
}

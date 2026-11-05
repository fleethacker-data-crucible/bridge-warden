# Porting warden/*.py to C# (src/src)

Rules for every file in `src/src`. The port is finished and the Python is gone from this branch; the original `backend/warden/*.py` (the spec it was ported from) is in git history on `master`. These rules still apply to every change to the C# host.

## Compiler and runtime
- **C# 5 only.** The compiler is the one built into Windows (.NET Framework `csc` via `Add-Type`). Not allowed: `$"..."` interpolation, `?.` / `?[]`, `nameof`, expression-bodied members (`=>` on methods/properties), auto-property initializers, `out var`, tuples / `ValueTuple`, `is` patterns, local functions, `default` literal, `throw` expressions. Use `string.Format`/concatenation, explicit null checks, full method bodies, `out` params or small classes. Lambdas `x => ...` and `delegate { }` are fine. `async`/`await` work but prefer threads + `.Wait()`.
- **References: `System.dll` and `System.Core.dll` only.** No `System.Web.Extensions`, no `System.Data`, no NuGet, no third-party DLLs.
- **One namespace:** `BridgeWarden`. One file per Python module (e.g. `world.py` -> `World.cs`).
- **ASCII only** in every `.cs` file (comments too). CRLF line endings. Use `(char)0x2028`-style for non-ASCII code points.
- **Source rules** (`src/Build.ps1`): no `System.Net`, `WebRequest`, `HttpClient`, sockets, `Dns.`, `Process.Start`, `Assembly.Load`, `LoadLibrary`, `DllImport`, or `https://` in code, except: `WebServer.cs` (HttpListener), `LlmClient.cs` (outbound model calls), `Ledger.cs` (`DllImport("winsqlite3.dll")` only).
- **Compile check your files** (separate process, nothing else breaks):
  `powershell -NoProfile -ExecutionPolicy Bypass -File src\tools\Test-Compile.ps1 Json.cs Py.cs Models.cs Paths.cs <your files...>`
  The whole set: `... Test-Compile.ps1 -All`.

## Foundation (don't change these; put extra helpers in your own file)
- `Json.cs`: `Json.Parse` / `ParseObject` give `Dictionary<string, object>` (key order kept), `List<object>`, `string`, `double`, `bool`, `null`. `Json.Write(object)` writes those, any `IDictionary`/`IEnumerable`, numbers, `IJson`, and `RawJson`.
- `Py.cs`: Python-style helpers. `Py.D("k", v, ...)`, `Py.L(...)`, `Py.Get(d, key[, default])` (dict.get), `Py.Dict/List(...)` casts, `Py.Num` (float()), `Py.Int` (int()), `Py.Str` (str()), `Py.Truthy`, `Py.Repr`, `Py.Round` (banker's, like Python 3), `Py.F(x, n)` (`f"{x:.nf}"`), `Py.Now()` (time.time()), `Py.Perf()` (perf_counter), `Py.Hex(n)` (uuid4().hex[:n]), `Py.DeepCopy`, `Py.Cut`.
- `Models.cs`: `AgentIdentity`, `Intent` (field `@params`, JSON key `params`), `Conflict`, `Escalation`, `Alert`, all with snake_case public fields; `.Dump()` = `model_dump()`, `.DumpWith("k", v, ...)` = `model_dump() | {...}`. `Stages.All`, `Domains.ForAction/ForAlert`, `Scopes.ScopedView`.
- `Paths.cs`: `Paths.Config` (`agents.json`, `policies.json`), `Paths.Scenarios` (`<name>.json`), `Paths.Skills`, `Paths.Fixtures`, `Paths.Data`, `Paths.EnvFile`.

## Names (so modules ported separately fit together)
- **Classes:** the Python class name (`World`, `ShipSystems`, `Scenario`, `Ledger`, `Warden`, `LLMAgentRuntime` ...).
- **Attributes / fields:** the Python attribute name unchanged, snake_case, public (`world.ship`, `world.sys`, `sys.interceptors`, `warden.intents`). Pydantic models (`Contact`, `OwnShip`, `Environment`) derive from `Model` with snake_case public fields.
- **Methods:** PascalCase of the Python name, leading underscore dropped: `predicted_min_cpa` -> `PredictedMinCpa`, `_step_dc` -> `StepDc`, `cop()` -> `Cop()`, `view(world)` -> `View(world)`. Properties stay methods.
- **Module functions:** static methods on a static class named after the module in PascalCase: `world.cpa_tcpa` -> `WorldMath.CpaTcpa`, `world.velocity` -> `WorldMath.Velocity`, `world.wrap360` -> `WorldMath.Wrap360`, `world.angle_diff` -> `WorldMath.AngleDiff`; `llm.run` -> `Llm.Run`; `explain.intent_story` -> `Explain.IntentStory`. Tuples return `double[]` (`Velocity` -> `{vx, vy}`, `CpaTcpa` -> `{cpa, tcpa}`).
- **Module constants:** public static (readonly) fields with the Python name, on that static class or on the main class of the module: `shipsys.COMP` -> `ShipSystems.COMP`, `shipsys.DEFENSES` -> `ShipSystems.DEFENSES`, `shipsys.TEAM_FRAMES_PER_S` -> `ShipSystems.TEAM_FRAMES_PER_S`.
- **Keyword args with defaults** -> optional parameters with the same defaults.

## Shapes
- Python dicts stay `Dictionary<string, object>`, lists stay `List<object>`. Keep **every key name and nesting** the Python produces: the UI reads them.
- Numbers: store as `double` unless Python clearly uses int (counts, ids). `Json.Write` prints whole doubles as `14.0`, like Python.
- `None` -> `null`. Python `set` -> `HashSet<string>`.
- YAML config became JSON with the same structure (`backend/config/*.json`, `backend/scenarios/*.json`).
- `random.Random(seed)` -> `System.Random(seed)` (sequences differ from Python; that's accepted).

## Threads
The tick loop runs on one thread; model calls run on worker threads. Where Python used `threading.Lock`, use `lock`. Don't call PowerShell from C#.

## Style
Match the Python's comments: keep the docstrings and inline comments (as `//`), same density. Same constant names in PascalCase or UPPER_CASE consts, same function order.

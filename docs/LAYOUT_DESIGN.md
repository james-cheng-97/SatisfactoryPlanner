# Factory layout & blueprint design guide

The design flow of the layout planner (`Pnr.cs`, `Layout.cs`, `BlueprintExport.cs`, writer `bp/write.js`), modelled on
EDA (PCB / chip) place-and-route, and the rules learned from in-game tests. Read this before changing the planner.
Keep it current: when a stage changes, update its **Status** and the rules.

---

## 1. Pipeline overview

| # | Stage | EDA equivalent | Where | Status |
|---|-------|----------------|-------|--------|
| 1 | Specification | spec capture | `Solver.cs`, `Plan.cs` | done |
| 2 | Netlist + line classes | netlist, net classes | `BuildPnr` (lanes), `Route` (`Global`) | done |
| 3 | Floor split | partitioning | `BuildPnrFloors` | done |
| 4 | Clustering | floorplanning | `FormClusters`, blocks in `Place` | first version (helps belt, not blueprints yet) |
| 5 | Placement | placement | `Place` (simulated annealing) | done |
| 6 | Local routing | detailed routing | `Route` (two-stage: stage A) | done — two-stage is the fallback |
| 7 | Global routing / buses | global routing | `Route` (two-stage: stage B) | done (implicit buses) |
| 8 | Congestion → stacking | layer assignment | `FindCorridors`, `Stacked` | first version (one shot) |
| 9 | Compaction | compaction | `Compacted`, `Place(compact)` | first version (kept only if better; small gains) |
| 10 | Over-building routes | over-the-cell routing | — | last (global lines above low machines) |
| 11 | Export | tape-out | `BlueprintExport`, `write.js` | done |

Every stage must keep a **fallback**: if a later stage fails or makes things worse, keep the earlier result.
Final fallback: the old rows engine (`BuildLayout` → rows), which must still obey the hard rules below.

---

## 2. Stages

### 2.1 Specification (done)
- **Drained mode, intermediates may back up** (`BackUpIntermediates`, **default off for now** — on the user's plan it
  removes 1 machine, which tipped its multi-floor layout over the time limit into the rows fallback): an item only machines use, made
  by machines without byproducts, gets no extra supply for its splitters (its belt backs up and balances). Not for
  items whose makers have a byproduct (a backed-up byproduct stalls the machine and production drops). Measured on
  the user's plan: 53 → 52 machines (most spare capacity comes from whole machines at 100 %).
Targets, recipes (overrides, optimizer), tier, rounding mode (Exact / Steady / NoClog / Drained), belt / pipe tier,
overflow guards, power in MW. Options that change the plan:
- **Plastic / rubber supplied** (`SupplyPolymers`, default on): they arrive as raw inputs unless they are a target.
- **Byproduct = product** → goes into the product's output box, not an overflow box (`SurplusIntoOutputs`).

### 2.2 Netlist & line classes (done)
- A **line** (net) = one item from its producers to its consumers; split into parallel lines (`#k`) when one belt
  can't carry it.
- **Global** line: touches a box (input/output, floor crossing) or spans > 40 m. **Local**: the rest.
- Pipes are lines too but are never stacked (few pipes; pumps).

### 2.3 Floor split (done)
- Machines taller than 15 m (Refinery, …) **always on the ground floor**; floors above leave a **hole** (no roof).
- Machines with fluids first (lowest floors: no pumping up), then production order, weighted by floor area × belts.
- Every item made on one floor and used on another gets **one crossing per destination floor** (never one lift
  splitting up and down). Crossings are placed freely on the lowest floor using them; floors above keep the spot.

### 2.4 Clustering (first version)
Bottom-up clustering by connection strength (as in chip floorplanning): every machine group on its own, then merge
the two connected clusters with the strongest link (items / min between them) for their combined size, while the
cluster fits ≤ 70% of a tile. Each cluster is laid out on its own, then moved as one block by the factory
placement (no turning / swapping of its groups; the one-tile pack ignores clusters). A cluster a failed line
touches is dissolved. Single-floor plans are also laid out without clusters; the better is kept.
Measured: rip3 forms one cluster (the whole chain) — belt 352 → ~190 m but 2 blueprints, so the unclustered
1-blueprint layout wins; HMF's groups are already about a tile each (no pair fits), plastic unchanged. The user's
instinct "only low-level intermediates cluster" matches what happens at these sizes.

### 2.5 Placement (done)
Simulated annealing over groups: estimated belt length (weighted by rate), bends, tile count, compactness.
Placement validity rules are in §3. On routing failure: more room (halo) around the failed lines' groups;
after 4 failures on a big floor, more room everywhere.

### 2.6 Routing — one stage, two-stage as fallback (done)
- **Default, one stage:** all lines negotiated together; lifts / stacked levels allowed where they pay off.
- **Fallback, two stages** (`TwoStage`, when one-stage routing fails — last strategy per floor, last try on a
  single floor): **A** local lines first, flat on the ground, no lifts / ramps; **B** global lines after, free to use
  lifts and stacked levels (raised levels priced ≈ ground, lifts cheaper) — buses emerge where they share space.
  Measured: rescues crowded plans (single-floor Big iron: rows → 23 tiles) but makes easy ones bigger and slower
  (Motors 16 → 20 tiles, the user's plan 22×16 → 21×23), hence fallback only.
- Negotiated congestion (PathFinder): all lines rerouted each round, sharing space costs more every round.
- Stacked **levels**: ground + 2 m, 4 m, 6 m (belts); pipes ground or 4 m only. Change level by a 3 m ramp
  (neighbour level) or a lift (any level).
- Splitters / mergers may sit on any level.

### 2.7 Congestion → stacking (first version)
- **Find corridors:** ground belts of different lines parallel within 4.5 m for ≥ 8 m → bundles (orange bands in
  the view, `≡n` lines).
- **Stack:** reroute with corridor cells dear on the ground / cheap up high; only here may a line lift straight up
  at its machine's opening. Keep if it routes.
- To do: proper loop — propose one corridor stack, measure (blueprints, belt, lifts), keep or revert.

### 2.8 Compaction (first version)
After routing + stacking: push groups west then south (2 m steps, twice) as far as the placement rules allow while
keeping 4 m, then 2 m, of extra room for belts and 3 m of run-out inside the factory; route and stack again; keep
only if fewer blueprints (then less belt). Measured: small gains (plastic 12×18 → 12×17), most pushes fail to
route — a smarter compaction (moving one row / column at a time, rerouting in between) is future work.

### 2.8b Where the time goes (measured, `PNR_PROFILE=1`)
Before gating: compaction ≈ 50 %, stacking pass ≈ 30 %, routing ≈ 20 %, placement ≈ 1 %. Both compaction and the
stacking pass now run only when the layout sticks ≤ 2 foundations past a tile edge (`Promising`) — elsewhere the
corridors are found and shown only. Same results, 20–35 % faster (HMF 70 → 57 s).
Tried and dropped: rerouting only conflicting lines each round (`PNR_INCREMENTAL=1`) — negotiation converges
worse (HMF 11 → 15 blueprints).

### 2.8c Step-aligned floors (tried, off — `PNR_STEPS=1`)
The user's idea: for plans ≥ 20 machines, keep each production step (groups at one depth) on one floor and start
the next floor at a step boundary (`StepFloors`). The "pull upper groups over their partners below" half already
happens through the crossings (placed near their users on the lower floor; upper groups are pulled to them).
Measured: helps some plans (motors2 15 → 13, hmfu3 15 → 12 blueprints) but breaks others (frames3 and the user's
plan fall back to rows); trying both splits and keeping the better doubled the time (user's plan 166 s) and the
step split's results didn't repeat. Revisit once floors route more reliably.

### 2.8a Lane spacing (decided)
Two-lane groups: **wide** 6 m / 9 m (lift → 5 m belt → splitter, as in "Sample connections") or **narrow** 4 m / 7 m.
Neither suits every plan: narrow is needed for plastic to fit 4 blueprints, wide for the user's heavy frame plan.
Single-floor plans are laid out with both (time permitting) and the one with fewer blueprints kept; multi-floor
plans use wide.

### 2.8d Crowding estimate (RUDY) — tried as a placement cost, off (`PNR_RUDYW=<w>`)
`Rudy()` in Pnr.cs: each line spreads its bounding-box length over 4 m bins; supply = free floor / 2 m pitch × 1.5
levels; overflow priced in `Place`'s cost. T6 results (blueprints, off → w3 / w7 / w20 / w60): HMF tiles 11 → 14/12/12/18,
HMF hand 10 → 10/11/15/14, plastic 4 → 6/6/6/6, rip3 1 → 2/2/2/2. Big floors route faster (HMF 56 → 21–34 s at w20+),
but any weight spreads tight plans over tile edges. Lesson: tile packing and crowding fight inside one annealing
cost. Keep the estimator for **ranking candidate layouts before routing** (skip predicted failures), not for placement.

### 2.8e Why routing fails, luck, and the portfolio (measured, all off)
- `PNR_RUDYLOG=1` logs each routing attempt (crowding score, result, reason). The crowding score does **not** predict
  failure (same floor: ok 95-327, fail 77-335), so ranking candidates by it was dropped.
- Failure reasons (T6): most are one line with no path in round 0; the rest are self-crossings and stalled negotiation.
  The "no path" cases are search-budget cut-offs (queue still open), an opening facing another line's opening within
  3 m, a machine right past a border zone in front of an opening, a hub boxed in by its own belt.
- Fixes behind `PNR_FIX` (1 opening clash, 2 border run-out, 4 hub exit, 8 double A* budget). 8 cuts failures ~50 -> 3,
  but every one of them moves the plans by several blueprints either way: the placement annealing is chaotic (any change =
  another random path; plastic ranges 4-9 blueprints and RIP 1-4 across variants). Off.
- Portfolio (`Portfolio`/`RunStrat` in Pnr.cs, unused): all variants/seeds in parallel, keep the best. Single-floor it
  is faster and hits the goals, multi-floor it doesn't help; the user rejected it anyway (CPU load / fan noise for a
  game tool, and best-of-luck isn't a real fix). **Direction: make placement less luck-driven (deterministic
  construction, fewer knobs), not more tries.**

### 2.9 Over-building (last)
A global line high enough may pass over low machines (≤ 12 m, `MaxLiftOver`) to save belt.

### 2.10 Export (done)
One blueprint per tile (Mk2 5×5 / Mk3 6×6 foundations), all floors stacked in it. See §3.4.

---

### 2.10 Layout cache (app)
`LayoutCache.cs`: every finished layout is saved with the plan it came from under `<AppDir>/layouts/<plan hash>/`
(`<options>.json` layout, `.meta.json` plan settings + summary + blueprints/belt). Plan hash = plan nodes/edges + tab
settings without the layout options; options key = floors / blueprint size / hand. The layout toolbar's "Saved" list
shows all of a tab's cached layouts (other plans marked ↩; picking one restores that plan); "Lay out again" rebuilds.
**Bump `LayoutCache.Engine` whenever the planner's output changes**, or old layouts keep being served.

### 2.10b Export blueprints (app button)
The writer ships inside the exe: `bp/` (write.js, templates.json, node_modules of satisfactory-file-parser) is content
of the single-file publish and self-extracts with the app (`AppContext.BaseDirectoryp`). "Export blueprints…" checks
for Node.js (warns + offers nodejs.org), asks for a folder (default SaveGameslueprints), takes the session header from
a game-made .sbp there (or a folder above), writes "<tab> rRcC.sbp" per tile and "<tab> wiring.txt" (joints + warnings).
Keep `bp/` in the repo in step with the scratchpad writer. Build flavours (build.ps1): standard (no Data, no node_modules: wiki data on first start, npm ci into %LOCALAPPDATA%/SatisfactoryPlanner/bp on first export) and full (-p:Flavor=Full: Data, node_modules and a portable node.exe from NodeDir, bp/node/node.exe preferred at run time).

### 2.10c Plastic + rubber recycling loop (app, `PolymerLoop.cs`)
A plan for exactly plastic + rubber, with the loop recipes available (save unlocks, or tier / alternates without a save),
asks once per tab (`Settings.PolymerLoopAnswer`) whether to pin the loop: Heavy Oil Residue (alt) → Diluted Packaged
Fuel (alt) → Unpackage Fuel → Recycled Plastic ⇄ Recycled Rubber (packaged water / canisters loop too). About 80 per 30
crude vs 40. It needs a starting stock (plastic, rubber in the recycling refineries; empty canisters in the packagers);
the machines' input slots are the buffer, no storage container. Plastic and rubber belts always get a smart splitter with
the output on Overflow (`Splitters.Plan(loopFirst)`, and the export treats their output boxes like overflow boxes), so
the other side's refineries are always fed first and the loop can't run dry. Test: scratchpad `QUICK=10 [MODE=..] [LAYOUT=1]`.

### 2.10d Clog guards: overflow chains (app, `OverflowChain.cs`, `ClogGuards.OptionsFor`, `Settings.ClogHandling`)
A clogging surplus is only safe when it ends where nothing fills up: an AWESOME Sink (items) or generators (fuels:
fuel / coal generators). A box fills, and an output is never guaranteed to be taken (user, 2026-09-25). Processing a
surplus with a direct recipe (the surplus + raw resources only) makes a NEW overflow — its product only exists while
there is surplus, so it never counts towards the targets: it's kept as its own item `<item>@ovf` with its own guard row
and handling (process again, burn, sink, or merge into that product's output through a smart splitter with a sink on
Overflow — the merger is finished by hand, the export warns). The main plan is solved as usual and never changed; the
chain (machines, water, generators' power) is added on top (`OverflowChain.Apply`). Overflow steps are uncapped
consumers (smart splitter Overflow) in the splitter plan, and the export makes the splitter feeding them smart.
Sizes / ports: sink 16 × 13 m (24 m), input 5 m out; fuel generator 20 × 20 (27 m), pipe 8.6 m out; coal generator
10 × 26 (36 m), belt + water 11 m out — ports measured in the player's save, templates from it too.
Tests: scratchpad `QUICK=10 CLOG=.. CLOG2=..` (loop resin chain), `QUICK=12` (heavy oil → Residual Fuel → generator).

### 2.10e Inputs and overflow without boxes (user, 2026-09-26)
- **Inputs are belt / pipe ends** (`Layout.InputStub` / `InputStubPipe`): no box in the blueprint, the belt starts
  loose where the player brings the supply; the wiring sheet lists where ("Inputs: …"), not as warnings. In place &
  route the stub keeps the room its box took (5 × 10, pipes 4 × 4): smaller cells upset the placement (RIP 1 -> 2,
  plastic 4 -> 6 blueprints).
- **Overflow is cut off at the end of its line** (`CutOffOverflow`): a surplus that is the end of a split line other
  machines use, or spare capacity of single-output machines (it backs up harmlessly), gets no box and no belt. A
  byproduct with nowhere else to go keeps its box (or sink / generator); cut off, it would stop its machine.
- Only product outputs keep a box.

### 2.10f The player's hand-built motor factory (Eorzea Cafe, 2026-09-26) — the target for a columns engine
10 motors / min, 41 machines (10 refineries: copper ingot, copper sheet, pure iron; a Solid Steel foundry; 8 iron wire,
3 iron pipe, 2 steel rod, 9 screw constructors; 4 stator, 2 copper rotor, 2 motor assemblers) and 2 water extractors in
**93 × 115 m (70 × 115 m without the extractors) on 2 levels, 1 box**. (A first capture took only 57 × 77 m of it.)
The old designer made 176 × 168 m on 2 floors for the same plan. How it's built:
1. **One column per production step**, machines packed at their own width: all 10 refineries in one column (10 m
   pitch) → wire / pipe / steel-rod constructors (8 m) and the foundry → stator assemblers (9 m) → the 2 motor
   assemblers side by side at the end, output box after them.
2. **One belt corridor between neighbouring columns**: the producers' merger chain runs straight on as the
   consumers' splitter chain; nothing crosses the factory.
3. **Manifolds run raised, lifts go straight from a splitter into a machine / from a machine up into a merger**, so the
   input and output manifolds share one corridor at different heights.
4. **Side chains upstairs**: screws and rotors on a floor 12 m up, above the column they feed.
5. **Inputs arrive on belts at one edge; manifolds just end** (the last splitter's through output left free).
6. **I/O ports close together**: a belt only needs room for its lift — two lifts side by side need about 4 m, so
   inputs / outputs sit at ~4 m pitch in one bank, not as separate PCB-style pads (pipes the same, a small tank
   where a pipe input needs one).

### 2.10g Columns engine (`Columns.cs`, Advanced → "Columns layout (experimental)", `Settings.LayoutEngine`)
Deterministic, fast. Columns in flow order (steps may share a column; tall machines packed among themselves when there
are floors), machines turned so inputs face west, flush with the column's west face. Corridor = distribute tracks of
the east column + collect tracks of the west one, one height each (ground track by the machines when that side has no
pipes; 2 / 4 / 6 m raised), a lift at each port (`BlueprintExport.PortsOf` gives the export's exact port positions).
Header rows 8 m up north of the columns (pipes on the ground), packed so non-overlapping spans share a y; inputs as a
bank at the west edge, product boxes at the east edge. Two floors: tall / pipe columns on the ground (open above),
every assignment of the other columns tried, header rows share y across floors, a lift + passthrough per item that
changes floors. A few column heights are tried; the smallest wins. Falls back to place & route if it fails.
Status (2026-09-26, lab notes artifact "Columns Engine Lab Notes"): the player's exact motor plan (41 machines), 2 floors:
152 × 112 m = 2.11× their 70 × 115 m (1.59× their 93 × 115 m with the water extractors), 0 export warnings; the old
designer 3.67×. Test: `QUICK=16 SCOPE=player FLOORS=2`. Next: U-turn corridors (merger chain → splitter chain
of the next column), header only for items that skip columns, side chains upstairs above their consumer.
Test: scratchpad `QUICK=16 [SCOPE=matched] [FLOORS=2] [TAG=..]`.

### 2.11 In-game test saves (scratchpad `bp/`)
`inject.js` puts a factory's tile blueprints (write.js objects, `DUMP_OBJECTS=1`) into a copy of a save, 50 m east / 100 m
up from the player (`STRIP=1` removes an earlier injection: names 2100000000–2100099999). `testrun.js` then joins every
border cut (straight piece between facing ends), fills input boxes (spec `fill`: item stacks; tanks full) and builds a
typed FGPipeNetwork per fluid system (tank, pipes, junctions, machine pipe ports — as the game saves them). Checks:
`conn.js` (wiring vs the injected factory), `scheck.js`, `rampport.js`, `humps.js`, `fold.js`, `twist.js`.
Fluid boxes: small Fluid Buffer by default; "Industrial fluid buffers" option (12 m, ports ±6 m measured) costs space
(plastic 4 -> 7, HMF to rows).

## 3. Hard rules (learned in game — don't break)
- **Storey height = tallest machine below (≤ 15 m) + ~4 m headroom**, rounded up to 4 m walls (min 12 m). A floor
  resting on the machines' roofs feels cramped in game (user, 2026-09-24).
- **No splitter / merger within a bend radius of a corner** on its through line: its straight port faces the old line
  while the belt already turns, and nothing snaps (seen in game). Router: no belt join within 2 m of a bend
  (`BendJoinR`, machines inside tiles; off with "machines may cross tiles", where it cost 10 -> 12 blueprints; 3 m
  cost HMF 11 -> 15). Export: slides an attachment onto the corner (or the turning belt back onto it) and re-seats every
  belt on the port facing its own leg. `bp/scheck.js` checks every attachment port — HMF tiles: 0 left.
- **Timing:** big plans run near their time limits; a busy CPU (e.g. a build running alongside) can push HMF into the
  rows fallback. Don't benchmark while something else runs.
- **A loose belt end snaps only onto a machine port on its own floor** (height checked): it once snapped onto a machine two
  floors up (32 m of belt straight up, two belts on one port). The export warns on any port with two belts / pipes.
  `bp/conn.js` checks a player's wired save against the injected factory (lost connections, joints, open ends, steep belts).
- **1 m of level belt at a port before a ramp** (splitter, merger, machine, lift): write.js starts the ramp 1 m out,
  taking it from the level run past the ramp (same length and slope). HMF: 7 -> 1 (a lift top with no level run after).
  `bp/rampport.js` counts them (user, 2026-09-24).
- **The last splitter before an overflow box is a Smart Splitter**: that output Overflow, other used outputs Any, unused
  None (rule OutputIndex 0 = Output1, 1 = Output2, 2 = Output3, read off sorting splitters in a save) (user, 2026-09-24).
- **A lift plugs straight into a machine port only when it stands against it** (belt ≤ 1.2 m, same tile); a lift 2 m out
  in the next tile lost a refinery's input.
- **Copied save objects:** rename every internal reference (power info, inventories), else the game crashes on load;
  testrun.js refuses to write with any reference to a missing object.
- **No zero-length belt / pipe in a blueprint**: a port right at a tile border (machines crossing tiles: no border
  zones) left a 0 m piece; the export drops pieces < 50 cm and lists "join by hand" at that port. Tried and reverted:
  keeping joins 4 m off borders in that mode (HMF hand 10 -> 11, fixed 1 spot). Left in hand mode (HMF): a merger on a
  border, one output turning at its port, 2 ramps at ports (no bend / border rules there by design).
- **A ramp keeps 2 m of level belt from a corner and 1 m from a port** (write.js slides it along its straight run): a
  ramp starting inside a corner's curve folded the belt back on itself (twisted merger output in game). `fold.js`.
- **A belt ramp is one smooth slope, level at both ends** (its own piece, horizontal tangents as long as the run), and
  ramps the same way with < 3 m of level belt between them merge into one slope (write.js). As two "bends" per slope
  change, or a 2 m-step stair, it looks like a hump in game (user, 2026-09-24).
- **Belts / pipes are written as one piece per straight run and per curve** (write.js), joined end to end: easier to
  fix in game than long splines. A loose end right after a curve must keep the curve's end tangent (snap direction);
  never two spline points in one spot (zero tangent = snap point facing anywhere).

### 3.1 Game geometry
- Belt: max slope 35°, min bend radius 2 m (tangent 330), max 56 m per piece (longer runs chained).
- Pipe bends: 1 m radius (tangent 164), flat and vertical alike. Pipe rise > 5 m risky, > 10 m needs a pump.
- Belts may cross / intersect; a line must never cross **itself** at the same height.
- Conveyor lift: faces the incoming belt (yaw = in-direction + 180°), top turns to the exit.
  At a machine it plugs **straight into the port** (no belt), standing 1 m past the machine edge.
- Splitter / merger / T-junction: ports as measured in `AttachPort` (T: Connection0 −x, 1 +x, 2 −y branch).
- Junction: never within 3 m of a pipe bend; splitters / mergers ≥ 4 m apart, not right by a lift.
- Two-lane machine groups: raised lane 6 m out (lift → 5 m belt → splitter), ground lane 9 m out.
- Lifts: ≥ 2 m apart side by side, 3 m in line; keep 2 m from a group's own lane lifts.

### 3.2 Blueprint tiles
- Nothing crosses a tile border except straight belts / pipes, which are cut there: belts 1 m short, pipes 2 m
  short (they over-extend), ≥ 1 m pipe after a bend; joints listed for the player.
- Border zone (no turns / lifts within 2.5 m of a border) — **lifted** when "Machines may cross tiles" is on:
  machines across a border are placed by hand (✋, outlet slot kept in the tile it covers most, dotted power
  hint); bends by a border are left for the player (lime dashes).
- Tile size: Mk3 6×6 by default (app setting), Mk2 5×5; "No tiles" plans without borders.

### 3.3 Floors
- Ground floor on 4 m foundations; upper floors on 1 m foundations, 12 m storeys (16 m if a floor has a machine
  > 11 m), no foundation over tall machines, passthroughs where lifts / pipes go through.
- Power: an 8×1 m wall with Mk2 / Mk3 outlets per tile, all machines wired, 2 slots left free; on the ground
  (single floor) or hung under the floor above (several floors).

### 3.4 Export conventions
- Belts stop 1 m short of borders; lifts at machines connect directly (`Direct`); T junction when exactly 3 pipes.
- Checks run on every export: bad junction ports, same-height crossings, overlaps, lift inside machine,
  pipe rise, loose ends, tile-border crossings — **zero warnings is the target**.

---

## 4. Working practice

- **Standard test set: QUICK=6** (`test/T.cs`) — the user's heavy frame plan (HMF, 3 floors, loaded from the app's
  `settings.json`), plastic + rubber, rip3; each with machines kept inside tiles and with "machines may cross tiles".
  **Goals: plastic ≤ 4 blueprints, rip3 ≤ 1** (more means planning went wrong); HMF currently 11 (10 with the
  option); zero export warnings with the option off. Keep a change only if it doesn't regress these.
- Wider checks when relevant: QUICK=2 (motors, frames, plastic, big iron — single floor), QUICK=1 (rip3),
  QUICK=3 (plastic export), QUICK=4 (multi-floor set), QUICK=5 (supplied polymers).
- **Always send all rendered pictures** after a run (`floors-*.png`, `cmp-*-pnr.png`, `quick-rip3-pnr.png`,
  `export-plastic.png`).
- Run export checks: `bp/tcheck2.js` (junction ports), `bp/cross2.js` (crossings), `write.js` warnings.
- Results vary with time limits: compare with `PNR_TIMEX` (scales all limits) when judging an algorithm.
  Current limits: ×2 (about two minutes for a big layout).
- A/B switches via environment variables for experiments; remove them once decided.
- Blueprints for the user go to the test session's folder under `SaveGames/blueprints/`, named by factory (`HMF r1c1`,
  `Motors 2F r1c1`, …); the user sorts them into in-game folders (folders live in the save, not on disk).
- Publish the app (`dotnet publish -c Release -o publish`, restart) after user-facing changes.

---

## 5. Open items (priority order)
1. Two-stage routing: fallback only (see §2.6); tune so it also helps easy plans, or leave.
2. Stacking as a keep-or-revert loop with a cost function.
3. Better compaction (row / column at a time).
4. Clustering (§2.4).
5. Stackable conveyor poles / pipe supports in the export.
6. Over-building with high global lines.
7. Single-floor Frames still falls back to rows; big multi-floor plans sit at the edge of the time limit (the
   user's plan: one machine less → floor 2 runs out of time) — make routing faster / floors lighter.
8. "Machines may cross tiles": belts / pipes can end loose at a border and junctions can sit on one (listed as
   hand work in the export); HMF saves 1 blueprint (10 vs 11), plastic / rip3 none.

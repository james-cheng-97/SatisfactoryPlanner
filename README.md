# Satisfactory Planner

A production planner for [Satisfactory](https://www.satisfactorygame.com/) that goes all the way from "I want N per
minute" to in-game blueprints.

- **Plan production**: recipes, alternates and M.A.M. unlocks (optionally read from your save), optimised for your
  goal, with rounding modes up to no-clog setups; plastic / rubber as direct inputs; belt and pipe tiers; a full
  production tree; one tab per factory.
- **Lay it out**: place & route (machine groups placed, every belt and pipe routed, busy runs stacked on lifted levels),
  multi-floor factories (tall machines on the ground floor, fluids low, proper headroom), packed into Mk2 / Mk3
  blueprint tiles. Belt geometry follows the game's rules (bend radii, level belt at ports, smooth ramps). Overflow
  outputs get a Smart Splitter set to Overflow.
- **Export blueprints**: real `.sbp` files, one per tile, into your session's blueprint folder, plus a `wiring.txt`
  listing the belts and pipes to join between tiles.
- Layouts are cached per tab and layout option. English, Deutsch, Français, 中文.

## Build

Requirements: Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/), and [Node.js](https://nodejs.org/) (for the
blueprint writer).

```
dotnet build -c Release          # restores bp/node_modules with `npm ci` if it's missing
dotnet publish -c Release -o publish
```

`publish/SatisfactoryPlanner.exe` is a single self-contained file (the blueprint writer is bundled inside it; exporting
blueprints needs Node.js installed on the machine that runs it).

### Game data

The repository contains no game data. On first start the app downloads recipe / item data and icons from
[satisfactory.wiki.gg](https://satisfactory.wiki.gg/) into `%LOCALAPPDATA%\SatisfactoryPlanner\data`; translated item
names are read from a local Satisfactory install (`CommunityResources/Docs`). To bundle data into a build, place it in
`Data/` (ignored by Git) before building.

## How the layout planner works

See [docs/LAYOUT_DESIGN.md](docs/LAYOUT_DESIGN.md): the pipeline, the rules learned in game, and the test procedure.

## Credits and disclaimer

This is a fan-made, non-commercial tool. It is **not affiliated with or endorsed by Coffee Stain Studios**.
Satisfactory, its names, data and artwork are © Coffee Stain Studios.

- Game data and icons: [satisfactory.wiki.gg](https://satisfactory.wiki.gg/) — text content under
  [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/); images are Coffee Stain Studios' artwork.
- Save / blueprint files: [@etothepii/satisfactory-file-parser](https://github.com/etothepii4/satisfactory-file-parser)
  (MIT), with [pako](https://github.com/nodeca/pako) (MIT and Zlib).

The planner's own code is under the [MIT licence](LICENSE).

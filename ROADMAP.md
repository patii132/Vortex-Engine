# Vortex Engine — Roadmap (updated 2026-10-11)

**The plan lives in the [GitHub milestones](https://github.com/shadow-kernel/Vortex-Engine/milestones).** This file summarises them;
the pinned issue #188 mirrors it.

**Goal:** when v5.0.0 closes, Vortex builds a complete **16+ player Battle Royale at Call of Duty: Black Ops quality** —
dedicated servers, texture streaming and world partition on a 2×2 km map, CoD movement and gunplay, squads, vehicles, voice
chat and matchmaking — shipped as a Project Hub template. Every issue that game needs carries the
[`br-blocker`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+is%3Aopen+label%3Abr-blocker) label.

Status legend: ✅ shipped on `main` · 🟡 partial · ⬜ open. Issue numbers refer to github.com/shadow-kernel/Vortex-Engine.

## Milestones (in order)

| Version | Milestone | Status | What it delivers |
|---|---|---|---|
| v2.8.0 | Windows · macOS · Linux — released 2026-10-04 | ✅ | macOS (Metal) + Linux (Vulkan) ports, Physics v2 (Jolt), editor-first hands |
| v2.9.0 | Global Asset Database — released 2026-10-05 | ✅ | machine-wide SHA-256 asset library, Library tab, Add to Project, duplicate-aware import, project indexer, maintenance, bundles, settings (#52–#65) |
| v2.10.0 | Asset Store & Claude Sound Studio — released 2026-10-05 | ✅ | Poly Haven / ambientCG / poly.pizza / Freesound / Kenney / Sketchfab providers feeding the library, Mixamo + Sonniss guided flows, license check + CREDITS.md, Claude Sound Studio with ElevenLabs / fal.ai / Stability backends and recipes (#66–#83) |
| v3.0.0 | Claude-Native Engine — released 2026-10-05 | ✅ | MCP server + 67 tools (68 in v3.0.1), Claude panel (the Claude sidebar in v3.0.3), safety (#84–#94, #99) — checked end to end with real Claude Code sessions; the cross-platform editor on Windows too (#183) with self-update and gamepads, the macOS DMG (#184), template packs (#299) + Update from Template (#186), docs sweep (#98), hardening + an installer test that plays an in-app update (#95), CI gate (#161), the v3.0 trailer and homepage |
| v3.1.0 | Physics v2 — released 2026-10-09 | ✅ | Jolt on every platform incl. Windows (#182), joints (#103), ragdolls (#104), render interpolation, the player on CharacterVirtual opt-in (#105, #187), physics materials (#107), debug layers (#106), kinematic movers in place (#362); the classic WPF editor retired (#311) |
| v3.2.0 | AI & Navigation — released 2026-10-09 | ✅ | Recast navmesh + DetourCrowd agents + AI Perception + Patrol Paths on every platform (#109 #110 #112 #114), behaviour trees with the visual editor and live debugging (#111), root motion (#113), the Horror monster (#115) and the Tactical combat bots (#193) |
| v3.3.0 | VFX — released 2026-10-10 | ✅ | the particle system on DirectX 12 + VFX editor, trails/beams, soft particles and collision (#117 #118 #121 #122), decals (#120), volumetric fog with light shafts (#119), weapon VFX (#178) for every camera (#194), the playable feature tour in the Tactical Shooter's Range |
| v3.4.0 | World | ✅ | terrain (#124), foliage (#125), water (#200, surfaces — buoyancy / swim / refraction stay open), the Texture-sky environment, weather as a game script, the **Open World template** (the desert oasis, the infected), environment MCP tools, the asset-pipeline tools |
| v3.4.5 | Streaming | ⬜ | foliage impostors (#199), splines (#202), HZB culling (#127), world partition (#197) + HLOD (#198), **texture streaming (#195)**, streaming-ready paks (#196), BCn compression (#179), async streaming (#128) |
| v3.5.0 | AAA Rendering: Look & Lighting | 🟡 | **moved before multiplayer (2026-10-11: the look first)** — scene exposure + selectable tonemapper (ACES / PBR Neutral), HDR sky through the tonemapper, aerial perspective (v3.4.3 ✅); next: dynamic GI (#257, the "Lumen" item: probe-based with ray-traced / raster-captured updates), TAA (#141) + FSR/MetalFX/DLSS + dynamic resolution, physical sky + time of day, dynamic GI, SSR (#155) + probes (#167), exposure + grading, DoF/motion blur (#168), HDR (#156), scopes, pipeline precompilation (#254–#262) |
| v3.6.0 | Multiplayer I: Netcode | ⬜ | design doc (#204), transport, headless dedicated server, tick + clock sync, replication, `[Replicated]`/RPC API, prediction, interpolation, hitboxes, lag compensation, interest management, netgraph, multiplayer play mode, replays, load tests, 8-player Tactical deathmatch (#145, #204–#219) |
| v3.7.0 | Animation v2 & Characters | ⬜ | state machine + blend trees (#146), blend spaces + aim offsets, retargeting (#148), turn-in-place, animation LOD for 64 characters, FP/TP weapon events, clip compression, character shading, operators (#220–#227, #142, #185) |
| v3.8.0 | Shooter Framework | ⬜ | `.vweapon` assets, attachments + camos, ballistics, damage + armor plates, downed/revive, inventory + loot, throwables, HUD toolkit, input actions + gamepad + aim assist, settings, first-person body shadow, shooter audio (#228–#242, #166) |
| v3.9.0 | Multiplayer II: Online Services | ⬜ | platform layer (Steam first, #159), relay + auth, parties, matchmaking with bot backfill, server hosting, voice chat, persistence, anti-cheat foundations (#243–#253) |
| v4.0.0 | XXL: 10x Performance | ⬜ | render graph, bindless, GPU culling + indirect draws, mesh shaders, job system, ECS core, frame allocators, async compute, profiler, benchmark CI (#131–#144) |
| v4.1.0 | Production & LiveOps | ⬜ | nightlies (#160), crash reports for games + servers, telemetry, delta patches, localization (#150) + typography (#149), accessibility, server deploy pipeline, console v2 (#163) (#263–#268) |
| v4.2.0 | Battle Royale Systems | ⬜ | vehicles, drop plane + freefall + parachute, zone, loot distribution, airdrops, match flow, squads + spectating, traversal, open-world navmesh, BR bots (#269–#279) |
| v5.0.0 | Battle Royale (16+ Players) | ⬜ | 2×2 km BR map, game mode, content set, front end, performance certification, public playtest, release (#280–#287) |

Backlog (no milestone — not needed for the Battle Royale): sequencer (#152), plugin system (#154), DXR (#157), visual
scripting (#158), viewport render modes (#162), bulk asset operations (#164), dependency graph UI (#165), Freesound
original-quality downloads (#289).

**Versioning:** the tag v2.8.0 (2026-10-04) is the Windows · macOS · Linux release, so the milestone that was planned as
"v2.8.0 – Global Asset Database" is v2.9.0 and the Asset Store moved to v2.10.0.

## Now
1. **The look first (user decision, 2026-10-11): the rendering is finished before multiplayer starts.** v3.4.3 brings the
   exposure pipeline (scene exposure in stops before the tonemapper, ACES / Khronos PBR Neutral per scene, the HDR sky
   through the same curve, the sun's glow in the fog) and the oasis to the horizon (4 km far dunes, mountains, a
   seamless 1024-texel terrain atlas). Next in **v3.5.0 — AAA Rendering**: dynamic GI (#257 — the "Lumen" item:
   probe volumes with ray-traced updates where the GPU can, raster captures elsewhere), TAA (#141) + upscaling (#255),
   large-world shadows (#201), physical sky (#256), auto-exposure + LUTs (#258).
2. **v3.4.5 — Streaming** stays the performance half: impostors (#199), HLOD (#198), texture streaming (#195),
   BCn (#179), world partition (#197).
3. **Stability:** every crash is a bug. The editor logs fatal errors with the last console lines
   (`<appdata>/VortexEngine/logs/editor-errors.log`), a native crash on Windows writes `crash-<time>.dmp` + `.txt`
   next to it, a lost DX12 device is named in the log, and a broken MCP tool is skipped instead of ending the editor.
   Send those files with every crash report.

## Shipped

### v3.0.0 — Claude-Native Engine (released 2026-10-05; v3.0.1 the same day)
- ✅ **Vortex MCP server** in the editor (`127.0.0.1` only) with 67 tools (68 in v3.0.1): scenes, entities, materials, custom shaders
  with the real compiler errors, scripts with line-accurate errors, prefabs, model import, grid / scatter / snap, audio,
  the mixer and generated sounds, play mode, screenshots and the console. Every call is one undo step, changing tools
  offer a dry run, and *Tools ▸ Claude ▸ Operations…* shows each call's file diffs and reverts any of them.
  *Connect Claude Code / Desktop…* writes the project's `.mcp.json` or gives the one command. Checked with real Claude
  Code sessions: one prompt builds and sounds a horror corridor in under three minutes
  ([trailer](docs/showcase/vortex-3.0-claude.webp)). v3.0.1 adds `create_sound_container` (68 tools). Docs:
  [Claude in the Editor](https://engine.vortexstudio.dev/docs/#/claude), [Claude Tools](https://engine.vortexstudio.dev/docs/#/claude-tools).
- ✅ **Claude panel** in the editor with the user's own Anthropic key: it sees the viewport and asks before it changes
  things. v3.0.3 turns it into the **Claude sidebar**: Ask / Agent, model, effort and context per message, sign-in with
  the Anthropic account, on the official Anthropic C# SDK.
- ✅ **One editor on Windows, macOS and Linux** (#183): the installer's *Vortex Engine* is the cross-platform editor
  (self-contained .NET 10, DX12 viewport in a child window) with self-update, gamepads (DualSense / DualShock HID and
  XInput on Windows, SDL3 elsewhere) and the same window header everywhere. The WPF editor stayed one release as
  *Vortex Engine (Classic)*; #311 retired it in v3.1 (Windows ships one editor, no .NET Framework, no MSBuild).
- ✅ **macOS DMG** on every release (#184); the app and the games it exports carry their own libraries.
- ✅ **Template packs** (#299): the Horror Starter and Tactical Shooter content downloads once per engine version.
  *Update from Template…* (#186) shows line diffs, backs up what it overwrites, and replacing scenes is opt-in.
- ✅ **Hardening** (#95): audio follows parent transforms, spawned and destroyed sounds behave, HRTF fades with distance,
  `Settings.SetMasterVolume` works, lights come back after Stop, Toggle Active is kept, Windows games flush their saves
  on quit. Before anything is published, the release workflow installs the previous release, updates it, plays an
  in-app update through and smokes the installed editor.
- ✅ **Docs** (#98): Getting Started, Horror Essentials, Audio; the Feature Status Matrix was re-checked against the code.
  Since v3.0.3 the documentation lives only on the [docs website](https://engine.vortexstudio.dev/docs/), and CI compiles
  its C# samples.

### v2.10.0 — Asset Store & Claude Sound Studio (released 2026-10-05)
- ✅ **Store tab** in the Asset Browser (`Managed/Vortex.Core/Store/`): Poly Haven (models, PBR sets, HDRIs, MD5-checked),
  ambientCG (zip → wired `.vmat`, renders in the Material Editor), Kenney (curated packs), poly.pizza, Freesound
  (audition, 2,000/day budget), Sketchfab (NC/ND hidden by default) — the user's own keys only. Mixamo (Downloads
  watcher instead of a WebView — Avalonia has none) and Sonniss (folder indexer) as guided flows.
- ✅ **One download pipeline**: queue, cancel / retry with back-off, HTTP range resume, checksum verification, disk-space
  check, import by kind into the library with source / author / license, optional Add to Project; nothing downloads twice.
- ✅ **Licenses**: license, author and source in the catalog and `.vmeta`, badges everywhere, `CREDITS.md` on export, a
  license check before builds with jump-to-asset links and an explicit ShareAlike / GPL confirmation; non-redistributable
  assets never go into library bundles.
- ✅ **Claude Sound Studio** (*Window → Sound Studio…*): procedural (offline), ElevenLabs, fal.ai (CassetteAI, Stable Audio
  Open) and Stability AI (Stable Audio 2.5 up to 3:10 min, Stable Audio 3 up to 6:20 min) backends; Claude designs the
  prompts in a streamed tool loop and refines them from feedback; takes saved with their recipe ("Open in Sound Studio"
  makes a sibling). Docs: [Asset Store](https://engine.vortexstudio.dev/docs/#/asset-store), [Sound Studio](https://engine.vortexstudio.dev/docs/#/sound-studio).
- ✅ **Library and Asset Store tabs** next to Project and Console (⌘/Ctrl+7, ⌘/Ctrl+8); Shift/⌘-double-click previews a
  library model, texture or material in the viewer without copying it into the project.
- ✅ **Store materials at their true scale**: real-world size from the provider, a tiled copy when dropped on a floor or
  wall primitive, *Fit to Selected Object* in the Material Editor; spheres, cylinders and cones were lit inside-out on
  every backend (fixed, guarded by `VortexGeometryTest`).

### v2.9.0 — Global Asset Database (released 2026-10-05)
- ✅ **Machine-wide library** (`Editor/Core/Assets/Library/`): SQLite catalog through the OS SQLite (no new dependency),
  content-addressed blob store (`blobs/ab/<sha256>`), thumbnails per hash, WAL so several editors share it. Every import
  is hashed into its `.vmeta` (`ContentHash`) and registered in the background; models keep their folder as companions.
- ✅ **Library tab** in the Asset Browser: virtualised grid, type chips, search, tags, saved filters, details (tags, source,
  license, projects using it), audition, Add to Project / Add to Scene / drag into the viewport.
- ✅ **Duplicate-aware import dialog** ("Already in your library"), **project indexer**, **tag manager**, **maintenance**
  (stats, verify, orphan fix, garbage collection), **bundles** (`.vlib.zip`), **settings** (move with verification, size
  cap, type rules), **backfill** of content hashes.
- ✅ `Managed/Vortex.Core.Tests` — headless core tests (`dotnet run --project Managed/Vortex.Core.Tests`), plus the editor
  smoke check `VORTEX_SMOKE_ONLY=library`. Docs: [Asset Library](https://engine.vortexstudio.dev/docs/#/asset-library),
  [design](https://engine.vortexstudio.dev/docs/#/design-asset-database).
- ✅ **Windows installer back**: the v2.8.0 release build had failed (DX12 lacked the UTF-8 overlay calls; the Visual
  Studio engine project lacked `RenderBackend.cpp`) and the Inno Setup download link now returns HTML (installed via
  Chocolatey now), so v2.8.0 shipped without an installer — all fixed; v2.9.0 and v2.10.0 ship installers.

### Since 2.8 (on main)
- ✅ **Linux port**: Vulkan backend via SDL GPU (GLSL → SPIR-V at build time), the editor and the player on X11
  (`Scripts/linux-dev.sh`).
- ✅ **Ragdolls** (#104): a Ragdoll component builds Jolt bodies + swing-twist / hinge joints from any humanoid skeleton
  at its current pose; `Ragdoll.Activate(EntityId)`; shots push the limb they hit.
- ✅ **Tactical Shooter template**: Call-of-Duty-style gun range, two animated first-person weapon packs, iron-sight
  ADS, recoil; the player has a **third-person operator body** for every camera but the local one (third-person
  weapons in the hands, left hand IK'd to the foregrip, crouch/slide via Foot IK, ragdoll on death).
- ✅ **Joints** (#103: hinge, ball, slider, fixed, distance, motors, breakable), **render interpolation** between fixed
  physics steps, rig-generic **Hand Pose, Look-At IK and Foot IK** (#147).
- 🟡 **VFX** (Particle System + VFX editor) and **AI & navigation** (navigation window, agents, perception) — merged as
  work in progress.
- ✅ The documented build scripts (`tools/macos/make-app.sh`, `tools/macos/dev.sh`, `tools/make-runtime-pack.*`) are in
  the repository.

### 2.8.0 (released 2026-10-04)
- ✅ **macOS port** (#180): Metal backend via SDL GPU, .NET 10 `Vortex.Core` / `Vortex.Player` / Avalonia `Vortex.Editor`,
  `tools/macos/make-app.sh --install --dmg` builds and installs `Vortex Editor.app`; branded exports for macOS /
  Windows / Linux with runtime packs (`tools/make-runtime-pack.*`).
- ✅ **Physics v2 — Jolt** (#101, #102): `Engine/Physics/PhysicsWorld` + C-ABI exports, managed `PhysicsService` (60 Hz
  fixed step, transform write-back, contact events → `OnCollisionEnter/OnTrigger*`), script API
  `Physics.AddForce/AddImpulse(AtPoint)/AddTorque/SetVelocity/OverlapSphere/…`, compound shapes, physics materials,
  debug lines, CharacterVirtual API. The Visual Studio build compiles the stub until Jolt is added to the .vcxproj (#182).
- ✅ **Character controller fixes**: edge pops / one-frame drops on curbs, step-up at high frame rates, jump swallowed by
  the step-up settle; camera step smoothing; raycast mantle in the template.
- ✅ **Editor-first hands**: `HandPose` component (per-finger curl, live preview), weapon sockets as child entities
  (`Grip`, `SupportGrip`, `Sight`, `Muzzle`, `Eject`, `MagWell`), auto-grip from the rig's hand geometry.
- ✅ **Horror Starter**: open night map "Yard" (33 Poly Haven CC0 props, 64 dynamic bodies), CoD movement, recorded
  weapon sounds in random containers, `Player.ventity` and `Character_Soldier.ventity` blueprints.

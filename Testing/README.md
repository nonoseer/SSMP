# Automated co-op tests

Real game clients play scripted steps against a local standalone server and check each other's state.
Every run prints one line: `RESULT: PASS` or `RESULT: FAIL`.

- `SSMP.TestBot/`: test-only BepInEx plugin. It does nothing unless `SSMP_TEST_DIR` is set, and is never shipped.
  - Redirects the game's save directory into the run folder and refuses to run if that fails.
  - Keeps clients running and muted when unfocused, and skips the first-launch language picker.
  - Runs the suite steps for its role and writes `result.txt`, `testbot.log` and in-game screenshots on failure.
- `run-coop-test.sh <suite>`: one run. Starts `SSMPServer` and one client per role, waits, collects logs.
  Also checksums the owner's real save folder before and after, and fails the run if it changed.
- `run-suite.sh [suites...]`: every suite, plus its mutation twins. A suite must fail with each mutation
  in its `mutations` list, otherwise it proves nothing.
- `build.sh`: builds SSMP, the bot and the server.

## Setup (macOS, Apple Silicon)

1. Requires the .NET 10 SDK (build) and the .NET 9 runtime (server, `DOTNET9` overrides its path).
2. Make one test copy of the game per role in `$SILKSONG_TEST_ROOT/<role>` (default
   `~/Desktop/Development/silksong-test/A`, `B`, ...). Never use your real install. `cp -c` makes APFS clones
   that take almost no space.
3. Install [BepInExPack_Silksong](https://thunderstore.io/c/hollow-knight-silksong/p/silksong_modding/BepInExPack_Silksong/)
   into each copy. Its doorstop library is x86_64 only, so change the last line of `run_bepinex.sh` to run
   under Rosetta. `/usr/bin/arch` strips `DYLD_*` variables, so pass them through:
   ```sh
   exec arch -x86_64 -e DYLD_LIBRARY_PATH="$DYLD_LIBRARY_PATH" \
       -e DYLD_INSERT_LIBRARIES="${doorstop_directory}${doorstop_name}" "$executable_path" "$@"
   ```
4. Put a save fixture in `$SILKSONG_TEST_ROOT/fixtures/default/<steam user id>/` (`user1.dat`, `shared.dat`).
   It is copied into every client's redirected save folder for each run.
5. `Testing/build.sh`, then `Testing/run-suite.sh`.

Run folders land in `$SILKSONG_TEST_ROOT/runs/<time>-<suite>/` with the server log, each client's bot log,
BepInEx log, screenshots and result.

## Suites

A suite lists steps per role. Steps run in order; any failure ends the run.

| Op | Arguments | Does |
|---|---|---|
| `wait_menu` | `timeout` | Main menu reached, SSMP initialized, save isolation verified |
| `connect` | `address`, `port` | Connects from the main menu as `Bot<role>` (SSMP then asks for a save) |
| `load_slot` | `slot` | Loads the save slot and waits for the hero to accept input |
| `barrier` | `name` | Waits until every role reached the barrier |
| `wait` | `seconds` | Waits |
| `move` | `dx`, `dy`, `settle` | Teleports the hero and checks it really moved |
| `publish_position` | `key` | Shares the hero's position with the other roles |
| `assert_players_in_scene` | `count` | At least `count` remote players in the local scene |
| `assert_remote_at` | `player`, `key`, `tolerance` | Remote player is seen at the published position |

Mutations (`--mutate <name>`): `no_position_updates`.

## Last result

2026-10-17, macOS 27, game 1.0.30000, SSMP `coop/test-harness` on `entity-sync`:

```
PASS  smoke-see-each-other
PASS  smoke-see-each-other [twin no_position_updates failed as expected]
2/2 passed
```

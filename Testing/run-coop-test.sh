#!/bin/bash
# Runs one co-op test: starts the standalone SSMP server and one real game client per role in the suite,
# waits for every client's test bot to finish, collects the logs and prints one line: RESULT: PASS or RESULT: FAIL.
#
# Usage: Testing/run-coop-test.sh <suite.json> [--mutate <name>]
#
# Each role needs a test copy of the game with BepInEx at $SILKSONG_TEST_ROOT/<role> (see Testing/README.md).
# The owner's install and saves are never touched: saves are redirected into the run folder by the test bot,
# and the real save folder is checksummed before and after the run.
set -euo pipefail

usage() { echo "Usage: $0 <suite.json> [--mutate <name>]" >&2; exit 2; }

[ $# -ge 1 ] || usage
SUITE="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
shift
MUTATE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --mutate) MUTATE="${2:?}"; shift 2 ;;
        *) usage ;;
    esac
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="${SILKSONG_TEST_ROOT:-$HOME/Desktop/Development/silksong-test}"
FIXTURE="${SSMP_TEST_FIXTURE:-$ROOT/fixtures/default}"
REAL_SAVES="$HOME/Library/Application Support/unity.Team-Cherry.Silksong"
DOTNET9="${DOTNET9:-/usr/local/share/dotnet/dotnet}"
SERVER_BIN="$ROOT/server-bin"

suite_field() { python3 -c "import json,sys; v=json.load(open(sys.argv[1])).get(sys.argv[2], sys.argv[3]); print(json.dumps(v) if isinstance(v,bool) else v)" "$SUITE" "$1" "$2"; }
NAME="$(suite_field name unnamed)"
PORT="$(suite_field port 26960)"
FULL_SYNC="$(suite_field fullSync false)"
TIMEOUT="$(suite_field timeoutSeconds 420)"
ROLES=($(python3 -c "import json,sys; print(' '.join(json.load(open(sys.argv[1]))['steps'].keys()))" "$SUITE"))

RUN="$ROOT/runs/$(date +%Y%m%d-%H%M%S)-$NAME${MUTATE:+-mut-$MUTATE}"
mkdir -p "$RUN"
echo "Run: $RUN"
echo "Suite: $NAME, roles: ${ROLES[*]}, port $PORT, full sync $FULL_SYNC, timeout ${TIMEOUT}s${MUTATE:+, mutation $MUTATE}"

fail_early() { echo "RESULT: FAIL ($1)"; exit 1; }

# Preflight: test copies, fixture and build output exist
for role in "${ROLES[@]}"; do
    [ -x "$ROOT/$role/run_bepinex.sh" ] || fail_early "no test copy with BepInEx at $ROOT/$role"
done
[ -d "$FIXTURE" ] || fail_early "no save fixture at $FIXTURE"
if lsof -nP -iUDP:"$PORT" > /dev/null 2>&1; then
    fail_early "UDP port $PORT is in use, probably a server left over from an earlier run"
fi
[ -f "$SERVER_BIN/SSMPServer.dll" ] || fail_early "no server build at $SERVER_BIN (run Testing/build.sh)"
SSMP_OUT="$REPO/bin/Release/netstandard2.1"
BOT_OUT="$REPO/bin/TestBot/Release/netstandard2.1"
[ -f "$SSMP_OUT/SSMP.dll" ] && [ -f "$BOT_OUT/SSMP.TestBot.dll" ] || fail_early "no plugin build (run Testing/build.sh)"

# Install the current build into every test copy
for role in "${ROLES[@]}"; do
    plugins="$ROOT/$role/BepInEx/plugins"
    mkdir -p "$plugins/SSMP" "$plugins/SSMP.TestBot"
    cp "$SSMP_OUT"/{SSMP.dll,SSMP.pdb,BouncyCastle.Cryptography.dll} "$plugins/SSMP/"
    cp "$BOT_OUT"/{SSMP.TestBot.dll,SSMP.TestBot.pdb} "$plugins/SSMP.TestBot/"
done

snapshot_real_saves() {
    [ -d "$REAL_SAVES" ] || return 0
    # AppConfig.ini holds display settings, which the game rewrites on every launch
    (cd "$REAL_SAVES" && find . -type f ! -name AppConfig.ini -print0 | sort -z | xargs -0 shasum -a 256)
}
snapshot_real_saves > "$RUN/real-saves-before.txt"

PIDS=()
# Kills everything this run started; the patterns are specific to this run's port and test copies
cleanup() {
    local signal
    # The server ignores SIGTERM, so escalate to SIGKILL after a grace period
    for signal in TERM KILL; do
        for pid in "${PIDS[@]}"; do kill -s "$signal" "$pid" 2>/dev/null || true; done
        pkill -"$signal" -f "SSMPServer.dll $PORT\$" 2>/dev/null || true
        for role in "${ROLES[@]}"; do pkill -"$signal" -f "$ROOT/$role/Hollow Knight Silksong.app/" 2>/dev/null || true; done
        if [ "$signal" = TERM ]; then sleep 3; fi
    done
}
trap cleanup EXIT

# Server, with its own copy so settings, keys and logs stay in the run folder
cp -R "$SERVER_BIN" "$RUN/server"
printf '{\n  "Port": %s,\n  "FullSynchronisation": %s\n}\n' "$PORT" "$FULL_SYNC" > "$RUN/server/consolesettings.json"
(cd "$RUN/server" && exec "$DOTNET9" SSMPServer.dll "$PORT" < /dev/null > server.log 2>&1) &
PIDS+=($!)
for _ in $(seq 1 60); do
    grep -q "Starting NetServer" "$RUN/server/server.log" 2>/dev/null && break
    sleep 0.5
done
grep -q "Starting NetServer" "$RUN/server/server.log" || fail_early "server did not start, see $RUN/server/server.log"

# Clients
for role in "${ROLES[@]}"; do
    slot="$RUN/$role"
    mkdir -p "$slot/saves"
    cp -R "$FIXTURE"/. "$slot/saves/"
    rm -f "$ROOT/$role/BepInEx/LogOutput.log"
    (
        cd "$ROOT/$role"
        export SSMP_TEST_DIR="$slot" SSMP_TEST_RUN_DIR="$RUN" SSMP_TEST_ROLE="$role" \
            SSMP_TEST_SUITE="$SUITE" SSMP_TEST_CLIENTS="${#ROLES[@]}" SSMP_TEST_MUTATE="$MUTATE"
        exec ./run_bepinex.sh -screen-fullscreen 0 -screen-width 960 -screen-height 540
    ) > "$slot/launcher.log" 2>&1 &
    PIDS+=($!)
    sleep 5
done

# Wait for every client's result, a dead client or the timeout
deadline=$((SECONDS + TIMEOUT))
status="timeout"
while [ $SECONDS -lt $deadline ]; do
    done_count=0
    for role in "${ROLES[@]}"; do
        [ -f "$RUN/$role/result.txt" ] && done_count=$((done_count + 1))
    done
    if [ $done_count -eq ${#ROLES[@]} ]; then status="finished"; break; fi
    # A client that already failed ends the run; the others would only wait at the next barrier
    if grep -qs "^FAIL" "$RUN"/*/result.txt; then status="client failed"; break; fi
    sleep 1
done
sleep 2
cleanup

# Collect logs
for role in "${ROLES[@]}"; do
    cp "$ROOT/$role/BepInEx/LogOutput.log" "$RUN/$role/LogOutput.log" 2>/dev/null || true
done

# Save guard
snapshot_real_saves > "$RUN/real-saves-after.txt"
save_guard="ok"
if ! diff -q "$RUN/real-saves-before.txt" "$RUN/real-saves-after.txt" > /dev/null; then
    save_guard="CHANGED"
fi

result="PASS"
echo "--- $NAME ($status)"
for role in "${ROLES[@]}"; do
    r="$(cat "$RUN/$role/result.txt" 2>/dev/null || echo "FAIL: no result (client did not finish)")"
    echo "  $role: $r"
    case "$r" in PASS) ;; *) result="FAIL" ;; esac
done
echo "  real save folder: $save_guard"
if [ "$save_guard" != "ok" ]; then
    echo "  !!! The owner's save folder changed during the run. Compare $RUN/real-saves-*.txt" >&2
    result="FAIL"
fi
echo "RESULT: $result"
[ "$result" = "PASS" ]

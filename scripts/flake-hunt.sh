#!/usr/bin/env bash
#
# Run the test suite N times and name whatever fails.
#
# This exists because of issue #24: a run reported "Failed: 1, Passed: 261" and
# the test's name was lost, because the run had been piped through `tail -2`.
# The summary line and the `Failed <TestName>` line that identifies the culprit
# are adjacent, and a tail keeps the wrong one. So the whole of every run is
# captured to a file here, and only the greps are lossy.
#
# Usage:
#   scripts/flake-hunt.sh                 # 30 runs on an idle machine
#   scripts/flake-hunt.sh -n 50           # 50 runs
#   scripts/flake-hunt.sh -l              # 30 runs under a parallel build loop
#   scripts/flake-hunt.sh -f CategorizerTests   # only matching tests
#   scripts/flake-hunt.sh -b              # add --blame-hang-timeout, to tell a
#                                         #   hang from a wrong assertion
#
# Logs from failing runs are kept; logs from green runs are deleted, so an
# empty output directory means an unbroken green streak.

set -uo pipefail

runs=30
filter=""
load=0
blame=0
outdir="${TMPDIR:-/tmp}/flake-hunt-$(date +%Y%m%d-%H%M%S)"

while getopts "n:f:o:lbh" opt; do
    case "$opt" in
        n) runs="$OPTARG" ;;
        f) filter="$OPTARG" ;;
        o) outdir="$OPTARG" ;;
        l) load=1 ;;
        b) blame=1 ;;
        h) sed -n '2,25p' "$0"; exit 0 ;;
        *) exit 2 ;;
    esac
done

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo" || exit 1
mkdir -p "$outdir"

# Build once, then --no-build every run: a rebuild between iterations would make
# each run's timing a measurement of the compiler instead of the suite.
echo "Building..."
if ! dotnet build > "$outdir/build.log" 2>&1; then
    echo "Build failed. See $outdir/build.log"
    exit 1
fi

args=(test --no-build)
[ -n "$filter" ] && args+=(--filter "$filter")
[ "$blame" = 1 ] && args+=(--blame-hang-timeout 120s)

load_pid=""
if [ "$load" = 1 ]; then
    # Approximates the condition issue #24's failure appeared under: the first
    # run immediately after a fresh build, machine still settling from a
    # compile. A rebuild loop of the app project keeps it settling throughout.
    #
    # Its own obj/bin would collide with the suite's, so it builds into a
    # scratch output directory.
    #
    # The loop watches its parent rather than trusting the EXIT trap below: a
    # trap does not fire on SIGKILL, and an orphaned `while true` around
    # `dotnet build` would sit there burning all eight cores indefinitely on a
    # machine nobody is watching. Checking the parent costs nothing and makes
    # the worst case "stops within one build" instead of "stops when someone
    # notices".
    hunter=$$
    (
        while kill -0 "$hunter" 2>/dev/null; do
            dotnet build "$repo/MealPlanner.csproj" \
                --no-incremental \
                -p:BaseOutputPath="$outdir/load-bin/" \
                -p:BaseIntermediateOutputPath="$outdir/load-obj/" \
                > /dev/null 2>&1
        done
    ) &
    load_pid=$!
    # Never pkill a pattern that matches this script's own command line
    # (AGENTS.md); the PID is recorded and killed by PID.
    echo "Load generator running as pid $load_pid"
fi

cleanup() {
    if [ -n "$load_pid" ]; then
        kill "$load_pid" 2>/dev/null
        # The loop spawns a fresh dotnet each iteration; kill the whole group.
        pkill -P "$load_pid" 2>/dev/null
        wait "$load_pid" 2>/dev/null
    fi
}
trap cleanup EXIT INT TERM

failures=0
echo "Running $runs iterations. Logs: $outdir"
for i in $(seq 1 "$runs"); do
    log="$outdir/run-$(printf '%03d' "$i").log"
    dotnet "${args[@]}" > "$log" 2>&1
    status=$?

    summary=$(grep -E "^(Passed|Failed)!" "$log" | head -1)
    if [ "$status" -eq 0 ]; then
        printf '  run %3d  ok    %s\n' "$i" "${summary:-（no summary line）}"
        rm -f "$log"
        continue
    fi

    failures=$((failures + 1))
    printf '  run %3d  FAIL  %s\n' "$i" "${summary:-（no summary line; see log）}"
    # The whole point of the exercise: the name, the message and the stack. Not
    # the last two lines.
    awk '/^[[:space:]]*Failed /{show=30} show{print "        " $0; show--}' "$log"
    echo "        (full log: $log)"
done

echo
if [ "$failures" -eq 0 ]; then
    echo "$runs/$runs green. Nothing kept in $outdir."
else
    echo "$failures/$runs failed. Logs kept in $outdir."
fi
exit 0

#!/bin/bash
# Runs every suite in Testing/suites (or the ones given) and its mutation twins.
# A suite must PASS as is and FAIL with each mutation listed in its "mutations" field, otherwise it tests nothing.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
[ $# -gt 0 ] && suites=("$@") || suites=("$HERE"/suites/*.json)

summary=()
failed=0
for suite in "${suites[@]}"; do
    name="$(basename "$suite" .json)"
    if "$HERE/run-coop-test.sh" "$suite"; then
        summary+=("PASS  $name")
    else
        summary+=("FAIL  $name"); failed=$((failed + 1))
    fi

    for mutation in $(python3 -c "import json,sys; print(' '.join(json.load(open(sys.argv[1])).get('mutations', [])))" "$suite"); do
        if "$HERE/run-coop-test.sh" "$suite" --mutate "$mutation"; then
            summary+=("FAIL  $name [twin $mutation passed, the test can't fail]"); failed=$((failed + 1))
        else
            summary+=("PASS  $name [twin $mutation failed as expected]")
        fi
    done
done

echo
echo "=== Suite summary ($(date '+%Y-%m-%d %H:%M'))"
printf '%s\n' "${summary[@]}"
echo "$(( ${#summary[@]} - failed ))/${#summary[@]} passed"
[ $failed -eq 0 ]

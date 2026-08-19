#!/usr/bin/env bash
# Post one comment on an issue or PR as a GitHub App — and nothing else.
#
# Deliberately NOT a general `gh api` wrapper. It exists so a single narrow
# permission rule can let an agent comment unattended without also granting
# every call the App's token can make: the endpoint and the method are fixed
# here, and the issue number is checked to be digits, so no argument can steer
# the request somewhere else.
#
# Note the consequence, which is the point rather than an oversight: this can
# post a comment but cannot edit or delete one. Cleanup goes through `gh api`
# by hand.
#
# This is a core, not the surface. Run it through its shims —
# ../coder-comment.sh and ../reviewer-comment.sh — because the *filename* is
# what a permission rule names, and one file per identity is what keeps a rule
# from granting both. `--as` here is an ordinary parameter, not a boundary.
#
# Usage (via a shim):
#   scripts/coder-comment.sh 32 "Body text"
#   scripts/reviewer-comment.sh 32 < body.md
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$here/common.sh"

# The name the caller typed: after the shim's `exec` our own $0 is this file,
# which is not a command anyone ran.
me="${MEALPLANNER_INVOKED_AS:-$(basename "${BASH_SOURCE[0]}")}"
usage="$me <issue-or-pr-number> [body]"

if [[ "${1:-}" != "--as" ]]; then
    echo "usage: $usage" >&2
    exit 2
fi
# No apostrophe in a ${var:?word} message: bash honours a single quote inside it
# even within double quotes, so "the App-s ID" written properly would open a
# string that never closes and the whole script would fail to parse.
role="${2:?--as needs a role, e.g. coder}"
shift 2

number="${1:-}"
if [[ ! "$number" =~ ^[0-9]+$ ]]; then
    echo "usage: $usage" >&2
    echo "       the body may come on stdin instead of as an argument" >&2
    exit 2
fi
shift

body="$(read_body "$me" "$usage" body "$@")" || exit
require_nonblank "$me" "$body" comment

# Build the payload with jq rather than interpolating into JSON: bodies carry
# quotes, newlines and backticks as a matter of course.
jq -n --arg body "$body" '{body: $body}' \
    | GH_TOKEN="$("$here/app-token.sh" --as "$role")" gh api --input - \
        "repos/$app_repo/issues/$number/comments" \
        --jq '"posted as \(.user.login) — \(.html_url)"'

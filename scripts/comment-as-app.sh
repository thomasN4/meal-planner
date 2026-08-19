#!/usr/bin/env bash
# Post one comment on an issue or PR as the GitHub App — and nothing else.
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
# Usage:
#   scripts/comment-as-app.sh 32 "Body text"
#   scripts/comment-as-app.sh 32 < body.md
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="${MEALPLANNER_APP_REPO:-thomasN4/meal-planner}"

number="${1:-}"
if [[ ! "$number" =~ ^[0-9]+$ ]]; then
    echo "usage: comment-as-app.sh <issue-or-pr-number> [body]" >&2
    echo "       the body may come on stdin instead of as an argument" >&2
    exit 2
fi
shift

if (($# > 1)); then
    echo "comment-as-app.sh: too many arguments — quote the body as one" >&2
    exit 2
fi

if (($# == 1)); then
    body="$1"
elif [[ -t 0 ]]; then
    # Reading stdin here is right for the documented `< body.md` form, but with a
    # terminal on stdin it would sit waiting for EOF with nothing on screen saying
    # so — a usage error that reads as a hung script. An unattended caller has
    # stdin closed or redirected and never reaches this branch.
    echo "comment-as-app.sh: no body — pass it as an argument or redirect one in" >&2
    echo "usage: comment-as-app.sh <issue-or-pr-number> [body]" >&2
    exit 2
else
    body="$(cat)"
fi

if [[ -z "${body//[[:space:]]/}" ]]; then
    echo "comment-as-app.sh: refusing to post an empty comment" >&2
    exit 2
fi

# Build the payload with jq rather than interpolating into JSON: bodies carry
# quotes, newlines and backticks as a matter of course.
jq -n --arg body "$body" '{body: $body}' \
    | GH_TOKEN="$("$here/app-token.sh")" gh api --input - \
        "repos/$repo/issues/$number/comments" \
        --jq '"posted as \(.user.login) — \(.html_url)"'

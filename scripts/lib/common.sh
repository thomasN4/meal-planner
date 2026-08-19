#!/usr/bin/env bash
# Shared helpers for the lib/ cores. Sourced, never executed.
#
# `read_body` lives here rather than being copied into comment.sh and
# file-issue.sh because its middle branch carries a reason (below) that would
# drift the moment there were two of it.

# The repo every core talks to. One override, and no way to steer a single call.
app_repo="${MEALPLANNER_APP_REPO:-thomasN4/meal-planner}"

# Resolve a body from a single optional argument, else stdin. Echoes it on
# stdout, so callers use `body="$(read_body …)" || exit` — the usage branches
# below exit the substitution's subshell, and that propagates.
#   read_body <me> <usage-line> <what> [arg...]
read_body() {
    local me="$1" usage="$2" what="$3"
    shift 3

    if (($# > 1)); then
        echo "$me: too many arguments — quote the $what as one" >&2
        exit 2
    fi

    if (($# == 1)); then
        printf '%s' "$1"
    elif [[ -t 0 ]]; then
        # Reading stdin is right for the documented `< body.md` form, but with a
        # terminal on stdin it would sit waiting for EOF with nothing on screen
        # saying so — a usage error that reads as a hung script. An unattended
        # caller has stdin closed or redirected and never reaches this branch.
        echo "$me: no $what — pass it as an argument or redirect one in" >&2
        echo "usage: $usage" >&2
        exit 2
    else
        cat
    fi
}

# Refuse whitespace-only text. Separate from read_body because file-issue.sh
# applies it to a title that arrives as a flag rather than through the ladder.
require_nonblank() {
    local me="$1" value="$2" what="$3"
    if [[ -z "${value//[[:space:]]/}" ]]; then
        echo "$me: refusing to post an empty $what" >&2
        exit 2
    fi
}

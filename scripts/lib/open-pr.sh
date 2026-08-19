#!/usr/bin/env bash
# Push the current branch and open a pull request as the GitHub App.
#
# Companion to comment.sh, and narrow in the same way: it pushes the
# branch you are on and opens one PR from it. It cannot force-push, delete a
# branch, merge, or call anything else — a history rewrite stays a manual git
# command, the way comment cleanup stays a manual gh call.
#
# It refuses to push the default branch. An agent opening a PR should never be
# one typo away from writing to main, and this script exists to be handed an
# unattended permission rule.
#
# The App token reaches git through a credential helper reading the
# environment, never through the remote URL or a command line — a token in
# argv is readable by every process on the machine for as long as the push
# runs, and one baked into a remote URL outlives the push in .git/config.
#
# This is a core, not the surface — see the note in comment.sh. Its only shim is
# ../coder-open-pr.sh. There is deliberately **no reviewer-open-pr.sh**: measured
# 2026-08-19 the reviewer App is installed with `contents: read` and cannot push
# at all, and a capability an App does not have is best represented by a file
# that does not exist. The role check below is what is left of that as defence in
# depth — only a wrongly-written shim can now reach it — and it still refuses
# before minting a token, rather than 403-ing at the push after the stray-author
# check has printed.
#
# Usage (via the shim):
#   scripts/coder-open-pr.sh --title "Subject line" --body-file /path/to/body.md
#   scripts/coder-open-pr.sh --draft --title "…" --body "One-liner"
#   scripts/coder-open-pr.sh --title "…" --body-file - < body.md
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$here/common.sh"
repo="$app_repo"

me="${MEALPLANNER_INVOKED_AS:-$(basename "${BASH_SOURCE[0]}")}"
usage="$me [--draft] --title <title> [--body <text> | --body-file <path>] [--base <branch>]"

if [[ "${1:-}" != "--as" ]]; then
    echo "usage: $usage" >&2
    exit 2
fi
# No apostrophe in a ${var:?word} message: bash honours a single quote inside it
# even within double quotes, so "the App-s ID" written properly would open a
# string that never closes and the whole script would fail to parse.
role="${2:?--as needs a role, e.g. coder}"
shift 2

if [[ "$role" != "coder" ]]; then
    echo "$me: refusing to push as '$role' — only the coder App has contents:write." >&2
    echo "$me: the reviewer App is installed read-only on contents and can comment" >&2
    echo "$me: but not push; use scripts/$role-comment.sh for that." >&2
    exit 2
fi

# The bot's git identity. The numeric prefix is what links the noreply address
# to the account, so GitHub attributes the commit rather than showing an
# unrecognised author. Hardcoded to the coder rather than derived from the role,
# because the role is already pinned to 'coder' above — a table of identities
# here would imply the others can get this far.
bot_name="meal-planner-coder[bot]"
bot_email="316699224+meal-planner-coder[bot]@users.noreply.github.com"

title=""
body=""
body_file=""
base="main"
draft="false"

while (($# > 0)); do
    case "$1" in
        --title)     title="${2:?--title needs a value}"; shift 2 ;;
        --body)      body="${2:?--body needs a value}"; shift 2 ;;
        --body-file) body_file="${2:?--body-file needs a path}"; shift 2 ;;
        --base)      base="${2:?--base needs a branch}"; shift 2 ;;
        --draft)     draft="true"; shift ;;
        --identity)  printf '%s\n%s\n' "$bot_name" "$bot_email"; exit 0 ;;
        *) echo "$me: unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [[ -z "$title" ]]; then
    echo "usage: $usage" >&2
    echo "       --identity prints the git author name and email to commit as" >&2
    exit 2
fi

if [[ -n "$body_file" ]]; then
    if [[ "$body_file" == "-" ]]; then
        body="$(cat)"
    elif [[ -r "$body_file" ]]; then
        body="$(cat "$body_file")"
    else
        echo "$me: cannot read body file: $body_file" >&2
        exit 2
    fi
fi

branch="$(git symbolic-ref --quiet --short HEAD)" || {
    echo "$me: detached HEAD — check out a branch first" >&2
    exit 2
}

if [[ "$branch" == "$base" || "$branch" == "main" || "$branch" == "master" ]]; then
    echo "$me: refusing to push '$branch' — open PRs from a topic branch" >&2
    exit 2
fi

if [[ -n "$(git status --porcelain)" ]]; then
    echo "$me: working tree is dirty; commit or stash first" >&2
    exit 2
fi

# A PR opened by the bot whose commits are authored by a person reads worse
# than either end done consistently, so say so rather than papering over it.
# Not fatal: the mismatch is a fact about commits that already exist, and this
# script does not rewrite history.
#
# Matched on the author EMAIL, by exact field comparison, for two reasons. It is
# the field GitHub attributes on (see the identity comment above). And this was a
# `grep -v "$bot_name"`, where grep read the `[bot]` in the name as a bracket
# expression — one character from {b,o,t} — so the pattern never matched the real
# author string, `grep -v` kept every line, and the warning fired on every branch
# including ones the bot wrote. `grep -F` fixes that instance; comparing a whole
# field cannot be re-broken by the next metacharacter someone puts in an identity.
# Tab-delimited so a display name with spaces in it cannot shift the field.
strays="$(git log "$base..HEAD" --format='%ae%x09%h %an <%ae>' \
    | awk -F'\t' -v bot="$bot_email" '$1 != bot { print $2 }')"
if [[ -n "$strays" ]]; then
    echo "$me: warning — these commits are not authored by the bot:" >&2
    # Through a pipeline, not `printf '  %s\n' "$strays"`: that passes one
    # newline-containing argument, consumes the format once, and indents only the
    # first line.
    printf '%s\n' "$strays" | sed 's/^/  /' >&2
    echo "$me: the PR will come from the bot, the commits will not." >&2
    echo "$me: commit with the bot identity next time via:" >&2
    echo "    export GIT_AUTHOR_NAME='$bot_name' GIT_COMMITTER_NAME='$bot_name'" >&2
    echo "    export GIT_AUTHOR_EMAIL='$bot_email' GIT_COMMITTER_EMAIL='$bot_email'" >&2
fi

token="$("$here/app-token.sh" --as "$role")"

# credential.helper reads the token from the environment, so it never appears
# in argv or on disk. One copy, used by both the push and the fetch below.
cred_helper='!f() { echo username=x-access-token; echo "password=$GH_APP_TOKEN"; }; f'

# No --set-upstream here: against an explicit URL it writes that URL into
# branch.<branch>.remote, which is the state the fixup below exists to avoid.
GH_APP_TOKEN="$token" git -c credential.helper="$cred_helper" \
    push "https://github.com/$repo.git" "refs/heads/$branch:refs/heads/$branch"

# Point the local branch at the canonical remote name, since the push above used
# an explicit URL rather than the 'origin' remote.
#
# Pushing to a URL creates no remote-tracking ref, so 'origin/<branch>' does not
# exist yet and --set-upstream-to has nothing to point at. This used to be one
# such call under `|| true`, which meant it always failed and never said so: the
# branch was left tracking a bare URL, and a later plain `git push` or `git pull`
# then went out through whatever ambient credential helper the machine has — as a
# person, which is the attribution hole this script exists to close. So fetch a
# real tracking ref first, and warn loudly if any of it does not land.
origin_slug="$(git remote get-url origin 2>/dev/null || true)"
origin_slug="${origin_slug%.git}"
origin_slug="${origin_slug#*github.com/}"
origin_slug="${origin_slug#*github.com:}"

if [[ "$origin_slug" != "$repo" ]]; then
    echo "$me: warning — 'origin' is not $repo, so '$branch' was left" >&2
    echo "$me: tracking whatever it tracked. Set its upstream yourself." >&2
elif GH_APP_TOKEN="$token" git -c credential.helper="$cred_helper" \
        fetch --quiet origin "refs/heads/$branch:refs/remotes/origin/$branch" \
     && git branch --set-upstream-to="origin/$branch" "$branch" >/dev/null; then
    :
else
    echo "$me: warning — could not point '$branch' at origin/$branch." >&2
    echo "$me: it may still track a URL, in which case a later plain" >&2
    echo "$me: 'git push' authenticates as you rather than as the App." >&2
    echo "$me: fix with:" >&2
    echo "    git fetch origin '$branch' && git branch --set-upstream-to=origin/'$branch' '$branch'" >&2
fi

jq -n --arg title "$title" --arg head "$branch" --arg base "$base" \
      --arg body "$body" --argjson draft "$draft" \
      '{title: $title, head: $head, base: $base, body: $body, draft: $draft}' \
    | GH_TOKEN="$token" gh api --input - "repos/$repo/pulls" \
        --jq '"opened #\(.number) as \(.user.login)\(if .draft then " (draft)" else "" end) — \(.html_url)"'

#!/usr/bin/env bash
# File one issue as a GitHub App — and nothing else.
#
# Same shape as comment.sh and for the same reason: the endpoint and the method
# are fixed, so no argument can steer the request. It can open an issue but
# cannot close, label, assign or edit one.
#
# There is deliberately no --label, --assignee or --milestone. Each would be
# another argument that changes what the request does, which is exactly the
# surface these wrappers exist to not have — and a label applied by hand costs
# one click, where a wrapper that can set arbitrary fields costs the argument
# for granting it unattended.
#
# This is a core, not the surface — see the note in comment.sh. Run it through
# ../coder-file-issue.sh or ../reviewer-file-issue.sh.
#
# Usage (via a shim):
#   scripts/coder-file-issue.sh --title "Subject" "Body text"
#   scripts/reviewer-file-issue.sh --title "Subject" < body.md
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$here/common.sh"

me="${MEALPLANNER_INVOKED_AS:-$(basename "${BASH_SOURCE[0]}")}"
usage="$me --title <title> [body]"

if [[ "${1:-}" != "--as" ]]; then
    echo "usage: $usage" >&2
    exit 2
fi
# See the apostrophe note in comment.sh.
role="${2:?--as needs a role, e.g. coder}"
shift 2

title=""
if [[ "${1:-}" == "--title" ]]; then
    title="${2:?--title needs a value}"
    shift 2
else
    echo "usage: $usage" >&2
    echo "       the body may come on stdin instead of as an argument" >&2
    exit 2
fi

require_nonblank "$me" "$title" title

body="$(read_body "$me" "$usage" body "$@")" || exit
require_nonblank "$me" "$body" issue

# jq for the same reason as comment.sh: titles and bodies carry quotes and
# backticks, and an issue body is usually many lines.
jq -n --arg title "$title" --arg body "$body" '{title: $title, body: $body}' \
    | GH_TOKEN="$("$here/app-token.sh" --as "$role")" gh api --input - \
        "repos/$app_repo/issues" \
        --jq '"filed #\(.number) as \(.user.login) — \(.html_url)"'

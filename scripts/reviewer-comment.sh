#!/usr/bin/env bash
# Comment on an issue or PR as meal-planner-reviewer-claude[bot].
#
# A shim, and the shape is the point: what a permission rule can name is a
# filename, so one file per (identity, capability) means a rule grants exactly
# that pair. The core in lib/ takes --as; this is the only thing that passes it.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# exec so stdin (bodies arrive that way), the exit status and signals pass
# straight through. MEALPLANNER_INVOKED_AS is how the core's usage messages name
# the command that was actually run rather than lib/comment.sh.
MEALPLANNER_INVOKED_AS="reviewer-comment.sh" exec "$here/lib/comment.sh" --as reviewer "$@"

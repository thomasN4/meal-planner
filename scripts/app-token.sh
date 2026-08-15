#!/usr/bin/env bash
# Mint a short-lived GitHub App installation token and print it to stdout.
#
# The point of this is attribution: `gh` authenticated as a person posts
# comments as that person, so an agent's review lands under a household
# member's name. A token minted here acts as the App instead, and its comments
# show up as <app-name>[bot].
#
# `gh` has no built-in way to do this — installation tokens need a JWT signed
# with the App's private key, which is what the openssl call below is for.
#
# Usage:
#   export MEALPLANNER_APP_ID=123456
#   export MEALPLANNER_APP_KEY=~/.config/meal-planner-app/private-key.pem
#   GH_TOKEN=$(scripts/app-token.sh) gh api repos/:owner/:repo/issues/32/comments -f body='…'
#
# The token expires in an hour and is not stored anywhere. The private key must
# live OUTSIDE this repo — it is a credential, and this repo is public.
set -euo pipefail

# No apostrophe in this message: bash honours a single quote inside ${var:?word}
# even within double quotes, so "App's" opens a string that never closes and the
# whole script fails to parse.
app_id="${MEALPLANNER_APP_ID:?set MEALPLANNER_APP_ID to the App ID shown on the App settings page}"
key_dir="${MEALPLANNER_APP_KEY_DIR:-$HOME/.config/meal-planner-app}"
repo="${MEALPLANNER_APP_REPO:-thomasN4/meal-planner}"

# Keys keep the name GitHub downloads them under (<app-slug>.<date>.private-key.pem),
# because that name says which App a key belongs to and there is going to be more
# than one. So with MEALPLANNER_APP_KEY unset, find the single .pem in the key
# directory; once a second App lands there, refuse to guess between them.
key_path="${MEALPLANNER_APP_KEY:-}"
if [[ -z "$key_path" ]]; then
    shopt -s nullglob
    candidates=("$key_dir"/*.pem)
    shopt -u nullglob
    case ${#candidates[@]} in
        1) key_path="${candidates[0]}" ;;
        0)
            echo "app-token: no .pem found in $key_dir" >&2
            echo "app-token: generate a private key on the App settings page and put it there," >&2
            echo "app-token: or point MEALPLANNER_APP_KEY at one. Keep it out of the repo." >&2
            exit 1
            ;;
        *)
            echo "app-token: several keys in $key_dir, so which App is ambiguous:" >&2
            printf '  %s\n' "${candidates[@]}" >&2
            echo "app-token: set MEALPLANNER_APP_KEY to the one matching MEALPLANNER_APP_ID=$app_id." >&2
            exit 1
            ;;
    esac
fi

if [[ ! -r "$key_path" ]]; then
    echo "app-token: cannot read private key at $key_path" >&2
    exit 1
fi

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

# GitHub rejects a JWT more than 10 minutes out, and clocks drift, so back-date
# `iat` by a minute and ask for nine.
now=$(date +%s)
header=$(printf '{"alg":"RS256","typ":"JWT"}' | b64url)
payload=$(printf '{"iat":%d,"exp":%d,"iss":"%s"}' "$((now - 60))" "$((now + 540))" "$app_id" | b64url)
signature=$(printf '%s' "$header.$payload" \
    | openssl dgst -sha256 -sign "$key_path" -binary \
    | b64url)
jwt="$header.$payload.$signature"

api() { curl -sS -H "Accept: application/vnd.github+json" -H "Authorization: Bearer $jwt" "$@"; }

# Ask the App which installation covers this repo, rather than making someone
# copy an id out of a settings URL.
installation=$(api "https://api.github.com/repos/$repo/installation")
installation_id=$(printf '%s' "$installation" | jq -r '.id // empty')

if [[ -z "$installation_id" ]]; then
    echo "app-token: no installation found for $repo" >&2
    printf '%s\n' "$installation" | jq -r '.message // .' >&2
    echo "app-token: check the App is installed on that repo, and that MEALPLANNER_APP_ID matches the key." >&2
    exit 1
fi

response=$(api -X POST "https://api.github.com/app/installations/$installation_id/access_tokens")
token=$(printf '%s' "$response" | jq -r '.token // empty')

if [[ -z "$token" ]]; then
    echo "app-token: could not mint a token" >&2
    printf '%s\n' "$response" | jq -r '.message // .' >&2
    exit 1
fi

printf '%s\n' "$token"

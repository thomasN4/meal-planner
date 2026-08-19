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
# There is one App per role — currently `coder` and `reviewer` — so every caller
# has to say which one it is speaking as. `--as` is REQUIRED and has no default:
# minting for whichever App happened to be configured is how an agent ends up
# speaking as the wrong identity, and the caller always knows which it wants.
#
# What a permission rule names is the *shim* that calls this
# (`Bash(./scripts/coder-comment.sh:*)`), not this file and not a flag — one
# filename per identity, so a rule cannot grant both. This script is a core:
# nothing should grant it directly, because minting a token is every capability
# the App has at once.
#
# Usage:
#   export MEALPLANNER_CODER_APP_ID=123456
#   GH_TOKEN=$(scripts/lib/app-token.sh --as coder) gh api repos/:owner/:repo/issues/32/comments -f body='…'
#
# The token expires in an hour and is not stored anywhere. The private key must
# live OUTSIDE this repo — it is a credential, and this repo is public.
set -euo pipefail

# No apostrophe in a ${var:?word} message: bash honours a single quote inside it
# even within double quotes, so "the App-s ID" written properly would open a
# string that never closes and the whole script would fail to parse.
role=""
while (($# > 0)); do
    case "$1" in
        --as) role="${2:?--as needs a role, e.g. coder}"; shift 2 ;;
        *) echo "app-token: unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [[ -z "$role" ]]; then
    echo "usage: app-token.sh --as <role>        # e.g. coder, reviewer" >&2
    exit 2
fi

# The role is interpolated into an App slug and a glob below, so keep it to
# characters that mean themselves in both.
if [[ ! "$role" =~ ^[a-z][a-z0-9-]*$ ]]; then
    echo "app-token: role must be lowercase letters, digits and dashes: $role" >&2
    exit 2
fi

role_uc="${role^^}"
role_uc="${role_uc//-/_}"
app_slug="meal-planner-$role-claude"

# Per-role variables, resolved by indirection: MEALPLANNER_CODER_APP_ID,
# MEALPLANNER_REVIEWER_APP_ID, and so on. There is deliberately no list of valid
# roles here — the environment defines which Apps exist, so an unknown role fails
# as a missing variable that names itself rather than as a curated rejection.
id_var="MEALPLANNER_${role_uc}_APP_ID"
key_var="MEALPLANNER_${role_uc}_APP_KEY"
app_id="${!id_var:-}"
if [[ -z "$app_id" ]]; then
    echo "app-token: set $id_var to the App ID shown on the App settings page" >&2
    echo "app-token: (that is the App ID, not the installation id and not the bot user id)" >&2
    exit 2
fi

key_dir="${MEALPLANNER_APP_KEY_DIR:-$HOME/.config/meal-planner-app}"
repo="${MEALPLANNER_APP_REPO:-thomasN4/meal-planner}"

# Keys keep the name GitHub downloads them under (<app-slug>.<date>.private-key.pem),
# because that name says which App a key belongs to — so with two Apps the name is
# already the answer to "which one", and the role globs it directly rather than
# needing a second variable to disambiguate. Two matches still means two dated
# keys for the SAME App (a rotation that left the old one behind), which is a real
# ambiguity about which is live, so name it and stop.
key_path="${!key_var:-}"
if [[ -z "$key_path" ]]; then
    shopt -s nullglob
    candidates=("$key_dir/$app_slug".*.pem)
    shopt -u nullglob
    case ${#candidates[@]} in
        1) key_path="${candidates[0]}" ;;
        0)
            echo "app-token: no key for '$role' in $key_dir" >&2
            echo "app-token: expected $key_dir/$app_slug.<date>.private-key.pem" >&2
            echo "app-token: generate one on the App settings page and put it there keeping" >&2
            echo "app-token: that filename, or point $key_var at it. Keep it out of the repo." >&2
            exit 1
            ;;
        *)
            echo "app-token: several keys for '$role' in $key_dir, so which is live is ambiguous:" >&2
            printf '  %s\n' "${candidates[@]}" >&2
            echo "app-token: delete the retired one, or set $key_var to the current one." >&2
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
    echo "app-token: check the App is installed on that repo, and that $id_var matches the key." >&2
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

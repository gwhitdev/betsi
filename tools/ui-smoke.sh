#!/usr/bin/env bash
#
# Signs in to the web interface as a development user and fetches a page, the whole way through
# Keycloak's login form. Proves the interactive path end to end — challenge, authorization code,
# cookie, tenant resolution — without a browser.
#
# Usage:
#   ui-smoke.sh [user] [path]        (default: nurse /escalations)
#
# Requires `docker compose up -d keycloak` and the application running on $BASE (default
# http://127.0.0.1:5080). The passwords are the development fixtures in
# tools/keycloak/betsi-realm.json; nothing here works against a real identity provider, which is
# the point of it being a fixture.

set -euo pipefail

USER_NAME="${1:-nurse}"
PATH_TO_FETCH="${2:-/escalations}"
BASE="${BASE:-http://127.0.0.1:5080}"
PASSWORD="${PASSWORD:-betsi}"

JAR=$(mktemp)
BODY=$(mktemp)
trap 'rm -f "$JAR" "$BODY"' EXIT

# 1. Ask for a page; follow the challenge to Keycloak's login form.
curl -sS -c "$JAR" -b "$JAR" -L -o "$BODY" "$BASE/sign-in?returnUrl=$PATH_TO_FETCH"

# 2. Post the credentials to the form's own action URL, which carries the session and tab ids.
ACTION=$(grep -oiE 'action="[^"]*"' "$BODY" | head -1 | sed -E 's/^[Aa][Cc][Tt][Ii][Oo][Nn]="//;s/"$//' | sed 's/&amp;/\&/g' || true)
if [[ -z "$ACTION" ]]; then
    echo "No login form was returned. Is the realm imported and the user '$USER_NAME' present?" >&2
    exit 1
fi

curl -sS -c "$JAR" -b "$JAR" -L -o "$BODY" \
    --data-urlencode "username=$USER_NAME" \
    --data-urlencode "password=$PASSWORD" \
    "$ACTION"

# 3. The provider answers with a self-submitting form (OIDC response_mode=form_post), which a
# browser posts to the callback and curl does not. Submitting it by hand is what makes this a
# test of the real flow rather than of a simplified one.
# Matched case-insensitively: this page is generated markup, and its tags are uppercase.
CALLBACK=$(grep -oiE 'action="[^"]*"' "$BODY" | head -1 | sed -E 's/^[Aa][Cc][Tt][Ii][Oo][Nn]="//;s/"$//' | sed 's/&amp;/\&/g' || true)
if [[ -n "${CALLBACK:-}" ]]; then
    FIELDS=()
    while IFS=$'\t' read -r name value; do
        [[ -n "$name" ]] && FIELDS+=(--data-urlencode "$name=$value")
    done < <(grep -oiE '<input[^>]*>' "$BODY" |
             grep -i 'hidden' |
             sed -E 's/.*[Nn][Aa][Mm][Ee]="([^"]*)".*[Vv][Aa][Ll][Uu][Ee]="([^"]*)".*/\1\t\2/')

    curl -sS -c "$JAR" -b "$JAR" -L -o "$BODY" "${FIELDS[@]}" "$CALLBACK"
fi

# 4. Fetch the page as the signed-in user.
STATUS=$(curl -sS -c "$JAR" -b "$JAR" -o "$BODY" -w '%{http_code}' "$BASE$PATH_TO_FETCH")

echo "GET $PATH_TO_FETCH as $USER_NAME -> $STATUS"
grep -oE '<h1[^>]*>[^<]*</h1>' "$BODY" | head -3
cat "$BODY" > "${SAVE_TO:-/dev/null}"
[[ "$STATUS" == "200" ]]

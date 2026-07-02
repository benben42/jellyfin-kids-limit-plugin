#!/usr/bin/env bash
#
# Phase 0 spike (REQUIREMENTS.md §2.1) — THE LOAD-BEARING RISK.
#
# Confirms, against a REAL running server + the household Android TV, whether the
# server can Stop / Pause an in-progress video and whether DisplayMessage renders.
# EVERYTHING in this plugin depends on Stop working on the actual TV — run this
# BEFORE trusting the plugin in production.
#
# Usage:
#   export JELLYFIN_URL="http://your-server:8096"
#   export JELLYFIN_TOKEN="<an admin API key: Dashboard > API Keys>"
#   ./spike/stop-test.sh list                 # show active sessions
#   ./spike/stop-test.sh stop    <sessionId>  # send Stop
#   ./spike/stop-test.sh pause   <sessionId>  # send Pause
#   ./spike/stop-test.sh unpause <sessionId>  # send Unpause
#   ./spike/stop-test.sh message <sessionId>  # send a DisplayMessage toast
#
# Start something playing on the Android TV, run `list`, grab its Id, then try
# `stop`. If the video stops on the TV -> GO. If not -> the monitor->kill model
# fails on the primary client (see REQUIREMENTS.md §2.1 / §12).

set -euo pipefail

: "${JELLYFIN_URL:?Set JELLYFIN_URL, e.g. http://server:8096}"
: "${JELLYFIN_TOKEN:?Set JELLYFIN_TOKEN to an admin API key}"

AUTH_HEADER="Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\""
cmd="${1:-list}"
sid="${2:-}"

case "$cmd" in
  list)
    echo "Active sessions with playback:"
    curl -fsSL -H "$AUTH_HEADER" "${JELLYFIN_URL}/Sessions" \
      | python3 -c '
import sys, json
for s in json.load(sys.stdin):
    npi = s.get("NowPlayingItem")
    print(f"- Id={s.get(\"Id\")}  User={s.get(\"UserName\")}  Client={s.get(\"Client\")}  Device={s.get(\"DeviceName\")}  Playing={npi.get(\"Name\") if npi else None}")
    caps = s.get("Capabilities") or {}
    print(f"    SupportsMediaControl={caps.get(\"SupportsMediaControl\")} SupportsRemoteControl={s.get(\"SupportsRemoteControl\")}")
'
    ;;
  stop)
    [ -n "$sid" ] || { echo "Need <sessionId>"; exit 1; }
    curl -fsSL -X POST -H "$AUTH_HEADER" "${JELLYFIN_URL}/Sessions/${sid}/Playing/Stop"
    echo "Sent Stop to ${sid}"
    ;;
  pause)
    [ -n "$sid" ] || { echo "Need <sessionId>"; exit 1; }
    curl -fsSL -X POST -H "$AUTH_HEADER" "${JELLYFIN_URL}/Sessions/${sid}/Playing/Pause"
    echo "Sent Pause to ${sid}"
    ;;
  unpause)
    [ -n "$sid" ] || { echo "Need <sessionId>"; exit 1; }
    curl -fsSL -X POST -H "$AUTH_HEADER" "${JELLYFIN_URL}/Sessions/${sid}/Playing/Unpause"
    echo "Sent Unpause to ${sid}"
    ;;
  message)
    [ -n "$sid" ] || { echo "Need <sessionId>"; exit 1; }
    curl -fsSL -X POST -H "$AUTH_HEADER" -H "Content-Type: application/json" \
      -d '{"Header":"Spike test","Text":"If you can read this, DisplayMessage works.","TimeoutMs":8000}' \
      "${JELLYFIN_URL}/Sessions/${sid}/Message"
    echo "Sent DisplayMessage to ${sid}"
    ;;
  *)
    echo "Unknown command: $cmd (use list|stop|pause|unpause|message)"; exit 1
    ;;
esac

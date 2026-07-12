#!/usr/bin/env bash
set -Eeuo pipefail

subject="${1:-ChatGPT Connector 告警}"
cooldown_key="${2:-generic}"
cooldown_seconds="${ALERT_COOLDOWN_SECONDS:-3600}"
state_dir="/var/lib/chatgpt-connector/alerts"
state_file="$state_dir/${cooldown_key//[^a-zA-Z0-9_.-]/_}.last"
now="$(date +%s)"

mkdir -p "$state_dir"
chmod 700 "$state_dir"
if [[ -f "$state_file" ]]; then
  last="$(cat "$state_file" 2>/dev/null || printf 0)"
  if [[ "$last" =~ ^[0-9]+$ ]] && (( now - last < cooldown_seconds )); then
    exit 0
  fi
fi

body="$(cat)"
printf '%s\n' "$body" | /app/chatgpt_connector/deploy/send-alert.py "$subject"
printf '%s\n' "$now" > "$state_file"
chmod 600 "$state_file"

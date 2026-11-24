#!/usr/bin/env bash
# End-to-end smoke test of the host and CLI with audio off, in a throwaway profile.
# Usage: scripts/smoke.sh [path-to-bspeak]   (defaults to the Debug build)
set -u
B="${1:-$(dirname "$0")/../src/BSpeak.Cli/bin/Debug/net10.0/bspeak.exe}"
export BSPEAK_HOME="${TMPDIR:-${TEMP:-/tmp}}/bspeak-smoke-$$"
mkdir -p "$BSPEAK_HOME"
# Start with audio disabled; the first command would otherwise auto-start a host with audio on.
cat > "$BSPEAK_HOME/config.json" <<'EOF'
{ "audio": { "enabled": false }, "room": { "userGapMs": 300 } }
EOF
fail=0
check() { local want="$1" got="$2" what="$3"; if [ "$want" = "$got" ]; then echo "ok   $what"; else echo "FAIL $what (exit $got, wanted $want)"; fail=1; fi; }

"$B" participant post --name Atlas > /dev/null; check 0 $? "join Atlas (auto-starts the host)"
"$B" participant post > "$BSPEAK_HOME/nova.json"; check 0 $? "join with a pooled name"
grep -q '"Nova"' "$BSPEAK_HOME/nova.json"; check 0 $? "pooled name is Nova"
"$B" participant post --name Nora > /dev/null; check 1 $? "a name that sounds like Nova is refused"

"$B" utterance list --as Atlas --after 0 --wait --timeout 20 > "$BSPEAK_HOME/wait.json" & waiter=$!
sleep 1
"$B" utterance post "Atlas, run the tests please." > /dev/null
wait $waiter; check 0 $? "a waiting agent wakes when you speak"
grep -q '"forYou": true' "$BSPEAK_HOME/wait.json"; check 0 $? "the utterance is for Atlas"

"$B" speech post "Tests finished. Two failures in the parser." --as Atlas --reply-to 1 --wait > "$BSPEAK_HOME/speech.json"; check 0 $? "speech post --wait returns after playback"
grep -q '"state": "finished"' "$BSPEAK_HOME/speech.json"; check 0 $? "the speech finished"

"$B" speech post $'Changes:\n- parser' --as Atlas > /dev/null; check 1 $? "markdown is refused"
"$B" speech post "Hello." --as Bob > /dev/null; check 1 $? "unknown participant is refused"
"$B" utterance post "Atlas, which parser?" > /dev/null
"$B" speech post "Old answer." --as Atlas --reply-to 1 > /dev/null; check 1 $? "a stale reply is refused (conversation-moved-on)"

"$B" utterance post "Stop listening." > /dev/null
"$B" listening get | grep -q '"listening": false'; check 0 $? "saying stop listening pauses"
"$B" utterance post "Atlas, are you there?" | grep -q '"delivered": false'; check 0 $? "paused speech is not delivered"
"$B" utterance post "Start listening." > /dev/null
"$B" listening get | grep -q '"listening": true'; check 0 $? "saying start listening resumes"

"$B" utterance list --as Nova --after 2 --wait --timeout 2 > /dev/null; check 3 $? "a wait with nothing new times out with exit 3"
"$B" brief get --as Atlas | grep -q "You: Atlas"; check 0 $? "brief shows who you are"
"$B" host delete > /dev/null; check 0 $? "stop the host"
rm -rf "$BSPEAK_HOME"
exit $fail

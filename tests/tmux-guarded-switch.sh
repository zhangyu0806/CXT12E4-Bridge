#!/usr/bin/env bash
# Contract test for the actual compiled BuildGuardedSwitchCommand output.
# Run from Ubuntu WSL, passing a Windows path to the current Bridge EXE/DLL.
# All tmux sessions and the PTY client belong exclusively to this unique -L
# socket. No default server, user's tmux configuration or real client is used.
set -euo pipefail

assembly_windows=${1:?Pass the Windows path of the current Bridge EXE or DLL}
powershell=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
[[ -x "$powershell" ]] || { printf '%s\n' 'Windows PowerShell is required.' >&2; exit 2; }
command -v tmux >/dev/null
command -v script >/dev/null
command -v base64 >/dev/null

socket="cxt-audit-$$-$RANDOM"
[[ "$socket" =~ ^cxt-audit-[0-9]+-[0-9]+$ ]] || exit 2
export CXT_AUDIT_SOCKET="$socket"
export TERM=xterm-256color
export TMUX_TMPDIR=/tmp
test_uid=$(id -u)
pty_pid=
socket_path=
cleanup() {
    local result=$? attempt diagnostic server_gone=false active_pid
    # The target is always the validated unique test socket, never default.
    tmux -L "$socket" kill-server >/dev/null 2>&1 || true
    if [[ -n "$pty_pid" ]]; then
        # Only signal our still-registered background job, not a recycled PID.
        for active_pid in $(jobs -pr); do
            if [[ "$active_pid" == "$pty_pid" ]]; then
                kill "$pty_pid" >/dev/null 2>&1 || true
            fi
        done
        wait "$pty_pid" >/dev/null 2>&1 || true
    fi
    if [[ -n "$socket_path" ]]; then
        for ((attempt = 0; attempt < 20; attempt++)); do
            if ! diagnostic=$(tmux -L "$socket" list-sessions 2>&1); then
                case "$diagnostic" in
                    *"no server running on $socket_path"*|*"error connecting to $socket_path"*)
                        server_gone=true
                        break
                        ;;
                esac
            fi
            sleep 0.05
        done
        # tmux may intentionally leave a stale socket file after kill-server.
        # Only remove that exact test-owned pathname after the explicit -L
        # endpoint has confirmed there is no server left behind it.
        if [[ "$server_gone" != true || "$socket_path" != "/tmp/tmux-$test_uid/$socket" ]]; then
            printf '%s\n' 'FAIL could not verify isolated test server shutdown and exact socket path.' >&2
            result=1
        else
            if [[ -S "$socket_path" ]]; then
                rm -f -- "$socket_path"
            fi
            if [[ -e "$socket_path" || -L "$socket_path" ]]; then
                printf '%s\n' 'FAIL isolated test socket residue remained after cleanup.' >&2
                result=1
            else
                printf '%s\n' 'PASS isolated server stopped and test socket removed; default server was never addressed.'
            fi
        fi
    fi
    exit "$result"
}
trap cleanup EXIT

tmux -L "$socket" -f /dev/null new-session -d -s 01-audit-source 'sleep 120'
socket_path=$(tmux -L "$socket" display-message -p -t 01-audit-source '#{socket_path}')
[[ "$socket_path" == "/tmp/tmux-$test_uid/$socket" ]] || exit 2
source_id=$(tmux -L "$socket" display-message -p -t 01-audit-source '#{session_id}')
tmux -L "$socket" new-session -d -s 02-audit-target 'sleep 120'
target_meta=$(tmux -L "$socket" display-message -p -t 02-audit-target $'#{session_id}\t#{session_created}\t#{pid}')
IFS=$'\t' read -r target_id created server_pid <<< "$target_meta"
[[ "$source_id" =~ ^\$[0-9]+$ && "$target_id" =~ ^\$[0-9]+$ && "$created" =~ ^[0-9]+$ && "$server_pid" =~ ^[0-9]+$ ]] || exit 2

# coproc keeps the client's stdin open without injecting any keyboard input.
coproc CXT_AUDIT_PTY { script -q -e -c "exec tmux -L $socket attach-session -t '$source_id'" /dev/null >/dev/null 2>&1; }
pty_pid=$CXT_AUDIT_PTY_PID
client=
for ((attempt = 0; attempt < 50; attempt++)); do
    client=$(tmux -L "$socket" list-clients -F '#{client_name}')
    [[ -n "$client" ]] && break
    sleep 0.05
done
[[ "$client" =~ ^/dev/pts/[0-9]+$ ]] || { printf '%s\n' 'Isolated PTY client did not attach.' >&2; exit 2; }

# Reflect only the pure command builder. This never starts the Bridge,
# installs a hook, opens UI, sends keys or contacts a tmux server on Windows.
read -r -d '' generator <<'POWERSHELL' || true
$ErrorActionPreference = 'Stop'
$assemblyPath = [Console]::In.ReadLine()
$sessionId = [Console]::In.ReadLine()
$created = [long][Console]::In.ReadLine()
$serverPid = [int][Console]::In.ReadLine()
$clientName = [Console]::In.ReadLine()
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$targetType = $assembly.GetType('Cxt1024Bridge.TerminalSessionTarget')
$target = [Activator]::CreateInstance($targetType, $true)
$targetType.GetField('SessionId',$flags).SetValue($target,$sessionId)
$targetType.GetField('SessionCreatedUnix',$flags).SetValue($target,$created)
$targetType.GetField('ServerPid',$flags).SetValue($target,$serverPid)
$builder = $assembly.GetType('Cxt1024Bridge.TmuxSessionClient').GetMethod('BuildGuardedSwitchCommand',[Reflection.BindingFlags]'Static,NonPublic')
foreach ($argument in $builder.Invoke($null,@($clientName,$target))) {
    [Console]::WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($argument)))
}
POWERSHELL

run_guarded() {
    local expected_client=$1 expected_session=$2 expected_created=$3 expected_pid=$4
    local encoded_output encoded line script_text
    local -a generated=()
    encoded_output=$(printf '%s\n' "$assembly_windows" "$expected_session" "$expected_created" "$expected_pid" "$expected_client" |
        "$powershell" -NoLogo -NoProfile -NonInteractive -Command "$generator") || return
    while IFS= read -r encoded; do
        line=${encoded%$'\r'}
        generated+=("$(printf '%s' "$line" | base64 --decode)")
    done <<< "$encoded_output"
    [[ ${#generated[@]} == 9 && ${generated[0]} == --exec && ${generated[1]} == bash && ${generated[2]} == -c ]] || return 2
    [[ ${generated[3]} == *'exec tmux if-shell -F -t '* ]] || return 2
    # Only adapt transport routing to the isolated test socket. The identity
    # expression, selected tmux commands, quoting and receipts are untouched.
    script_text=${generated[3]/exec tmux /exec tmux -L \"\$CXT_AUDIT_SOCKET\" }
    bash -c "$script_text" "${generated[@]:4}"
}

actual_session() { tmux -L "$socket" list-clients -F '#{session_id}'; }
reset_source() { tmux -L "$socket" switch-client -c "$client" -t "$source_id"; }
assert_no_success() { [[ "$1" != *CXT-TMUX-SWITCHED* ]]; }

output=$(run_guarded "$client" "$target_id" "$created" "$server_pid")
[[ "$output" == *$'\nCXT-TMUX-SWITCHED' && $(actual_session) == "$target_id" ]]
printf '%s\n' 'PASS matching identity switches the isolated client and confirms success.'

reset_source
output=$(run_guarded "$client" "$target_id" "$created" "$((server_pid + 1))")
[[ "$output" == *CXT-TMUX-IDENTITY-MISMATCH && $(actual_session) == "$source_id" ]]
assert_no_success "$output"
printf '%s\n' 'PASS reused ID with wrong server PID does not switch or confirm.'

output=$(run_guarded "$client" "$target_id" "$((created + 1))" "$server_pid")
[[ "$output" == *CXT-TMUX-IDENTITY-MISMATCH && $(actual_session) == "$source_id" ]]
assert_no_success "$output"
printf '%s\n' 'PASS wrong creation time does not switch or confirm.'

output=$(run_guarded "$client" '$999999999' "$created" "$server_pid")
[[ "$output" == *CXT-TMUX-IDENTITY-MISMATCH && $(actual_session) == "$source_id" ]]
assert_no_success "$output"
printf '%s\n' 'PASS disappeared session cannot fall back to a different current session.'

if output=$(run_guarded /dev/pts/cxt-audit-missing "$target_id" "$created" "$server_pid" 2>&1); then
    printf '%s\n' 'FAIL nonexistent client unexpectedly succeeded.' >&2
    exit 1
fi
assert_no_success "$output"
[[ $(actual_session) == "$source_id" ]]
printf '%s\n' 'PASS failed switch has nonzero exit and cannot emit success receipt.'
printf '%s\n' 'All 5 isolated tmux contract checks passed; the EXIT trap removes only the test server.'

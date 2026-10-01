#!/usr/bin/env bash
# Tests the admin page's launcher (issue #5) as root against a scratch folder: deploy/install-admin.sh itself, a copy of
# deploy/bs3d-admin-run.sh that starts a stand-in for the page, and deploy/bs3d-admin.sh with a stand-in for sudo. CI
# runs it with sudo; on the Pi, where root is not needed for anything it touches: unshare -r tests/deploy/install-admin.test.sh
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "run it as root (sudo, or unshare -r): the installer refuses anyone else" >&2; exit 1; }
PATH="$PATH:/usr/sbin:/sbin"   # visudo, for a caller whose PATH lacks it

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/libexec" "$work/sudoers.d" "$work/stub"

failures=0
pass() { echo "ok    $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

# --- install-admin.sh, as root run by sudo from the account $1 ("" for no sudo at all); its output goes to $work/out

install_admin() {
    local user=$1
    shift
    set +e
    env SUDO_USER="$user" BIN_DIR="$work/bin" LIBEXEC_DIR="$work/libexec" SUDOERS_DIR="$work/sudoers.d" \
        bash "$repo/deploy/install-admin.sh" "$@" > "$work/out" 2>&1
    status=$?
    set -e
}
nothing_installed() { [[ -z "$(find "$work/bin" "$work/libexec" "$work/sudoers.d" -mindepth 1)" ]]; }
# Each refusal starts from nothing, so one that wrongly installs fails only its own case
reset() { find "$work/bin" "$work/libexec" "$work/sudoers.d" -mindepth 1 -delete; }
installed() { sha256sum "$work/bin/bs3d-admin" "$work/libexec/bs3d-admin-run" "$work/sudoers.d/bs3d-admin"; }

reset
install_admin ""
if [[ $status -ne 0 ]] && nothing_installed && grep -q "not as root itself" "$work/out"; then pass "refuses to run without a sudo caller, installs nothing"; else fail "refuses to run without a sudo caller ($status)"; fi

reset
install_admin root
if [[ $status -ne 0 ]] && nothing_installed; then pass "refuses root as the page's owner"; else fail "refuses root as the page's owner ($status)"; fi

reset
install_admin "nobody ALL=(ALL) NOPASSWD: ALL #"
if [[ $status -ne 0 ]] && nothing_installed && grep -q "refusing the user name" "$work/out"; then pass "refuses a user name that would write its own rule"; else fail "refuses a user name that would write its own rule ($status)"; fi

reset
install_admin nobody --force
if [[ $status -eq 2 ]] && nothing_installed; then pass "refuses an unknown argument"; else fail "refuses an unknown argument ($status)"; fi

# A stand-in visudo that refuses every rule: what the installer does when the real one would
mkdir -p "$work/refuse"
printf '#!/bin/sh\nexit 1\n' > "$work/refuse/visudo"
chmod +x "$work/refuse/visudo"
reset
PATH="$work/refuse:$PATH" install_admin nobody
if [[ $status -ne 0 ]] && nothing_installed && grep -q "visudo refused the rule" "$work/out"; then pass "installs nothing when visudo refuses the rule"; else fail "installs nothing when visudo refuses the rule ($status)"; fi

reset
install_admin nobody
if [[ $status -eq 0 ]]; then pass "installs"; else fail "installs ($status): $(cat "$work/out")"; fi
if cmp -s "$repo/deploy/bs3d-admin-run.sh" "$work/libexec/bs3d-admin-run" && cmp -s "$repo/deploy/bs3d-admin.sh" "$work/bin/bs3d-admin"; then pass "the runner and the launcher are this release's"; else fail "the runner and the launcher are this release's"; fi
if [[ "$(stat -c '%a %U %G' "$work/libexec/bs3d-admin-run" "$work/bin/bs3d-admin" | sort -u)" == "755 root root" ]]; then pass "both are root's, 0755"; else fail "both are root's, 0755: $(stat -c '%n %a %U %G' "$work/libexec/bs3d-admin-run" "$work/bin/bs3d-admin")"; fi
if [[ "$(stat -c '%a %U %G' "$work/sudoers.d/bs3d-admin")" == "440 root root" ]]; then pass "the rule is root's, 0440"; else fail "the rule is $(stat -c '%a %U %G' "$work/sudoers.d/bs3d-admin")"; fi
if [[ "$(grep -v '^#' "$work/sudoers.d/bs3d-admin")" == "nobody ALL=(bs3d-api) NOPASSWD: $work/libexec/bs3d-admin-run" ]]; then pass "the rule allows the caller exactly the runner, as bs3d-api"; else fail "the rule: $(cat "$work/sudoers.d/bs3d-admin")"; fi
if visudo -cqf "$work/sudoers.d/bs3d-admin"; then pass "visudo takes the rule"; else fail "visudo takes the rule"; fi
if [[ "$(find "$work/sudoers.d" -mindepth 1)" == "$work/sudoers.d/bs3d-admin" ]]; then pass "leaves nothing else in sudoers.d"; else fail "leaves $(find "$work/sudoers.d" -mindepth 1)"; fi

first=$(installed)
install_admin nobody
if [[ $status -eq 0 && "$(installed)" == "$first" ]]; then pass "a second run changes nothing"; else fail "a second run changes nothing ($status)"; fi

install_admin "" --remove
if [[ $status -eq 0 ]] && nothing_installed; then pass "--remove takes all three away"; else fail "--remove takes all three away ($status)"; fi

# --- bs3d-admin-run.sh: a copy that runs as whoever runs this test and starts a stand-in recording what reaches it

sed -e "s|^user=bs3d-api\$|user=$(id -un)|" -e "s|^app=/opt/bs3d-api/current/BS3D.Api\$|app=$work/stub/BS3D.Api|" \
    "$repo/deploy/bs3d-admin-run.sh" > "$work/run.sh"
if ! grep -q "^user=$(id -un)\$" "$work/run.sh" || ! grep -q "^app=$work/stub/BS3D.Api\$" "$work/run.sh"; then
    echo "the runner's user= and app= lines changed: update this test" >&2
    exit 1
fi
cat > "$work/stub/BS3D.Api" << 'EOF'
#!/bin/sh
{
    echo "args: $*"
    echo "cwd: $(pwd)"
    echo "stdin: $(readlink /proc/self/fd/0)"
    tr '\0' '\n' < /proc/$$/environ | sort
} > @SEEN@
EOF
sed -i "s|@SEEN@|$work/stub/seen|" "$work/stub/BS3D.Api"
chmod +x "$work/stub/BS3D.Api"
echo "typed at the terminal" > "$work/typed"

# The caller's environment asks for every address, another database, and code loaded into the page
run_runner() {
    rm -f "$work/stub/seen"
    set +e
    env ASPNETCORE_URLS=http://0.0.0.0:80 Scores__Database=/tmp/elsewhere.db Scores__AddressSalt=leak \
        DOTNET_STARTUP_HOOKS=/tmp/hook.dll bash "$work/run.sh" "$@" < "$work/typed" > "$work/out" 2>&1
    status=$?
    set -e
}
expected_env=$(printf '%s\n' ASPNETCORE_ENVIRONMENT=Production PATH=/usr/bin:/bin \
    Scores__CeilingsDirectory=/var/lib/bs3d-api/ceilings Scores__Database=/var/lib/bs3d-api/scores.db)

run_runner
if [[ $status -eq 0 ]] && grep -qx "args: admin web" "$work/stub/seen"; then pass "starts the page"; else fail "starts the page ($status): $(cat "$work/out")"; fi
if [[ "$(sed -n '4,$p' "$work/stub/seen")" == "$expected_env" ]]; then pass "the page gets its own environment and nothing of the caller's"; else fail "the page's environment: $(sed -n '4,$p' "$work/stub/seen")"; fi
if grep -qx "cwd: /" "$work/stub/seen" && grep -qx "stdin: /dev/null" "$work/stub/seen"; then pass "in /, reading nothing from the terminal"; else fail "cwd and stdin: $(sed -n '2,3p' "$work/stub/seen")"; fi

run_runner --port 5002
if [[ $status -eq 0 ]] && grep -qx "args: admin web --port 5002" "$work/stub/seen"; then pass "passes on --port N"; else fail "passes on --port N ($status)"; fi

for args in "--port" "--port 5002 --urls" "--urls http://0.0.0.0:80" "--port 50a2" "--port 123456" "--port=5002" "--port -1"; do
    # shellcheck disable=SC2086  # each case is split into its arguments on purpose
    run_runner $args
    if [[ $status -eq 2 && ! -e "$work/stub/seen" ]]; then pass "refuses '$args'"; else fail "refuses '$args' ($status)"; fi
done

# The shipped runner's user, with only the page replaced by the stand-in: the real page must not start if this fails
sed -e "s|^app=/opt/bs3d-api/current/BS3D.Api\$|app=$work/stub/BS3D.Api|" "$repo/deploy/bs3d-admin-run.sh" > "$work/run.sh"
run_runner
if [[ $status -eq 1 && ! -e "$work/stub/seen" ]] && grep -q "runs as bs3d-api" "$work/out"; then pass "the runner refuses anyone but bs3d-api"; else fail "the runner refuses anyone but bs3d-api ($status)"; fi

# --- bs3d-admin.sh, with a stand-in for sudo

cat > "$work/stub/sudo" << 'EOF'
#!/bin/sh
echo "sudo $*" > @SEEN@
EOF
sed -i "s|@SEEN@|$work/stub/sudo-seen|" "$work/stub/sudo"
chmod +x "$work/stub/sudo"
env PATH="$work/stub:$PATH" bash "$repo/deploy/bs3d-admin.sh" --port 5002
if [[ "$(cat "$work/stub/sudo-seen")" == "sudo -n -u bs3d-api /usr/local/libexec/bs3d-admin-run --port 5002" ]]; then pass "the launcher runs the runner as bs3d-api and never asks sudo for a password"; else fail "the launcher ran: $(cat "$work/stub/sudo-seen")"; fi

echo
if (( failures > 0 )); then echo "$failures failure(s)"; exit 1; fi
echo "all passed"

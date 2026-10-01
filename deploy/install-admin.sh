#!/usr/bin/env bash
# Installs the admin page's launcher (issue #5) from this release. Run with sudo from the account that will start the
# page, once, and again when update.sh says a release carries a different launcher:
#
#   sudo /opt/bs3d-api/current/deploy/install-admin.sh
#   sudo /opt/bs3d-api/current/deploy/install-admin.sh --remove     takes all three away
#
#   /usr/local/bin/bs3d-admin           what the owner types (bs3d-admin.sh)
#   /usr/local/libexec/bs3d-admin-run   the runner, run as bs3d-api (bs3d-admin-run.sh)
#   /etc/sudoers.d/bs3d-admin           "<owner> ALL=(bs3d-api) NOPASSWD: /usr/local/libexec/bs3d-admin-run"
#
# The cost, accepted in #5 (question 3): any process running as the owner can start the page and read every player
# through it, with no password. What it buys: starting the page never authenticates sudo, so it never leaves a
# timestamp that makes anything root. Nothing here runs at update: what bs3d-api may be run as stays the owner's call.
#
# BIN_DIR, LIBEXEC_DIR and SUDOERS_DIR exist for tests/deploy/install-admin.test.sh.
set -euo pipefail

BIN_DIR=${BIN_DIR:-/usr/local/bin}
LIBEXEC_DIR=${LIBEXEC_DIR:-/usr/local/libexec}
SUDOERS_DIR=${SUDOERS_DIR:-/etc/sudoers.d}
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

[[ $EUID -eq 0 ]] || { echo "run it with sudo: sudo $0" >&2; exit 1; }

case "${1:-}" in
    "") ;;
    --remove)
        # The rule first, so it never names a runner that is gone
        rm -f "$SUDOERS_DIR/bs3d-admin" "$BIN_DIR/bs3d-admin" "$LIBEXEC_DIR/bs3d-admin-run"
        echo "removed bs3d-admin, its runner and $SUDOERS_DIR/bs3d-admin"
        exit 0 ;;
    *) echo "usage: sudo $0 [--remove]" >&2; exit 2 ;;
esac

# The rule is for whoever ran sudo; a name is checked before it is written into sudoers, where anything after it
# would be read as more rule
owner=${SUDO_USER:-}
[[ -n "$owner" && "$owner" != root ]] \
    || { echo "run it with sudo from the account that will start the page, not as root itself. Nothing changed." >&2; exit 1; }
if ! [[ "$owner" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || ! id -u "$owner" > /dev/null 2>&1; then
    echo "refusing the user name '$owner'. Nothing changed." >&2
    exit 1
fi
for file in bs3d-admin.sh bs3d-admin-run.sh; do
    [[ -f "$here/$file" ]] || { echo "no $file beside $0. Nothing changed." >&2; exit 1; }
done

rule=$(mktemp)
trap 'rm -f "$rule"' EXIT
printf '%s\n' "# The admin page (issue #5): $owner starts it as bs3d-api with bs3d-admin. From install-admin.sh." \
    "$owner ALL=(bs3d-api) NOPASSWD: $LIBEXEC_DIR/bs3d-admin-run" > "$rule"
visudo -cqf "$rule" || { echo "visudo refused the rule. Nothing changed." >&2; exit 1; }

# The runner before the rule naming it. The rule goes in under a name sudo skips (it holds a dot) and is renamed over
# the old one, so sudo never reads half a file: a broken sudoers file can take sudo away from everyone
install -d -m 0755 -o root -g root "$BIN_DIR" "$LIBEXEC_DIR"
install -m 0755 -o root -g root "$here/bs3d-admin-run.sh" "$LIBEXEC_DIR/bs3d-admin-run"
install -m 0755 -o root -g root "$here/bs3d-admin.sh" "$BIN_DIR/bs3d-admin"
install -m 0440 -o root -g root "$rule" "$SUDOERS_DIR/.bs3d-admin.new"
mv -f "$SUDOERS_DIR/.bs3d-admin.new" "$SUDOERS_DIR/bs3d-admin"

echo "installed $BIN_DIR/bs3d-admin, $LIBEXEC_DIR/bs3d-admin-run and $SUDOERS_DIR/bs3d-admin (for $owner)"
echo "Start the page with: bs3d-admin"

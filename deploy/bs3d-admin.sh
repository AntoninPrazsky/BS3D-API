#!/usr/bin/env bash
# `bs3d-admin [--port N]`: starts the admin page (issue #5), installed by install-admin.sh as /usr/local/bin/bs3d-admin.
# It prints a link that works once, for 5 minutes, and lives as long as this terminal. From the desktop:
#
#   ssh -t -o ExitOnForwardFailure=yes -L 127.0.0.1:5001:127.0.0.1:5001 rdt@<the Pi> bs3d-admin
#
# sudo -n: the page never asks for a password and never authenticates sudo, so it never leaves a timestamp that would
# make anything else root. /etc/sudoers.d/bs3d-admin allows exactly the runner, as bs3d-api.
set -euo pipefail

exec sudo -n -u bs3d-api /usr/local/libexec/bs3d-admin-run "$@"

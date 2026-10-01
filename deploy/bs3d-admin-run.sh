#!/bin/bash
# The admin page's runner (issue #5): installed by install-admin.sh as /usr/local/libexec/bs3d-admin-run, root:root
# 0755, and run through sudo as bs3d-api, which /etc/sudoers.d/bs3d-admin lets the owner do without a password. The
# owner types `bs3d-admin`, which runs this.
#
# Nothing of the caller reaches the page but `--port N`: not ASPNETCORE_URLS, not a Scores__Database of their choosing,
# not the salt (the page needs none), not their terminal's input. sudo already resets the environment; env -i makes it
# so whatever sudo's rules become. bash is named by path, because sudo runs this for another user.
set -euo pipefail

# What tests/deploy/install-admin.test.sh replaces in a copy of this script: the shipped one has no knob
user=bs3d-api
app=/opt/bs3d-api/current/BS3D.Api

usage() { echo "usage: bs3d-admin [--port <1024-65535>]" >&2; exit 2; }

[[ "$(id -un)" == "$user" ]] || { echo "the admin page runs as $user, through sudo: type bs3d-admin" >&2; exit 1; }
case $# in
    0) ;;
    2) [[ "$1" == --port && "$2" =~ ^[0-9]{1,5}$ ]] || usage ;;
    *) usage ;;
esac

# The page needs no working directory, and bs3d-api cannot read the owner's home
cd /
exec env -i PATH=/usr/bin:/bin ASPNETCORE_ENVIRONMENT=Production \
    Scores__Database=/var/lib/bs3d-api/scores.db Scores__CeilingsDirectory=/var/lib/bs3d-api/ceilings \
    "$app" admin web "$@" < /dev/null

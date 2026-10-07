#!/bin/sh
# Prints the tag for a patch release of the current commit if the Jellyfin packages
# differ from the latest release, and nothing otherwise. Release tags are
# <major>.<minor>.<patch>, e.g. 0.2.1.
set -eu

project=Jellyfin.Plugin.HAnimeTV/Jellyfin.Plugin.HAnimeTV.csproj
dependencies() { grep -E 'Include="Jellyfin\.(Controller|Model)"'; }

latest=$(git tag -l \
    | grep -E '^v?[0-9]+\.[0-9]+\.[0-9]+$' \
    | sed 's/^v//' \
    | awk -F. '{ print $1, $2, $3, $0 }' \
    | sort -n -k1,1 -k2,2 -k3,3 \
    | tail -n 1 \
    | cut -d' ' -f4)

if [ -z "$latest" ]; then
    # The first release is tagged by hand
    exit 0
fi

latest_ref=$latest
git rev-parse -q --verify "refs/tags/$latest_ref" >/dev/null || latest_ref="v$latest"
if [ "$(dependencies < "$project")" = "$(git show "$latest_ref:$project" 2>/dev/null | dependencies)" ]; then
    exit 0
fi

echo "${latest%.*}.$(( ${latest##*.} + 1 ))"

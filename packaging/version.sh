#!/usr/bin/env bash
# Single source of the Lattice release version, shared by the bash packaging
# scripts (bundle / make-dmg / build-appimage / build-tarball).
#
# The version is computed by MinVer (configured in src/Lattice.App/Lattice.App.csproj):
#   - a v-prefixed git tag on the current commit -> that tag (v0.2.0-alpha.1 -> 0.2.0-alpha.1);
#   - an untagged commit -> latest tag + commit height + short sha (0.2.0-alpha.1.5+abc1234).
# We ask MinVer for its computed Version (no build metadata) so the artifact
# filename + Info.plist stamp come from the SAME computation that stamps the
# assembly during `dotnet publish`. No script passes -p:Version anymore; MinVer
# stamps the managed assembly itself, so the About surface and the filename can
# never diverge.
#
# A dry run (or a local one-off) can pin the version by exporting
#   MinVerVersionOverride=1.2.3-dev
# which BOTH this query and the assembly's MinVer build honour — one knob, both
# outputs. Requires the .NET SDK on PATH (already true wherever we publish).
#
# Usage:  source "$REPO_ROOT/packaging/version.sh"
#         VERSION="$(lattice_resolve_version "$REPO_ROOT")"
lattice_resolve_version() {
    local repo_root="$1"
    # -t:MinVer runs only MinVer's target (so -getProperty sees the computed value,
    # which a plain evaluation-time --getProperty would miss); -restore makes this
    # usable on a fresh CI checkout before any other build step has run.
    dotnet msbuild "$repo_root/src/Lattice.App/Lattice.App.csproj" \
        -t:MinVer -getProperty:Version -restore -nologo
}

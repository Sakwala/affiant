#!/usr/bin/env bash
# Vendor the conformance suite from the pinned affiant-protocol ref, or verify the copy.
#
#   conformance/sync.sh            vendor from the pin (needs the protocol repository)
#   conformance/sync.sh --verify   re-check the vendored copy against SHA256SUMS (offline)
#
# The driver builds and runs OFFLINE: the vendored tree is committed. `--verify` is what
# CI runs, so a local edit to a vendored fixture cannot pass unnoticed — an edited fixture
# is no longer the document the comparison is about (affiant-protocol conformance/DRIVER.md §1).
#
# The protocol repository is found, in order:
#   1. $AFFIANT_PROTOCOL_REPO — a path to a local clone or worktree
#   2. a clone of `repository` from PROTOCOL_PIN into a temporary directory
set -euo pipefail

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/.." && pwd)
pin=$here/PROTOCOL_PIN

value() { sed -n "s/^$1=//p" "$pin" | head -1; }

repository=$(value repository)
commit=$(value commit)
tag=$(value tag)
vendored_rel=$(value vendored)
vendored=$root/$vendored_rel
ref=${tag:-$commit}

# Everything the driver reads at run time. Paths are relative to conformance/ in the
# protocol repository and keep their shape under the vendored root.
paths=(
  fixtures
  fixture.schema.json
  results.schema.json
  canonical-vector.schema.json
  lint/coverage-exemptions.json
  parity/MANIFEST.schema.json
)

sums() {
  # Deterministic, path-sorted, relative to the vendored root.
  ( cd "$vendored" && find . -type f ! -name SHA256SUMS -print0 \
      | LC_ALL=C sort -z \
      | xargs -0 sha256sum )
}

if [[ ${1:-} == --verify ]]; then
  [[ -f $vendored/SHA256SUMS ]] || { echo "sync.sh: no vendored copy at $vendored_rel — run conformance/sync.sh" >&2; exit 1; }
  actual=$(sums)
  if ! diff -u "$vendored/SHA256SUMS" <(printf '%s\n' "$actual"); then
    echo "sync.sh: the vendored conformance suite does not match SHA256SUMS." >&2
    echo "sync.sh: re-run conformance/sync.sh against the pin ($ref), and review the diff." >&2
    exit 1
  fi
  echo "sync.sh: vendored suite verified against SHA256SUMS ($(wc -l < "$vendored/SHA256SUMS") files), pin $ref."
  exit 0
fi

if [[ -n ${AFFIANT_PROTOCOL_REPO:-} ]]; then
  src=$AFFIANT_PROTOCOL_REPO
  cleanup=false
else
  src=$(mktemp -d)
  cleanup=true
  trap '[[ $cleanup == true ]] && rm -rf "$src"' EXIT
  git clone --quiet "$repository" "$src"
fi

git -C "$src" cat-file -e "$ref^{commit}" 2>/dev/null || git -C "$src" fetch --quiet origin "$ref"

echo "sync.sh: vendoring conformance/{$(IFS=,; echo "${paths[*]}")} from $ref"
rm -rf "$vendored"
mkdir -p "$vendored"
for p in "${paths[@]}"; do
  mkdir -p "$vendored/$(dirname "$p")"
  if git -C "$src" cat-file -e "$ref:conformance/$p" 2>/dev/null; then
    git -C "$src" archive "$ref" "conformance/$p" | tar -x -C "$vendored" --strip-components=1
  else
    echo "sync.sh: $p is not in $ref" >&2
    exit 1
  fi
done

sums > "$vendored/SHA256SUMS"
cat > "$vendored/README.md" <<EOF
# Vendored — do not edit

The conformance suite, copied from
[\`Sakwala/affiant-protocol\`](https://github.com/Sakwala/affiant-protocol) at the ref
\`../../../conformance/PROTOCOL_PIN\` names ($ref).

Every file here is a copy. Editing one changes what this repository's published parity
claim is about, so \`conformance/sync.sh --verify\` (which CI runs) fails on any change
that is not a re-vendor from a new pin. Regenerate with \`conformance/sync.sh\`.
EOF
sums > "$vendored/SHA256SUMS"
echo "sync.sh: vendored $(wc -l < "$vendored/SHA256SUMS") files into $vendored_rel"

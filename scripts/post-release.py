#!/usr/bin/env python3
"""Post-release bookkeeping for Daqifi.Core. Run on a fresh branch off main once a stable
release is on nuget.org; until the result merges, CI's baseline check fails every PR.

    python3 scripts/post-release.py 2.0.0 [--dry-run]

1. Sets PackageValidationBaselineVersion to the release.
2. Folds PublicAPI.Unshipped.txt into PublicAPI.Shipped.txt, applying *REMOVED* lines.
3. Regenerates CompatibilitySuppressions.xml against the new baseline (no file if nothing needs
   suppressing), then packs again without the generate flag to prove the result validates.

Step 2 works from the API files *at the release tag*, not from whatever Unshipped holds now:
anything merged after the tag has not shipped and stays in Unshipped. Edits files only; the
diff is yours to review and commit. Safe to run again.
"""
import argparse
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CSPROJ = "src/Daqifi.Core/Daqifi.Core.csproj"
SHIPPED = "src/Daqifi.Core/PublicAPI.Shipped.txt"
UNSHIPPED = "src/Daqifi.Core/PublicAPI.Unshipped.txt"
SUPPRESSIONS = "src/Daqifi.Core/CompatibilitySuppressions.xml"
REMOVED = "*REMOVED*"
HEADER = "#nullable enable"


def read(path):
    with open(os.path.join(ROOT, path), encoding="utf-8-sig") as f:
        return f.read()


def write(path, text):
    with open(os.path.join(ROOT, path), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def read_at(ref, path):
    result = subprocess.run(["git", "show", f"{ref}:{path}"], cwd=ROOT, capture_output=True)
    if result.returncode != 0:
        sys.exit(f"Cannot read {path} at {ref}; is the tag here? (git fetch --tags)\n"
                 + result.stderr.decode())
    return result.stdout.decode("utf-8-sig")


def entries(text, source):
    lines = [line for line in text.splitlines() if line.strip()]
    if not lines or lines[0] != HEADER or any(line.startswith("#") for line in lines[1:]):
        sys.exit(f"{source}: expected '{HEADER}' first and no other directive.")
    return lines[1:]


def surface(shipped_text, unshipped_text, source):
    """The API the two files describe together: Shipped, less *REMOVED*, plus the additions."""
    shipped = set(entries(shipped_text, f"{source} Shipped"))
    unshipped = entries(unshipped_text, f"{source} Unshipped")
    removed = {line[len(REMOVED):] for line in unshipped if line.startswith(REMOVED)}
    added = {line for line in unshipped if not line.startswith(REMOVED)}
    unmatched = sorted(removed - shipped)
    if unmatched:
        sys.exit(f"{source}: {REMOVED} entries that are not in Shipped:\n  " + "\n  ".join(unmatched))
    return (shipped - removed) | added


def api_file(lines):
    return "\n".join([HEADER, *sorted(lines)]) + "\n"


def pack(*args):
    # Never --no-build: it skips ApiCompat and passes without checking anything.
    with tempfile.TemporaryDirectory() as out:
        command = ["dotnet", "pack", CSPROJ, "-c", "Release", "-o", out, *args]
        print("$", " ".join(command), flush=True)
        if subprocess.run(command, cwd=ROOT).returncode != 0:
            sys.exit("dotnet pack failed. If it could not find the baseline package, the release "
                     "is not on nuget.org yet: run this again once it is.")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    parser.add_argument("version", help="the version just released, e.g. 2.0.0")
    parser.add_argument("--dry-run", action="store_true", help="say what would change; write and build nothing")
    args = parser.parse_args()

    version = args.version[1:] if args.version.startswith("v") else args.version
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        sys.exit(f"{version} is not a stable version. A prerelease is never the baseline, so there "
                 "is nothing to do after one.")

    csproj = read(CSPROJ)
    pattern = r"(<PackageValidationBaselineVersion>)([^<]*)(</PackageValidationBaselineVersion>)"
    matches = re.findall(pattern, csproj)
    if len(matches) != 1:
        sys.exit(f"{CSPROJ}: expected one PackageValidationBaselineVersion, found {len(matches)}.")
    baseline = matches[0][1].strip()
    if [int(n) for n in version.split(".")] < [int(n) for n in baseline.split(".")]:
        sys.exit(f"The baseline is already {baseline}, newer than {version}.")

    tag = f"v{version}"
    released = surface(read_at(tag, SHIPPED), read_at(tag, UNSHIPPED), tag)
    current = surface(read(SHIPPED), read(UNSHIPPED), "working tree")
    unshipped = (current - released) | {REMOVED + line for line in released - current}

    print(f"PackageValidationBaselineVersion: {baseline} -> {version}")
    before = set(entries(read(SHIPPED), SHIPPED))
    print(f"PublicAPI.Shipped.txt: +{len(released - before)} -{len(before - released)}, "
          f"now {len(released)} entries")
    print(f"PublicAPI.Unshipped.txt: {len(unshipped)} entries left (merged since {tag})")
    if args.dry_run:
        return

    write(CSPROJ, re.sub(pattern, rf"\g<1>{version}\g<3>", csproj))
    write(SHIPPED, api_file(released))
    write(UNSHIPPED, api_file(unshipped))

    # Deleted first, so that no file is left behind when the new baseline needs no suppressions:
    # an entry that suppresses nothing fails the pack ("Unnecessary suppressions found").
    path = os.path.join(ROOT, SUPPRESSIONS)
    if os.path.exists(path):
        os.remove(path)
    pack("-p:ApiCompatGenerateSuppressionFile=true")
    pack()

    print(f"\nCompatibilitySuppressions.xml: {'regenerated' if os.path.exists(path) else 'not needed, deleted'}.")
    print("Done. Review `git diff`, commit and open a PR.")


if __name__ == "__main__":
    main()

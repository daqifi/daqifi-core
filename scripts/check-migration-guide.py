#!/usr/bin/env python3
"""Checks docs/migration/2.0.md against the public API. Not part of CI; run it after changing either.

    python3 scripts/check-migration-guide.py              # names only, no build
    python3 scripts/check-migration-guide.py --compile    # also builds the code samples

1. Every *REMOVED* line in PublicAPI.Unshipped.txt has a table row that is about it: a code span in the
   left column is that type, or that type's member. Mentioning it as a parameter or in another row does
   not count, nor does a "Behaviour" row. A lost setter may be in a "Setters" row; a lost or re-typed
   getter may not. A line that differs only in a namespace needs a row for the type that moved.
   Overloads are not told apart: one row naming a method covers all its removed overloads.
2. Per CompatibilitySuppressions.xml: every member that implementers now have to add (CP0005, CP0006) is
   in that type's row under "If you implement", and every type that became sealed (CP0009) is in
   the list of newly sealed types there.
3. Every Core name in a right column or a 2.0 sample exists in the current API.
4. With --compile, the 1.x samples build against the 1.10.0 package and the 2.0 ones against this
   checkout.

Each run first checks itself on guides that are wrong on purpose.

The removed lines and suppressions are folded away once 2.0 has shipped, so from then on only 3 and 4
say anything.
"""
import argparse
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORE = os.path.join(ROOT, "src", "Daqifi.Core")
GUIDE = os.path.join(ROOT, "docs", "migration", "2.0.md")
IDENT = r"[A-Za-z_]\w*"
# Names a right column may use that are not Core's: BCL types and members, and C# placeholders.
NOT_CORE = set("""
Action ArgumentException ArgumentOutOfRangeException CancellationToken Console DateTime DateTimeOffset
Dispose Exception EventHandler FirstOrDefault First Func IAsyncDisposable IDisposable IReadOnlyList
InfiniteTimeSpan InvalidOperationException MaxValue NativeAOT Obsolete OfType OperationCanceledException
ObjectDisposedException ParamName Task TimeSpan Timeout TimeoutException TrimmerRootAssembly
Include Use UtcDateTime ValueTask Version WriteLine FromSeconds Where IAsyncEnumerable ToString System
Daqifi Core Communication Messages Device Export Logging Discovery DAQiFi AI0
""".split())
# What the samples can use without declaring it.
SAMPLE = """
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Firmware;

static class Sample{index}
{{
    static async Task Run({device}
        IFirmwareUpdateService updater, IProgress<FirmwareUpdateProgress> progress, string hexPath,
        string packagePath, string locationKey, CancellationToken cancellationToken)
    {{
        await Task.CompletedTask;
{body}
    }}
}}
"""
PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <NoWarn>CS0219;CS1998</NoWarn>
  </PropertyGroup>
  <ItemGroup>{reference}</ItemGroup>
</Project>
"""


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def api_lines():
    """The removed lines, and the lines the current API consists of."""
    shipped = read(os.path.join(CORE, "PublicAPI.Shipped.txt")).splitlines()
    unshipped = read(os.path.join(CORE, "PublicAPI.Unshipped.txt")).splitlines()
    mark = "*REMOVED*"
    removed = [line[len(mark):] for line in unshipped if line.startswith(mark)]
    gone = set(removed)
    current = [line for line in shipped if line not in gone]
    current += [line for line in unshipped if not line.startswith(mark)]
    return removed, [line for line in current if line and not line.startswith("#")]


def is_type(line):
    return re.search(r"[( ]", declaration(line)) is None


def declaration(line):
    """The line without its modifiers."""
    return re.sub(r"^~?(?:(?:static|virtual|override|abstract|const|readonly|sealed) )*", "", line)


def symbol(line):
    """(type, member) a PublicAPI line declares; member is None for the type itself."""
    text = declaration(line)
    path = re.split(r"[( ]", re.sub(r"<[^<>]*(?:<[^<>]*>)?[^<>]*>(?!\$)", "", text))[0].split(".")
    if is_type(line):
        return path[-1], None
    if path[-1] in ("get", "set", "init"):
        path.pop()
    return path[-2], path[-1]


def spans(text):
    return re.findall(r"`([^`]+)`", text)


def names(text):
    return set(re.findall(IDENT, text))


class Row:
    def __init__(self, intro, left, right, section):
        self.right, self.section = right, section
        self.behaviour = intro.startswith("Behaviour")
        self.setters = re.match(r"setters?\b", left, re.I) is not None
        self.types, self.members = set(), set()  # what the row is about
        # `A.X`, `.Y`, `Z` is about three members of A; `X` alone, of the types the intro names.
        current, scope = None, names(" ".join(spans(intro)))
        for span in spans(left):
            alone = re.fullmatch(rf"(?:Daqifi\.[\w.]+\.)?({IDENT})(?:<{IDENT}>)?", span)
            if alone:
                self.types.add(alone[1])
            head = re.sub(r"<[^<>]*>", "", span.split("(")[0].split(" = ")[0]).split()
            if not head or not re.fullmatch(rf"\.?{IDENT}(\.{IDENT})*", head[-1]):
                continue
            path = head[-1].split(".")
            if span.startswith("new "):
                self.members.add((path[-1], path[-1]))
            elif len(path) == 1:
                self.members |= {(owner, path[0]) for owner in scope | {current}}
            else:
                current = path[-2] or current
                self.members.add((current, path[-1]))

    def covers(self, type_name, member, setter=False):
        return (type_name, member) in self.members and (setter or not self.setters)


def tables(guide):
    """Every two-column row, with the paragraph directly above its table."""
    rows, intro, paragraph, section = [], "", [], ""
    in_table = in_code = ended = False
    for line in guide.splitlines():
        if line.startswith("```"):
            in_code = not in_code
        elif in_code:
            continue
        elif line.startswith("|"):
            if not in_table:
                intro, in_table, paragraph = " ".join(paragraph), True, []
                continue
            cells = [cell.strip() for cell in line.strip("|").split(" | ")]
            if len(cells) == 2 and not line.startswith("|---"):
                rows.append(Row(intro, cells[0], cells[1], section))
        else:
            in_table = False
            if line.startswith("## "):
                section = line[3:]
            if line.startswith("#"):
                paragraph = []
            elif line.strip():
                paragraph = [line] if ended else paragraph + [line]
            ended = not line.strip()
    return rows


def samples(guide):
    for body in re.findall(r"```csharp\n(.*?)```", guide, re.S):
        version, code = body.split("\n", 1)
        if version not in ("// 1.x", "// 2.0"):
            sys.exit(f"A sample does not start with '// 1.x' or '// 2.0': {version}")
        yield version[3:], code


def check_removed(rows, removed, current):
    removed_types = [declaration(line) for line in removed if is_type(line)]
    live_types = [declaration(line) for line in current if is_type(line)]
    spaces = {t.rpartition(".")[0] for t in removed_types + live_types} - {""}
    space = re.compile(r"\b(?:" + "|".join(map(re.escape, sorted(spaces, key=len, reverse=True))) + r")\.")

    def local(text):
        return space.sub("", text)

    live_names = set(map(local, live_types))
    still = set(map(local, current))
    rows = [row for row in rows if not row.behaviour]
    problems = []
    for line in removed:
        type_name, member = symbol(line)
        if line.startswith("virtual ") and member == "EqualityContract" or "operator" in line \
                or member in ("Equals", "GetHashCode", "ToString", "PrintMembers", "<Clone>$") or re.search(r"\(\S+ original\)", line):
            continue  # what the compiler generates for a record
        of_removed = next((t for t in removed_types if declaration(line).startswith(t + ".")), None)
        if is_type(line) or of_removed and (local(of_removed) not in live_names or local(line) in still):
            if not any(type_name in row.types for row in rows):
                problems.append(f"type {type_name}")
        else:
            setter = re.search(r"\.set -> ", line) is not None
            if not any(row.covers(type_name, member, setter) for row in rows):
                problems.append(f"{type_name}.{member}: {local(line)}")
    return sorted(set(problems))


def check_suppressions(guide, rows, suppressions):
    listed = re.search(r"^Newly sealed types\.(.*?)(?:\n\n|\Z)", guide, re.S | re.M)
    sealed = names(" ".join(spans(listed[1]))) if listed else set()
    implement = [row for row in rows if row.section.startswith("If you implement")]
    problems = []
    for rule, target in re.findall(r"<DiagnosticId>(CP0005|CP0006|CP0009)</DiagnosticId>\s*<Target>\w:([^<(]+)",
                                   suppressions):
        owner, member = target.split(".")[-2:]
        if rule == "CP0009" and member not in sealed:
            problems.append(f"sealed type {member}")
        if rule != "CP0009" and not any(owner in row.types and member in names(" ".join(spans(without_drops(row))))
                                        for row in implement):
            problems.append(f"new member to implement {owner}.{member}")
    return problems


def without_drops(row):
    """The right column less "Drop `X`", which names what is gone."""
    return re.sub(r"\bDrop [^.]*", "", row.right)


def self_test():
    def removed(guide, line, *current):
        return check_removed(tables(guide), [line], list(current))

    def table(left, right="x", intro="", section="Reference"):
        return f"## {section}\n\n{intro}\n\n| 1.x | 2.0 |\n|---|---|\n| {left} | {right} |\n"

    def suppressed(guide, rule, target):
        xml = f"<DiagnosticId>{rule}</DiagnosticId><Target>{target}</Target>"
        return check_suppressions(guide, tables(guide), xml)

    sealing = "Newly sealed types. These were open: `ScpiMessage`.\n"
    arguments = "T:Daqifi.Core.Device.ReconnectAttemptEventArgs"
    gone, getter, setter = "N.Gone", "N.Kept.Value.get -> int", "N.Kept.Value.set -> void"
    for expected, problems in (
            # A type that became sealed has to be in the list; a row about its timestamp is not that.
            (1, suppressed(table("`DateTime ReconnectAttemptEventArgs.Timestamp`") + "\n" + sealing, "CP0009", arguments)),
            (0, suppressed(sealing.replace("ScpiMessage", "ReconnectAttemptEventArgs"), "CP0009", arguments)),
            # A new interface member has to be in the implementer's row, as something to add.
            (1, suppressed(table("`IThing.Added`"), "CP0006", "M:N.IThing.Added")),
            (1, suppressed(table("`IThing`", "Drop `Added`", section="If you implement"), "CP0006", "M:N.IThing.Added")),
            (0, suppressed(table("`IThing`", "Add `Added`", section="If you implement"), "CP0006", "M:N.IThing.Added")),
            # A removed type has to be what the row is about.
            (1, removed(table("`Kept.Use(Gone)`"), gone)),
            (1, removed(table("`Kept.Gone`"), gone)),
            (1, removed(table("`Kept`", "`Gone`"), gone)),
            (1, removed(table("`Kept`", intro="On `Gone`:"), gone)),
            (1, removed(table("`Gone`", intro="Behaviour:"), gone)),
            (0, removed(table("`Gone`"), gone)),
            # So does a removed member, and on its own type.
            (1, removed(table("`Other.Value`, `Kept.More`"), getter, "N.Kept")),
            (1, removed(table("`Kept.Use(Value)`"), getter, "N.Kept")),
            (1, removed(table("`Kept.Value`", intro="Behaviour:"), getter, "N.Kept")),
            (1, removed(table("Setters `Kept.Value`"), getter, "N.Kept")),
            (1, removed(table("`Value`", intro="On `Kept`:\n\nNot this."), getter, "N.Kept")),
            (0, removed(table("Setters `Kept.More`, `.Value`"), setter, "N.Kept")),
            (0, removed(table("`int Kept.Value`"), getter, "N.Kept")),
            (0, removed(table("`Value`", intro="On `Kept`:"), getter, "N.Kept")),
            # A constructor is not the type.
            (1, removed(table("`Kept`"), "N.Kept.Kept() -> void", "N.Kept")),
            (0, removed(table("`new Kept()`"), "N.Kept.Kept() -> void", "N.Kept"))):
        if len(problems) != expected:
            sys.exit(f"This script is broken: expected {expected} problem(s), got {problems}")


def check_names(rows, guide, current):
    members = {}
    for line in current:
        type_name, member = symbol(line)
        members.setdefault(type_name, set()).add(member)
    known = set(NOT_CORE).union(members, *members.values())
    parts = [(" ".join(spans(without_drops(row))), row.right)
             for row in rows if row.section != "MCP server"]
    parts += [(re.sub(r'"[^"]*"|//.*', "", code), code) for version, code in samples(guide) if version == "2.0"]
    problems = set()
    for text, where in parts:
        for name in re.findall(rf"(?<![\w.]){IDENT}|(?<=\.){IDENT}", text):
            if name[0].isupper() and len(name) > 1 and name not in known:
                problems.add(f"{name}: {where.strip()[:100]}")
        for type_name, member in re.findall(rf"(?<![\w.])({IDENT})\.({IDENT})", text):
            # Not inherited ones: the guide names a member on the type that declares it.
            if type_name in members and member not in members[type_name]:
                problems.add(f"{type_name}.{member}: not declared by that type")
    return sorted(problems)


def compile_samples(guide, wrapper):
    references = {
        "1.x": '<PackageReference Include="Daqifi.Core" Version="1.10.0" />',
        "2.0": f'<ProjectReference Include="{CORE}/Daqifi.Core.csproj" />',
    }
    failed = False
    for version, reference in references.items():
        with tempfile.TemporaryDirectory() as folder:
            with open(os.path.join(folder, "samples.csproj"), "w", encoding="utf-8") as f:
                f.write(PROJECT.format(reference=reference))
            for index, (_, code) in enumerate(s for s in samples(guide) if s[0] == version):
                declares = re.search(r"\bvar device\b", code)
                with open(os.path.join(folder, f"Sample{index}.cs"), "w", encoding="utf-8") as f:
                    f.write(SAMPLE.format(index=index, body=code,
                                          device="" if declares else "DaqifiStreamingDevice device,"))
            project = os.path.join(folder, "samples.csproj")
            result = subprocess.run([*wrapper, "dotnet", "build", project, "-c", "Release", "-nodeReuse:false",
                                     "-p:OutputType=Library", "-v:q", "-nologo"])
            print(f"{version} samples: {'built' if result.returncode == 0 else 'FAILED'}")
            failed |= result.returncode != 0
    return failed


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--compile", action="store_true", help="also build the code samples")
    parser.add_argument("--wrapper", default="", help="command to run dotnet through, e.g. a build queue")
    args = parser.parse_args()

    self_test()
    guide = read(GUIDE)
    suppressions = os.path.join(CORE, "CompatibilitySuppressions.xml")
    suppressions = read(suppressions) if os.path.exists(suppressions) else ""
    rows = tables(guide)
    removed, current = api_lines()
    failed = False
    for title, problems in (
            (f"Removed and not covered ({len(removed)} *REMOVED* lines)", check_removed(rows, removed, current)),
            ("Implementer changes not covered", check_suppressions(guide, rows, suppressions)),
            ("Names that are not in the current API", check_names(rows, guide, current))):
        print(f"{title}: {len(problems) or 'none'}")
        for problem in problems:
            print(f"  {problem}")
        failed |= bool(problems)
    if args.compile:
        failed |= compile_samples(guide, args.wrapper.split())
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()

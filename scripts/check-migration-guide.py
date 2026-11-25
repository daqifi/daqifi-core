#!/usr/bin/env python3
"""Checks docs/migration/2.0.md against the public API. Not part of CI; run it after changing either.

    python3 scripts/check-migration-guide.py              # names only, no build
    python3 scripts/check-migration-guide.py --compile    # also builds the code samples

1. Every *REMOVED* line in PublicAPI.Unshipped.txt has a table row that is about it: a code span in the
   left column is that type, or that type's member. Mentioning it as a parameter or in another row does
   not count, nor does a "Behaviour" row or the summary. A lost setter may be in a "Setters" row; a lost
   or re-typed getter may not. A line that differs only in a namespace needs a row for the type that moved.
   A type that only changed its name covers the members that came with it unchanged, and no others.
2. Per CompatibilitySuppressions.xml: every member that implementers now have to add (CP0005, CP0006) is
   in that type's row under "If you implement", and every type that became sealed (CP0009) is in
   the list of newly sealed types there.
3. Every Core name in a right column or a 2.0 sample exists in the current API.
4. With --compile, the 1.x samples build against the 1.10.0 package and the 2.0 ones against this
   checkout.

What it cannot see, because no API file records it: rows about behaviour, MCP, base classes, default bodies,
class or struct, members that also moved up to an interface, a second way to call an overload, or what an
implementer drops (the tables for the members themselves have it); and whether a replacement that exists
is the right one. Overloads are told apart by their parameter count and by the types the row names, so a
row that writes `...` or no parentheses covers every overload.

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
    def __init__(self, intro, left, right, section, header=""):
        self.right, self.section = right, section
        self.behaviour = intro.startswith("Behaviour")
        # What to implement says what to add; that something went is for the other tables.
        self.summary = section.startswith("The changes most") or section.startswith("If you") and "implement" in header
        self.setters = re.match(r"setters?\b", left, re.I) is not None
        self.types, self.members = set(), []  # what the row is about; a member with its arguments
        # `A.X`, `.Y`, `Z` is about three members of A; `X` alone, of the types the intro names.
        current, scope = None, names(" ".join(spans(intro)))
        first = spans(right)[:1]
        self.successor = first[0] if first and right.startswith(f"`{first[0]}`") else None
        # "`A.X` as `Y`" is about A.X; `Y` is what it was typed as.
        for span in spans(re.sub(r"\b(?:as|returning|deriving from) `[^`]*`", "", left)):
            if "==" in span:
                continue
            arguments = argument_list(span)
            alone = re.fullmatch(rf"(?:Daqifi\.[\w.]+\.)?({IDENT})(?:<{IDENT}>)?", span)
            if alone:
                self.types.add(alone[1])
            head = re.sub(r"<[^<>]*>", "", span.split("(")[0].split(" = ")[0]).split()
            if not head or not re.fullmatch(rf"\.?{IDENT}(\.{IDENT})*", head[-1]):
                continue
            path = head[-1].split(".")
            if span.startswith("new "):
                self.members.append((path[-1], path[-1], arguments))
            elif len(path) == 1:
                self.members += [(owner, path[0], arguments) for owner in (scope | {current}) - {path[0]}]
            else:
                current = path[-2] or current
                self.members.append((current, path[-1], arguments))

    def covers(self, type_name, member, parameters):
        return any((t, m) == (type_name, member) and same_overload(arguments, parameters)
                   for t, m, arguments in self.members)


def argument_list(span):
    """What the span's first parentheses hold; None for no parentheses, or with a `...` among them."""
    opened = span.find("(")
    if opened < 0:
        return None
    depth, found = 0, [""]
    for character in span[opened + 1:]:
        if character == ")" and depth == 0:
            break
        depth += (character in "(<[{") - (character in ")>]}")
        if character == "," and depth == 0:
            found.append("")
        else:
            found[-1] += character
    if any("..." in argument for argument in found):
        return None
    return [argument.strip() for argument in found if argument.strip()]


def same_overload(arguments, parameters):
    """Whether a row's arguments can be those parameters: as many, and where one is a type, that type."""
    if arguments is None or parameters is None:
        return True

    def agrees(argument, parameter):
        named = re.match(IDENT, argument)
        if not named or not (named[0][0].isupper() or named[0] in ("int", "uint", "bool", "double", "string")):
            return True
        type_name = re.sub(r"<.*", "", re.sub(r"^(?:out|this) ", "", parameter)).split(" ")[0].rstrip("!?")
        return named[0] == type_name.split(".")[-1]

    return len(arguments) == len(parameters) and all(map(agrees, arguments, parameters))


def tables(guide):
    """Every two-column row, with the paragraph directly above its table."""
    rows, intro, header, paragraph, section = [], "", "", [], ""
    in_table = in_code = ended = False
    for line in guide.splitlines():
        if line.startswith("```"):
            in_code = not in_code
        elif in_code:
            continue
        elif line.startswith("|"):
            if not in_table:
                intro, header, in_table, paragraph = " ".join(paragraph), line, True, []
                continue
            cells = [cell.strip() for cell in line.strip("|").split(" | ")]
            if len(cells) == 2 and not line.startswith("|---"):
                rows.append(Row(intro, cells[0], cells[1], section, header))
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
    rows = [row for row in rows if not row.behaviour and not row.summary]
    removed_names = set(map(local, removed_types)) - live_names
    renamed = {t: row.successor for row in rows for t in row.types & removed_names
               if len(row.types) == 1 and row.successor in live_names}

    # Renamed and nothing else; otherwise a member of an enum can have kept its name and changed its meaning.
    only_renamed = {t for row in rows for t in row.types if row.right == f"`{row.successor}`"}

    def after_renames(text):
        return re.sub(IDENT, lambda match: renamed.get(match[0], match[0]), text)

    problems = []
    for line in removed:
        type_name, member = symbol(line)
        if line.startswith("virtual ") and member == "EqualityContract" or "operator" in line \
                or member in ("Equals", "GetHashCode", "ToString", "PrintMembers", "<Clone>$") or re.search(r"\(\S+ original\)", line):
            continue  # what the compiler generates for a record
        of_removed = next((t for t in removed_types if declaration(line).startswith(t + ".")), None)
        went = of_removed and local(of_removed) not in live_names and type_name not in renamed
        changed_enum = type_name in renamed and type_name not in only_renamed and re.search(r" = -?\d+ -> ", line)
        if is_type(line) or went or of_removed and not changed_enum and after_renames(local(line)) in still:
            if not any(type_name in row.types for row in rows):
                problems.append(f"type {type_name}")
        else:
            parameters = argument_list(declaration(line).split(" -> ")[0])
            covering = [row for row in rows if row.covers(type_name, member, parameters)]
            if re.search(r"\.set -> ", line):
                # A property that can now only be initialized needs that said, even if it was renamed too.
                owner = renamed.get(type_name, type_name)
                now = {f"{owner}.{member}"}.union(*(re.findall(rf"\b{owner}\.{IDENT}", row.right) for row in covering))
                if any(f"{name}.init -> void" in still and f"{name}.set -> void" not in still for name in now):
                    covering = [row for row in covering if row.setters]
            else:
                covering = [row for row in covering if not row.setters]
            if not covering:
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
        owner = declaration(line).split("(")[0].split(" ")[0].split(".")[:2]
        gone_too = [] if ".".join(owner) in current else [".".join(owner)]
        return check_removed(tables(guide), [line] + gone_too, list(current))

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
            (1, removed(table("`Gone`", section="The changes most callers need"), gone)),
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
            # A renamed type covers the members that came with it; a member that changed too needs a row.
            (0, removed(table("`Old`", "`New`"), "N.Old.Kept.get -> int", "N.New", "N.New.Kept.get -> int")),
            (1, removed(table("`LanChipInfo`", "`WifiChipInfo`"), "N.LanChipInfo.FwVersion.get -> string!",
                        "N.WifiChipInfo", "N.WifiChipInfo.FirmwareVersion.get -> string!")),
            (0, removed(table("`LanChipInfo`", "`WifiChipInfo`") + "| `LanChipInfo.FwVersion` | x |\n",
                        "N.LanChipInfo.FwVersion.get -> string!", "N.WifiChipInfo")),
            # An overload is told apart by how many parameters it has.
            (1, removed(table("`Kept.Go(a)`"), "N.Kept.Go(int a, int b = 0) -> void", "N.Kept")),
            (0, removed(table("`Kept.Go(a, b)`"), "N.Kept.Go(int a, System.Func<int, int>! b) -> void", "N.Kept")),
            (0, removed(table("`Kept.Go(...)`"), "N.Kept.Go(int a, int b = 0) -> void", "N.Kept")),
            (1, removed(table("`Kept.Go(TimeSpan)`"), "N.Kept.Go(System.Threading.CancellationToken t) -> void", "N.Kept")),
            # A member of an enum that was replaced, not renamed, needs a row even if a namesake exists.
            (1, removed(table("`Old`", "`New`, with more"), "N.Old.On = 1 -> N.Old", "N.New", "N.New.On = 1 -> N.New")),
            (1, removed(table("`Old`", "`New`, with more") + "| `x.Y == Old.On` | x |\n", "N.Old.On = 1 -> N.Old",
                        "N.New", "N.New.On = 1 -> N.New")),
            (0, removed(table("`Old`", "`New`"), "N.Old.On = 1 -> N.Old", "N.New", "N.New.On = 1 -> N.New")),
            # Losing the setter has to be said even when the property was renamed.
            (1, removed(table("`Kept.Value`", "`Kept.Other`"), setter, "N.Kept", "N.Kept.Other.init -> void")),
            (0, removed(table("`Kept.Value`", "`Kept.Other`"), setter, "N.Kept", "N.Kept.Other.set -> void")),
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

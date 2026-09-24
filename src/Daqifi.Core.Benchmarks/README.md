# Daqifi.Core.Benchmarks

A BenchmarkDotNet harness for the library's hot paths (issue #640).

Nine performance tickets in this repo each state a specific number — "~700 ms of fixed overhead
per SCPI exchange", "~1 s per port wasted on teardown" — and every one of those numbers was
produced by hand, once, on somebody's machine. None of them was reproducible afterwards and
nothing noticed when one regressed. This project is where those numbers get produced from now on.

## Running it

```bash
dotnet run -c Release --project src/Daqifi.Core.Benchmarks/Daqifi.Core.Benchmarks.csproj
```

With no arguments it lists the families and asks which to run. The usual BenchmarkDotNet switches
all work:

```bash
# one family
dotnet run -c Release --project src/Daqifi.Core.Benchmarks/Daqifi.Core.Benchmarks.csproj -- --filter '*Decode*'

# a quick look — fewer iterations, correspondingly less trustworthy
dotnet run -c Release --project src/Daqifi.Core.Benchmarks/Daqifi.Core.Benchmarks.csproj -- --filter '*' --job short

# what is available
dotnet run -c Release --project src/Daqifi.Core.Benchmarks/Daqifi.Core.Benchmarks.csproj -- --list flat
```

Release is not optional — BenchmarkDotNet refuses to measure an unoptimized build.

There is also a `Benchmarks` workflow on GitHub Actions (`workflow_dispatch`) that runs the suite
on demand and posts the tables to the run summary.

### Measuring on .NET 9

The project targets `net10.0` alone, so that `dotnet run -c Release` needs no `-f`. Measuring the
library on `net9.0` as well is a BenchmarkDotNet job, not a second target framework — add
`[SimpleJob(RuntimeMoniker.Net90)]` alongside `[SimpleJob(RuntimeMoniker.Net10_0)]` on the class
you care about and one run reports both runtimes side by side.

## What is measured, and why

| Family | Covers | The ticket behind it |
| --- | --- | --- |
| `StreamDecodeBenchmarks` | A stream frame arriving and becoming per-channel samples: channel-snapshot caching, timestamp reconstruction, gap detection, analog and digital unpacking, event dispatch. | #490 (per-frame allocation), #531 (measuring it with the wrong instrument) |
| `ProtobufFramingBenchmarks` | Varint length prefixes, frame boundaries, and the partial frame a read almost always ends on. | #490 |
| `StreamConsumerBenchmarks` | The consumer's read/append/drain loop over a scripted stream that never hands out a whole frame. | #490 |
| `SdCardParseBenchmarks` | All three log parsers, each measured twice: full drain (throughput) and time to first sample (latency). | #489 (parsers materialized the whole file before yielding anything) |
| `ChannelScalingBenchmarks` | The two per-sample conversions — device calibration and the user's transducer transform. | #534 |

The decode, framing and consumer families report **per frame** and the scaling family **per
sample** (`OperationsPerInvoke`), so the figures can be read straight against a sample rate rather
than divided first. The SD family reports per file.

`StreamConsumerBenchmarks` prints a `MinIterationTime` advisory. That is expected: its iterations
have to be whole drains of a scripted stream, which is tens of milliseconds rather than the 100 ms
BenchmarkDotNet would prefer. The measurement is stable regardless — the standard deviation is
well under 1% of the mean.

**The allocation column is the one to watch.** Timings on a shared runner move with the weather;
allocation does not. A change that reintroduces a per-frame allocation shows up as bytes-per-frame
climbing, and that reads the same on any machine.

## Not part of CI

This is deliberately not wired into the pull-request build. Benchmark timings on shared GitHub
runners are noisy enough that a perf gate would fail green PRs on a regular basis, which is
exactly the flake pattern (#634, #632) the repo has spent effort getting out of. The suite runs on
demand: before a release, or either side of a change that claims a performance win, with the table
pasted into the ticket making the claim.

If a gate is ever wanted, gate on allocation rather than time.

## Baseline

Non-protobuf rows are the `2a59fd1` run on `main` (the table that filed #697). The protobuf
SD-card rows are the post-#699 figures for the raw `AnalogInData` path, copied from #699
rather than re-measured here. No other timing row was hand-edited; those refresh on the next
`workflow_dispatch` of the Benchmarks workflow. That run also picks up this harness change:
`DecodeRawAnalogFrame` is the decode baseline, and the framing and consumer buffers are raw
counts, so the published means for those families still describe the previous harness.

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.5 (25F71) [Darwin 25.5.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.203, .NET 10.0.7, Arm64 RyuJIT armv8.0-a
```

**Stream decode** — per frame, 16 analog channels (plus 16 DIO in the combined case):

| Method | Mean | Allocated |
| --- | ---: | ---: |
| DecodeAnalogFloatFrame | 257.7 ns | 1.38 KB |
| DecodeRawAnalogFrame | 329.8 ns | 1.38 KB |
| DecodeCombinedFrame | 814.6 ns | 2.78 KB |

**Protobuf framing** — per frame:

| Method | Mean | Allocated |
| --- | ---: | ---: |
| ParseWholeFrames | 164.4 ns | 1.22 KB |
| ParseWithTrailingPartialFrame | 163.0 ns | 1.22 KB |

**Stream consumer** — per frame, end to end through the reader loop:

| Method | Mean | Allocated |
| --- | ---: | ---: |
| ConsumeScriptedStream | 197.0 ns | 1.33 KB |

**SD-card parsing** — per 10,000-row file, 8 analog channels:

| Method | Mean | Allocated |
| --- | ---: | ---: |
| ProtobufDrainAll | 2,091.4 µs | 13,170 KB |
| ProtobufTimeToFirstSample | 9.4 µs | 263 KB |
| CsvDrainAll | 5,412 µs | 15,212 KB |
| CsvTimeToFirstSample | 1.96 µs | 13.6 KB |
| JsonDrainAll | 7,042 µs | 7,181 KB |
| JsonTimeToFirstSample | 2.10 µs | 9.9 KB |

**Channel scaling** — per sample:

| Method | Mean | Allocated |
| --- | ---: | ---: |
| GetScaledValue | 4.25 ns | – |
| ApplyTransducerScaling | 0.67 ns | – |
| ApplyIdentityScaling | 0.67 ns | – |

The absolute numbers are a property of this machine, not of the library. What travels between
machines is the shape: the ratios between cases, and the allocation figures.

#699 closed #697: the reader yields each frame as it is decoded instead of parsing a whole 64 KB
buffer first, and the configuration pre-scan stops once the clock is known. `CsvTimeToFirstSample`
was 2.0 µs on that run, so first-sample latency is 4.7× CSV rather than 800×. Draining the whole
file got 2.2× faster as a side effect: the per-chunk list and message wrappers are gone.

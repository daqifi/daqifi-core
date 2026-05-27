# DAQiFi MCP Planning

**Status:** Draft RFC — open for feedback.

This folder contains the active planning effort for the **AI-native DAQiFi** initiative: a first-class Model Context Protocol (MCP) server for Nyquist devices, plus the launch positioning that surrounds it.

## Contents

- [`RFC.md`](RFC.md) — the proposal. Motivation, stack decision, architecture, MCP tool specification, safety model, distribution, phasing, marketing plan, and open questions.
- [`GITHUB_ISSUES.md`](GITHUB_ISSUES.md) — ready-to-paste issue drafts for the v0.1 milestone. One epic plus twelve implementation issues, with dependencies, effort estimates, and acceptance criteria.

## How to use this folder

1. **Review the RFC.** Comment on the PR with feedback on the strategic direction, the stack choice, the tool surface, the safety model, and the open questions.
2. **Once aligned, create the GitHub issues** from `GITHUB_ISSUES.md`. The drafts are written so each section maps to one issue; the dependency graph at the bottom of that doc shows the order.
3. **Build.** Start with `#mcp-1` (scaffold). `#mcp-5` (streaming infrastructure) and `#mcp-9` (safety) can run in parallel.
4. **Archive when superseded.** When the v0.1 launch ships and the plan is no longer the active reference, move this folder to `docs/archive/mcp-v0.1/` (matching the `docs/archive/simulator/` pattern).

## Quick links

- [Strategic summary](RFC.md#summary)
- [Stack decision rationale](RFC.md#tech-stack-decision)
- [Tool spec (appendix)](RFC.md#appendix-complete-tool-reference)
- [Safety model](RFC.md#safety-model)
- [Launch plan](RFC.md#marketing--launch-plan)
- [Open questions](RFC.md#open-questions)
- [v0.1 epic](GITHUB_ISSUES.md#epic-daqifi-mcp-v01--ai-native-control-surface)
- [v0.1 dependency graph](GITHUB_ISSUES.md#dependency-graph)

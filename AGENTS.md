# Agent instructions

Read `CLAUDE.md` first and follow its project conventions, safety constraints,
and documentation guidance. `GOAL.md`, `ROADMAP.md`, and `PROGRESS.md` are the
authoritative project tracking documents.

## Model selection and delegation

Every main agent should delegate routine/simple work to a cheaper capable
available model. For Claude-led sessions, use `claude-agent/haiku`; for
OpenAI/GPT-led sessions, use `openai/gpt-6-luna`. Examples include small changes,
reviews, documentation/encoding checks, evidence wording, and coverage spot
checks. Discover exact model IDs; do not guess. Verify delegated outputs. If the
preferred model is unavailable, disclose that and use an available cheaper,
capable equivalent, or ask if none is available. Keep complex concurrency,
durability, security, IPC protocol, and architecture work in the main model.

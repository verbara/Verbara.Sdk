# Technical Specifications

Design documents describing what a feature **is** and how it works. Paired with plans (how we'll ship it) and ADRs (why we chose the design).

## When to add a spec

- A non-trivial feature that multiple contributors will work on.
- A protocol or wire-format definition.
- An integration contract between two SDK packages.

For one-off implementations, the code + XML doc comments are enough.

## File convention

`{YYYY-MM-DD}-{feature-kebab}.md` — date-prefixed for chronological sort.

## Lifecycle

```
specs/              ← current designs
  └ archived/       ← superseded specs kept for history
```

## Catalog

<!-- Add one line per spec as they are created -->

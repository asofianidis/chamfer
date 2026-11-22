# 3. History-based persistent naming with geometric fallback

- Status: accepted
- Date: 2026-11-22

## Context

Features refer to faces and edges ("fillet this edge", "sketch on this face").
Kernel indices change whenever upstream geometry changes, so storing indices
breaks models as soon as a dimension is edited: the classic topological naming
problem.

## Decision

Every body carries a **name for each face**, derived from modelling history:

- Faces swept from a sketch are named after the feature and the sketch entity
  that generated them (`f3:side:12`), or the region for caps (`f3:end:1`).
- Operations transfer names through kernel history: faces that are carried
  over or modified keep their name; faces generated from a named edge or face
  get `feature:tag(source)` (e.g. a fillet's face `f5:fillet(f3:side:1|f3:end:1)`).
- Faces split into several pieces get `#k` suffixes ordered by position; faces
  with no history get a positional fallback.

**Edges** are named by the sorted pair of faces they separate
(`faceA|faceB`), disambiguated by position when a pair occurs more than once.

References (`TopoRef`) store the name **and** the position the entity had when
it was picked. Resolution tries the name first and falls back to the nearest
entity of the same kind, reporting a warning on the feature.

## Consequences

- References survive edits that change geometry but not the history that
  created it, which covers dimension changes, the most common edit. A
  regression test widens a sketch and checks that a fillet stays on the same
  logical corner.
- Edits that change topology (adding a sketch segment between two others) can
  still rename faces; the geometric fallback repairs most such cases and flags
  them.
- Names are readable strings, which makes failures debuggable.

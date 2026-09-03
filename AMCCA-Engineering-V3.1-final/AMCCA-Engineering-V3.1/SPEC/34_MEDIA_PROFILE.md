# 34 — Media Profiles

> **Normative language:** MUST/SHALL = mandatory; SHOULD = recommended unless a documented exception exists; MAY = optional.

## What a profile is

A named, versioned set of technical constraints for a delivery target: container, video and audio codec,
resolution, frame rate, aspect ratio, bitrate ceiling, duration bounds, loudness target, caption format,
and safe-area margins.

## Source of truth

Profiles are **configuration with evidence**, not constants. Each profile records `source_ref` and
`retrieved_at` for the platform requirements it encodes. Platform requirements change; a profile that
cannot say when it was last checked is a profile that will eventually produce rejected uploads.

A profile older than the configured staleness window degrades its platform capability to `UNVERIFIED`,
which blocks autonomous publication to that target (D-028).

## Application

The profile is resolved at `STORYBOARD_VERIFIED -> ASSET_GENERATION` so that generation targets the right
dimensions from the start. Technical QA validates the final render against the profile of every target.

## Multi-target

When a production targets several platforms with incompatible profiles, the pipeline produces one render
per profile family, each its own artifact version with its own lineage. It does not produce one render and
hope, and it does not silently drop a target whose profile cannot be met — an unmeetable target is rejected
at strategy time (`SPEC/30`).

## Loudness and safe areas

Loudness is normalised to the profile target and verified by measurement in audio QA, not assumed from the
encoder settings. Safe areas are validated against the storyboard's on-screen text placement, including
the placement of any required disclosure.

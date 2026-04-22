# SkillsForUnity — Agent Instructions

## Project Overview
Unity demo project showcasing AI-driven editor automation via the **Coplay** plugin and Claude integration. Uses **Universal Render Pipeline (URP)**.

## Unity Editor Automation
All Unity Editor operations (create GameObjects, scripts, materials, scenes, etc.) go through the Coplay MCP REST API.

**Always load and follow the skill before automating Unity:**
- Skill file: [.claude/skills/unity-skills/SKILL.md](.claude/skills/unity-skills/SKILL.md)
- Trigger: any request mentioning Unity, scenes, GameObjects, assets, editor, scripts, or automation

## Key Files
| Path | Purpose |
|------|---------|
| `Assets/Scripts/SpriteOutline2D.cs` | Runtime 2D sprite outline via `MaterialPropertyBlock` + URP shader |
| `Assets/Editor/FixSpriteImport.cs` | Editor utility to fix sprite import settings |
| `Assets/SampleScene.unity` | Main test scene |
| `.claude/settings.local.json` | MCP permissions for Coplay (13 allowed operations) |

## Conventions
- **Rendering**: URP only — use URP-compatible shaders and materials
- **Sprites**: Set MeshType to `FullRect` with 4px extrude (see `FixSpriteImport.cs` pattern)
- **Material updates**: Use `MaterialPropertyBlock` (not `material` property) to avoid instantiation
- **Editor scripts**: Place in `Assets/Editor/`; never reference editor types from runtime scripts
- **Script attributes**: Use `[ExecuteAlways]` for editor-preview scripts; add `[RequireComponent]` when a dependency is mandatory

## Packages
- `com.coplaydev.coplay` — Coplay AI plugin (beta), required for MCP tool calls
- `com.unity.render-pipelines.universal` v14.0.12
- `com.unity.textmeshpro` v3.0.7
- `com.unity.timeline` v1.7.7

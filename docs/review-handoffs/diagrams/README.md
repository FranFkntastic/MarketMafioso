# Outfitter policy diagram assets

Editable Mermaid sources (`.mmd`) are rendered with Mermaid CLI 11.16.0 using
`mermaid-config.json`, a fixed white background, a 1200-pixel base width, and a
1.25 scale factor. The pinned local render command is:

```powershell
npx --yes --cache "$env:TEMP\codex-mermaid-cli-11.16.0" `
  '@mermaid-js/mermaid-cli@11.16.0' `
  -c mermaid-config.json -b white -w 1200 -s 1.25 `
  -i <name>.mmd -o <name>.svg
```

The white canvas preserves the neutral theme's contrast in both light and dark
Obsidian themes. The SVGs are documentation assets only. Keep every SVG beside its same-name
Mermaid source and rerender both when a diagram changes.

# Lucide

KeiTerm uses the Lucide SVG assets from `@iconify-json/lucide` **1.2.141**. This is the same icon data used in the reviewed Lucide preview.

The package URL and SHA-512 integrity are recorded in `manifest.json`. Only the 42 SVGs needed for the 39 `Kei.Icon.*` resources and seven settings categories are retained in `svg/`. The SVG bodies come from the Iconify package without changes; the enclosing SVG element supplies Lucide's standard 24×24 viewport and 2px round strokes. Icon aliases retain their requested names, such as `trash-2`.

`generate.py` converts the SVG paths, circles and rounded rectangles to Avalonia centerline geometry. Regenerate the resources offline from the repository root:

```sh
python3 third-party/lucide/generate.py
```

This updates `DesignSystem/KeiIcons.axaml` and `ViewModels/Settings/SettingsIcons.cs`. Render every geometry with no fill, a 2px stroke, round caps and round joins, inside a fixed 24×24 canvas. Scale that canvas with a `Viewbox` to the requested display size; stretching individual geometry bounds changes the icon proportions and stroke weight.

The app loads the raw resources through `LucideIconResources`. `LucideIconGeometry` centers each actual stroked outline in the canvas and normalizes its longest visible dimension to 20 units while retaining a 2px stroke. Visual review at 16px adds small optical corrections: connection plug 1.06 (21.2 units), quick-connect lightning 1.04, and disconnected plug 1.03. Settings category icons pass through the same normalization in `LucideIconGeometryConverter`. Toolbar actions use 16px viewports; tree and file rows retain their own consistent row sizes.

Lucide is licensed under ISC. The included upstream `LICENSE` also contains the MIT notice for icons inherited from Feather; both notices must be retained. The complete license and this README are copied to `Licenses/Lucide/` in application and publish output. No icon runtime or Node.js dependency is required by the app.

# Material Design Icons

KeiTerm uses the official Material Design Icons 7.4.47 SVG assets by Pictogrammers.

Source: https://github.com/Templarian/MaterialDesign-SVG/tree/9e04201d4557e729822fb57f62a316c3dea1d4a8

The unmodified SVG files used by the app are retained in `svg/`; `manifest.json` maps each existing `Kei.Icon.*` resource to its upstream icon. `KeiIcons.axaml` embeds the same path data for Avalonia, prefixed with `F1` to preserve SVG's default nonzero fill rule. Render these resources inside a fixed 24×24 viewport rather than stretching their individual path bounds.

Icons are provided under Apache License 2.0. The upstream license notice is retained in `LICENSE`; the full license text is in `Apache-2.0.txt`. No brand or logo icons are included. These notices are copied into release distributions under `Licenses/MaterialDesignIcons/`.

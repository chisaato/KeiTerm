#!/usr/bin/env python3
"""Regenerate Avalonia geometry from the vendored Lucide SVG subset, offline."""

from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
ASSETS = Path(__file__).resolve().parent


def number(value):
    return format(float(value), '.12g')


def shape_path(element):
    tag = element.tag.rsplit('}', 1)[-1]
    attributes = element.attrib
    if 'transform' in attributes:
        raise ValueError('Lucide geometry must use the original 24×24 coordinates')
    if tag in {'svg', 'g'}:
        return ' '.join(shape_path(child) for child in element)
    if tag == 'path':
        path = attributes['d']
        # Each SVG element starts at (0, 0); a concatenated geometry keeps the
        # previous element's current point. Make only the initial move absolute.
        relative_move = re.match(r'^\s*m\s*([+-]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][+-]?\d+)?)\s*,?\s*([+-]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][+-]?\d+)?)(.*)$', path, re.S)
        if relative_move:
            x, y, rest = relative_move.groups()
            # Extra coordinates after a relative moveto are implicit lineto.
            if rest.lstrip() and rest.lstrip()[0] in '0123456789.+-':
                rest = 'l' + rest
            return f'M{x},{y}{rest}'
        return path
    if tag in {'circle', 'ellipse'}:
        x, y = float(attributes['cx']), float(attributes['cy'])
        rx = float(attributes.get('rx', attributes.get('r', 0)))
        ry = float(attributes.get('ry', attributes.get('r', 0)))
        return f'M{number(x-rx)},{number(y)} A{number(rx)},{number(ry)} 0 1 0 {number(x+rx)},{number(y)} A{number(rx)},{number(ry)} 0 1 0 {number(x-rx)},{number(y)} Z'
    if tag == 'rect':
        x, y = float(attributes.get('x', 0)), float(attributes.get('y', 0))
        width, height = float(attributes['width']), float(attributes['height'])
        rx = min(float(attributes.get('rx', attributes.get('ry', 0))), width / 2)
        ry = min(float(attributes.get('ry', attributes.get('rx', 0))), height / 2)
        right, bottom = x + width, y + height
        if not rx or not ry:
            return f'M{number(x)},{number(y)} H{number(right)} V{number(bottom)} H{number(x)} Z'
        return (f'M{number(x+rx)},{number(y)} H{number(right-rx)} '
                f'A{number(rx)},{number(ry)} 0 0 1 {number(right)},{number(y+ry)} V{number(bottom-ry)} '
                f'A{number(rx)},{number(ry)} 0 0 1 {number(right-rx)},{number(bottom)} H{number(x+rx)} '
                f'A{number(rx)},{number(ry)} 0 0 1 {number(x)},{number(bottom-ry)} V{number(y+ry)} '
                f'A{number(rx)},{number(ry)} 0 0 1 {number(x+rx)},{number(y)} Z')
    if tag == 'line':
        return f'M{attributes["x1"]},{attributes["y1"]} L{attributes["x2"]},{attributes["y2"]}'
    if tag in {'polygon', 'polyline'}:
        return 'M' + attributes['points'] + (' Z' if tag == 'polygon' else '')
    raise ValueError(f'Unsupported SVG element: {tag}')


def main():
    manifest = json.loads((ASSETS / 'manifest.json').read_text())
    paths = {}
    for name in set(manifest['icons'].values()) | set(manifest['settingsIcons'].values()):
        svg = ET.parse(ASSETS / 'svg' / (name + '.svg')).getroot()
        if svg.attrib['viewBox'] != '0 0 24 24':
            raise ValueError(f'Unexpected viewport: {name}')
        paths[name] = shape_path(svg)

    lines = ['<ResourceDictionary xmlns="https://github.com/avaloniaui"',
             '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">', '',
             f'    <!-- Lucide / @iconify-json/lucide {manifest["version"]}. ISC + Feather MIT notices. -->',
             '    <!-- 自动生成：python3 third-party/lucide/generate.py。请修改 manifest 或 SVG 后重新生成。 -->',
             '    <!-- 中心描边几何：24×24 视口、2px 线宽、Round 线帽/连接、Fill=null。 -->', '']
    for key, name in manifest['icons'].items():
        lines += [f'    <!-- lucide:{name} -->',
                  f'    <StreamGeometry x:Key="Kei.Icon.{key}">{paths[name]}</StreamGeometry>', '']
    lines += ['</ResourceDictionary>', '']
    (ROOT / 'src/Kei.Term.App/DesignSystem/KeiIcons.axaml').write_text('\n'.join(lines))

    settings = ['namespace Kei.Term.App.ViewModels.Settings;', '',
                '// Lucide 分类图标：24×24 中心描边几何，与 Kei.Icon 统一视口和线宽。',
                '// 自动生成：python3 third-party/lucide/generate.py。',
                'public static class SettingsIcons', '{']
    for key, name in manifest['settingsIcons'].items():
        settings += [f'    // lucide:{name}', f'    public const string {key} = "{paths[name]}";', '']
    settings.pop()
    settings += ['}', '']
    (ROOT / 'src/Kei.Term.App/ViewModels/Settings/SettingsIcons.cs').write_text('\n'.join(settings))
    print(f'Generated {len(manifest["icons"])} icon resources and {len(manifest["settingsIcons"])} categories from {len(paths)} SVGs')


if __name__ == '__main__':
    main()

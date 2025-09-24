using System;
using Avalonia.Media;

namespace Metriclonia.Monitor.Visualization;

internal sealed class ColorPalette
{
    private readonly Color[] _colors =
    {
        Color.FromRgb(0x4C, 0xAF, 0x50),
        Color.FromRgb(0x1E, 0x88, 0xE5),
        Color.FromRgb(0xEF, 0x6C, 0x00),
        Color.FromRgb(0x8E, 0x24, 0xAA),
        Color.FromRgb(0xFF, 0xB3, 0x00),
        Color.FromRgb(0xE5, 0x39, 0x35),
        Color.FromRgb(0x00, 0x97, 0xA7),
        Color.FromRgb(0x7C, 0x4D, 0xFF),
        Color.FromRgb(0x43, 0xA0, 0x47),
        Color.FromRgb(0xFF, 0x57, 0x22)
    };

    private int _index;

    public Color Next()
    {
        var color = _colors[_index % _colors.Length];
        _index++;
        return color;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
namespace SuiteRedes;

/// <summary>Cores por faixa de latência (usadas na linha do tempo por salto e no gráfico grande).</summary>
public static class Lat
{
    static SolidColorBrush B(byte a, string hex) { var c = (Color)ColorConverter.ConvertFromString(hex); var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
    public static readonly Brush Good = B(255, "#4ADE80"), Mid = B(255, "#FBBF24"), High = B(255, "#FB923C"), Lost = B(255, "#F87171"),
        LostSoft = B(120, "#F87171"), Back = B(255, "#0A1012"), Empty = B(255, "#1C272B");
    public static Brush For(double ms) => ms <= 80 ? Good : ms <= 150 ? Mid : High;
}


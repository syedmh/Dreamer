namespace Khilat.Api.Helpers;

using Khilat.Core.Enums;

public static class ColorHelper
{
    private static readonly Dictionary<ProductColor, string> ColorHexMap = new()
    {
        { ProductColor.Ivory, "#FFFFF0" },
        { ProductColor.Champagne, "#F7E7CE" },
        { ProductColor.DustyRose, "#DCAE96" },
        { ProductColor.SageGreen, "#B2AC88" },
        { ProductColor.MidnightNavy, "#191970" },
        { ProductColor.Burgundy, "#800020" },
        { ProductColor.Charcoal, "#36454F" },
        { ProductColor.PearlWhite, "#F0EAD6" },
        { ProductColor.Emerald, "#50C878" },
        { ProductColor.Mauve, "#E0B0FF" },
        { ProductColor.SlateBlue, "#6A5ACD" },
        { ProductColor.Terracotta, "#E2725B" }
    };

    public static string GetHex(ProductColor color) =>
        ColorHexMap.TryGetValue(color, out var hex) ? hex : "#000000";
}

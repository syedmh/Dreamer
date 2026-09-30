param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [switch] $WriteFiles,

    [switch] $SelfTestRightShoeValidation
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$expectedHash = '04665D7D9B164B00CA55011C02E6814A318D3B45074BE68402CA0C5507EDA1CF'
$hashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
$sourceStream = [System.IO.File]::OpenRead($SourcePath)
try {
    $actualHash = [System.BitConverter]::ToString(
        $hashAlgorithm.ComputeHash($sourceStream)
    ).Replace('-', '')
}
finally {
    $sourceStream.Dispose()
    $hashAlgorithm.Dispose()
}
if ($actualHash -ne $expectedHash) {
    throw "Avatar.jpg SHA-256 mismatch: expected $expectedHash, received $actualHash"
}

$compilerSource = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public sealed class AvatarAssetResult
{
    public string Name { get; set; }
    public string Sha256 { get; set; }
    public int OpaquePixels { get; set; }
    public int DarkOpaquePixels { get; set; }
    public int MinimumVisibleY { get; set; }
    public int MaximumVisibleY { get; set; }
}

public sealed class RightShoeValidationStats
{
    public int PixelsBelowFloorBoundary { get; set; }
    public int PixelsBelowContour { get; set; }
    public int PixelsOutsideLowerCorridor { get; set; }
    public int AnkleOverlapPixels { get; set; }
    public int DarkOpaquePixels { get; set; }
    public int MinimumVisibleY { get; set; }
    public int MaximumVisibleY { get; set; }
}

internal sealed class AvatarLayerDefinition
{
    public string Name { get; private set; }
    public Point[][] Polygons { get; private set; }

    public AvatarLayerDefinition(string name, params Point[][] polygons)
    {
        Name = name;
        Polygons = polygons != null && polygons.Length > 0 ? polygons : null;
    }
}

public static class DeterministicAvatarCompiler
{
    private const int Width = 896;
    private const int Height = 1195;
    private const int ShoeFloorBoundaryY = 1121;
    private const int ShoeContourMaximumY = 1170;
    private const int ShoeCorridorMinimumX = 526;
    private const int ShoeCorridorMaximumX = 614;
    private const int AnkleMinimumY = 1022;
    private const int AnkleMaximumY = 1072;
    private const int ArtworkMinimumX = 260;
    private const int ArtworkMaximumX = 635;
    private const int ArtworkMinimumY = 45;
    private const int ArtworkMaximumY = 1172;
    private const int DarkEdgeFeatherRadius = 14;
    private const int DistanceLimit = 16;
    private const int RightHandSourceMinimumX = 552;
    private const int RightHandSourceMaximumX = 625;
    private const int RightHandSourceMinimumY = 620;
    private const int RightHandSourceMaximumY = 718;
    private const int SourceInteriorMinimumEdgeDistance = 2;

    public static RightShoeValidationStats LastRightShoeValidationStats { get; private set; }

    private static readonly Point[] FloorReflectionMask = new Point[]
    {
        new Point(-1, 1121), new Point(526, 1121), new Point(526, 1138),
        new Point(532, 1153), new Point(539, 1166), new Point(553, 1172),
        new Point(579, 1172), new Point(598, 1167), new Point(610, 1153),
        new Point(615, 1135), new Point(615, 1121), new Point(Width, 1121),
        new Point(Width, Height), new Point(-1, Height)
    };

    private static readonly Point[][] TorsoJointOverlaps = new Point[][]
    {
        new Point[]
        {
            new Point(320, 274), new Point(323, 274), new Point(323, 285),
            new Point(320, 285)
        },
        new Point[]
        {
            new Point(571, 247), new Point(574, 247), new Point(574, 254),
            new Point(571, 254)
        },
        new Point[]
        {
            new Point(346, 253), new Point(356, 256), new Point(355, 259),
            new Point(345, 256)
        },
        new Point[]
        {
            new Point(567, 244), new Point(576, 252), new Point(574, 256),
            new Point(565, 248)
        },
        new Point[]
        {
            new Point(383, 799), new Point(451, 799), new Point(457, 837),
            new Point(378, 840)
        },
        new Point[]
        {
            new Point(498, 802), new Point(558, 805), new Point(574, 842),
            new Point(494, 845)
        }
    };

    // Keep only a source-aligned, body-facing axilla underlay with torsoHead.
    // These masks stay inside pivot+8..+140 and contain no synthetic pixels.
    private static readonly Point[][] TorsoUnderarmContinuityMasks = new Point[][]
    {
        new Point[]
        {
            new Point(337, 284), new Point(340, 284), new Point(340, 300),
            new Point(337, 300)
        },
        new Point[]
        {
            new Point(557, 286), new Point(560, 286), new Point(560, 300),
            new Point(557, 300)
        }
    };

    // The lower neck/collar is torso-owned rather than duplicated into the
    // articulated head. This preserves neutral reconstruction while reserving
    // overlap budget for the underarm continuity masks.
    private static readonly Point[] HeadStaticNeckMask = new Point[]
    {
        new Point(414, 226), new Point(479, 226), new Point(475, 278),
        new Point(418, 278)
    };

    // Restore only the source's narrow dark shoulder contour that the edge-connected
    // near-black segmentation otherwise mistakes for background. The long, tapered
    // strip closes the sleeve-to-torso alpha seam without adding a synthetic joint cap.
    private static readonly Point[] LeftShoulderContourBridge = new Point[]
    {
        new Point(320, 274), new Point(323, 274), new Point(327, 316),
        new Point(323, 317)
    };

    private static readonly Point[][] LeftUpperArmStaticShoulderMasks = new Point[][]
    {
        new Point[]
        {
            new Point(306, 232), new Point(359, 232), new Point(359, 316),
            new Point(306, 316)
        }
    };

    private static readonly Point[][] RightUpperArmStaticShoulderMasks = new Point[][]
    {
        new Point[]
        {
            new Point(545, 232), new Point(609, 232), new Point(609, 316),
            new Point(545, 316)
        }
    };

    private static readonly Point[][] LeftUpperLegStaticTunicMasks = new Point[][]
    {
        new Point[]
        {
            new Point(366, 798), new Point(452, 798), new Point(463, 832),
            new Point(458, 840), new Point(451, 844), new Point(432, 841),
            new Point(421, 837), new Point(411, 834), new Point(390, 832),
            new Point(374, 828), new Point(366, 820)
        }
    };

    private static readonly Point[][] RightUpperLegStaticTunicMasks = new Point[][]
    {
        new Point[]
        {
            new Point(486, 800), new Point(559, 801), new Point(591, 829),
            new Point(595, 842), new Point(585, 854), new Point(571, 859),
            new Point(561, 863), new Point(550, 867), new Point(539, 870),
            new Point(527, 872), new Point(514, 872), new Point(506, 868),
            new Point(501, 852), new Point(497, 836), new Point(490, 826)
        },
        new Point[]
        {
            new Point(548, 660), new Point(611, 660), new Point(611, 876),
            new Point(548, 876)
        }
    };

    private static readonly Point[][] LeftHandStaticTunicMasks = new Point[][]
    {
        new Point[]
        {
            new Point(343, 630), new Point(359, 630), new Point(359, 705),
            new Point(341, 705), new Point(341, 674), new Point(343, 668)
        }
    };

    private static readonly Point[][] RightHandStaticTunicMasks = new Point[][]
    {
        new Point[]
        {
            new Point(550, 636), new Point(561, 636), new Point(560, 650),
            new Point(558, 660), new Point(573, 670), new Point(567, 680),
            new Point(565, 692), new Point(550, 692)
        },
        new Point[]
        {
            new Point(552, 690), new Point(599, 690), new Point(599, 724),
            new Point(552, 724)
        },
        new Point[]
        {
            new Point(548, 660), new Point(611, 660), new Point(611, 876),
            new Point(548, 876)
        }
    };

    private static readonly AvatarLayerDefinition[] Layers = new AvatarLayerDefinition[]
    {
        new AvatarLayerDefinition("leftLeg.png",
            new Point[] {
                new Point(376, 806), new Point(446, 806), new Point(466, 860),
                new Point(466, 900), new Point(458, 934), new Point(445, 948),
                new Point(390, 944), new Point(374, 910), new Point(368, 850)
            },
            new Point[] {
                new Point(372, 906), new Point(478, 904), new Point(482, 940),
                new Point(482, 1048), new Point(476, 1080), new Point(392, 1062),
                new Point(374, 1000)
            },
            new Point[] {
                new Point(380, 1028), new Point(468, 1025), new Point(482, 1055),
                new Point(482, 1110), new Point(470, 1121), new Point(320, 1121),
                new Point(305, 1085), new Point(320, 1055)
            }),
        new AvatarLayerDefinition("rightLeg.png",
            new Point[] {
                new Point(493, 810), new Point(546, 810), new Point(583, 850),
                new Point(592, 900), new Point(584, 940), new Point(574, 956),
                new Point(516, 960), new Point(498, 930), new Point(487, 855)
            },
            new Point[] {
                new Point(494, 910), new Point(592, 910), new Point(600, 950),
                new Point(601, 1015), new Point(588, 1072), new Point(518, 1075),
                new Point(486, 1040)
            },
            new Point[] {
                new Point(525, 1024), new Point(592, 1024), new Point(608, 1060),
                new Point(625, 1090), new Point(615, 1135), new Point(610, 1153),
                new Point(598, 1167), new Point(579, 1173), new Point(553, 1173),
                new Point(539, 1167), new Point(532, 1153), new Point(526, 1138),
                new Point(520, 1110), new Point(505, 1080), new Point(512, 1055)
            }),
        new AvatarLayerDefinition("torsoHead.png"),
        new AvatarLayerDefinition("leftArm.png",
            new Point[] {
                new Point(323, 242), new Point(345, 240), new Point(351, 285),
                new Point(348, 360), new Point(343, 445), new Point(337, 486),
                new Point(291, 486), new Point(288, 420), new Point(293, 330),
                new Point(303, 275)
            },
            new Point[] {
                new Point(292, 456), new Point(344, 452), new Point(347, 510),
                new Point(343, 625), new Point(335, 640), new Point(294, 638),
                new Point(288, 610), new Point(287, 520)
            },
            new Point[] {
                new Point(292, 622), new Point(342, 620), new Point(355, 635),
                new Point(356, 665), new Point(347, 695), new Point(326, 708),
                new Point(302, 698), new Point(291, 674), new Point(286, 642)
            },
            new Point[] {
                new Point(337, 284), new Point(365, 284), new Point(365, 416),
                new Point(337, 416)
            }),
        new AvatarLayerDefinition("rightArm.png",
            new Point[] {
                new Point(551, 240), new Point(575, 244), new Point(594, 267),
                new Point(606, 310), new Point(623, 390), new Point(630, 450),
                new Point(628, 493), new Point(571, 493), new Point(565, 455),
                new Point(561, 365), new Point(561, 290)
            },
            new Point[] {
                new Point(566, 458), new Point(624, 452), new Point(633, 490),
                new Point(635, 560), new Point(628, 631), new Point(612, 650),
                new Point(570, 646), new Point(562, 610), new Point(560, 520)
            },
            new Point[] {
                new Point(566, 623), new Point(616, 620), new Point(625, 638),
                new Point(622, 668), new Point(607, 703), new Point(581, 718),
                new Point(558, 707), new Point(552, 684), new Point(555, 646)
            },
            new Point[] {
                new Point(532, 286), new Point(560, 286), new Point(560, 418),
                new Point(532, 418)
            },
            new Point[] {
                new Point(557, 239), new Point(562, 239), new Point(566, 244),
                new Point(560, 244)
            })
    };

    public static AvatarAssetResult[] Compile(string sourcePath, string outputDirectory, bool writeFiles)
    {
        using (Bitmap loaded = new Bitmap(sourcePath))
        {
            if (loaded.Width != Width || loaded.Height != Height)
            {
                throw new InvalidOperationException(
                    String.Format("Avatar.jpg dimensions mismatch: expected {0}x{1}, received {2}x{3}",
                        Width, Height, loaded.Width, loaded.Height));
            }

            using (Bitmap source = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(source))
                {
                    graphics.Clear(Color.Black);
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    graphics.DrawImage(loaded, new Rectangle(0, 0, Width, Height));
                }

                byte[] sourcePixels = ReadPixels(source);
                bool[] sourceDefinedRightHandInterior =
                    CreateSourceDefinedRightHandInteriorMask(sourcePixels);
                bool[] background = SegmentEdgeConnectedBackground(
                    sourcePixels,
                    sourceDefinedRightHandInterior);
                byte[] foreground = CreateForeground(
                    sourcePixels,
                    background,
                    sourceDefinedRightHandInterior);
                List<AvatarAssetResult> results = new List<AvatarAssetResult>();

                LastRightShoeValidationStats = InspectRightShoe(foreground);
                AssertRightShoeInvariants(LastRightShoeValidationStats);

                results.Add(CreateAsset(
                    "idle.png",
                    foreground,
                    null,
                    outputDirectory,
                    writeFiles));

                foreach (AvatarLayerDefinition layer in Layers)
                {
                    results.Add(CreateAsset(
                        layer.Name,
                        foreground,
                        layer.Polygons,
                        outputDirectory,
                        writeFiles));
                }

                return results.ToArray();
            }
        }
    }

    public static void AssertRightShoeInvariants(RightShoeValidationStats stats)
    {
        if (stats.PixelsBelowFloorBoundary < 2500)
        {
            throw new InvalidOperationException(
                String.Format(
                    "Right-shoe contour invariant failed: expected at least 2500 alpha pixels below y={0}, received {1}; the shoe may have been globally clipped.",
                    ShoeFloorBoundaryY,
                    stats.PixelsBelowFloorBoundary));
        }
        if (stats.MaximumVisibleY < 1168 || stats.MaximumVisibleY > ShoeContourMaximumY)
        {
            throw new InvalidOperationException(
                String.Format(
                    "Right-shoe natural-reach invariant failed: expected maximum visible y in 1168..{0}, received {1}.",
                    ShoeContourMaximumY,
                    stats.MaximumVisibleY));
        }
        if (stats.MinimumVisibleY < 1022 || stats.MinimumVisibleY > 1028)
        {
            throw new InvalidOperationException(
                String.Format(
                    "Right-shoe upper-contour invariant failed: expected minimum visible y in 1022..1028, received {0}.",
                    stats.MinimumVisibleY));
        }
        if (stats.PixelsBelowContour != 0)
        {
            throw new InvalidOperationException(
                String.Format(
                    "Right-shoe reflection-exclusion invariant failed: expected no alpha below y={0}, received {1} pixels.",
                    ShoeContourMaximumY,
                    stats.PixelsBelowContour));
        }
        if (stats.PixelsOutsideLowerCorridor != 0)
        {
            throw new InvalidOperationException(
                String.Format(
                    "Right-shoe corridor invariant failed: expected lower alpha only within x={0}..{1}, received {2} outside pixels.",
                    ShoeCorridorMinimumX,
                    ShoeCorridorMaximumX,
                    stats.PixelsOutsideLowerCorridor));
        }
        if (stats.AnkleOverlapPixels < 1200)
        {
            throw new InvalidOperationException(
                String.Format(
                    "Right-shoe ankle-overlap invariant failed: expected at least 1200 overlapping pixels over y={0}..{1}, received {2}.",
                    AnkleMinimumY,
                    AnkleMaximumY,
                    stats.AnkleOverlapPixels));
        }
        if (stats.DarkOpaquePixels < 3000)
        {
            throw new InvalidOperationException(
                String.Format(
                    "Right-shoe dark-detail invariant failed: expected at least 3000 dark opaque pixels, received {0}.",
                    stats.DarkOpaquePixels));
        }
    }

    public static string SelfTestClippedRightShoeValidation()
    {
        RightShoeValidationStats clipped = new RightShoeValidationStats
        {
            PixelsBelowFloorBoundary = 0,
            PixelsBelowContour = 0,
            PixelsOutsideLowerCorridor = 0,
            AnkleOverlapPixels = 1800,
            DarkOpaquePixels = 3516,
            MinimumVisibleY = 1024,
            MaximumVisibleY = 1121
        };

        try
        {
            AssertRightShoeInvariants(clipped);
        }
        catch (InvalidOperationException error)
        {
            if (error.Message.IndexOf("globally clipped", StringComparison.Ordinal) >= 0)
            {
                return error.Message;
            }
            throw new InvalidOperationException(
                "Right-shoe validator rejected the clipped simulation for an unexpected reason.",
                error);
        }

        throw new InvalidOperationException(
            "Right-shoe validator accepted a simulated y>=1122 global cutoff.");
    }

    private static RightShoeValidationStats InspectRightShoe(byte[] foreground)
    {
        Point[][] rightLeg = FindLayerPolygons("rightLeg.png");
        Point[] rightLowerLeg = rightLeg[1];
        Point[] rightShoe = rightLeg[2];
        RightShoeValidationStats stats = new RightShoeValidationStats
        {
            MinimumVisibleY = Height,
            MaximumVisibleY = -1
        };

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (!PointInPolygon(x, y, rightShoe))
                {
                    continue;
                }

                int offset = (y * Width + x) * 4;
                int alpha = foreground[offset + 3];
                if (alpha > 0 && y > ShoeContourMaximumY)
                {
                    stats.PixelsBelowContour++;
                }
                if (
                    alpha > 0
                    && y > ShoeFloorBoundaryY
                    && (x < ShoeCorridorMinimumX || x > ShoeCorridorMaximumX))
                {
                    stats.PixelsOutsideLowerCorridor++;
                }
                if (alpha < 32)
                {
                    continue;
                }

                stats.MinimumVisibleY = Math.Min(stats.MinimumVisibleY, y);
                stats.MaximumVisibleY = Math.Max(stats.MaximumVisibleY, y);
                if (y > ShoeFloorBoundaryY)
                {
                    stats.PixelsBelowFloorBoundary++;
                }
                if (
                    y >= AnkleMinimumY
                    && y <= AnkleMaximumY
                    && PointInPolygon(x, y, rightLowerLeg))
                {
                    stats.AnkleOverlapPixels++;
                }
                if (alpha >= 180)
                {
                    double luminance =
                        foreground[offset + 2] * 0.2126
                        + foreground[offset + 1] * 0.7152
                        + foreground[offset] * 0.0722;
                    if (luminance <= 70)
                    {
                        stats.DarkOpaquePixels++;
                    }
                }
            }
        }

        if (stats.MinimumVisibleY == Height)
        {
            stats.MinimumVisibleY = -1;
        }
        return stats;
    }

    private static Point[][] FindLayerPolygons(string name)
    {
        foreach (AvatarLayerDefinition layer in Layers)
        {
            if (String.Equals(layer.Name, name, StringComparison.Ordinal))
            {
                return layer.Polygons;
            }
        }
        throw new InvalidOperationException("Missing avatar layer definition: " + name);
    }

    private static byte[] ReadPixels(Bitmap bitmap)
    {
        Rectangle rectangle = new Rectangle(0, 0, Width, Height);
        BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[Math.Abs(data.Stride) * Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            if (data.Stride == Width * 4)
            {
                return pixels;
            }

            byte[] packed = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(pixels, y * data.Stride, packed, y * Width * 4, Width * 4);
            }
            return packed;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool[] SegmentEdgeConnectedBackground(
        byte[] pixels,
        bool[] sourceDefinedRightHandInterior)
    {
        bool[] background = new bool[Width * Height];
        bool[] protectedArtwork = CreateProtectedArtworkMask(
            pixels,
            sourceDefinedRightHandInterior);
        int[] queue = new int[Width * Height];
        int head = 0;
        int tail = 0;

        Action<int, int> enqueue = delegate(int x, int y)
        {
            int index = y * Width + x;
            if (
                !background[index]
                && !protectedArtwork[index]
                && IsEstimatedBackgroundPixel(pixels, index)
            )
            {
                background[index] = true;
                queue[tail++] = index;
            }
        };

        for (int x = 0; x < Width; x++)
        {
            enqueue(x, 0);
            enqueue(x, Height - 1);
        }
        for (int y = 0; y < Height; y++)
        {
            enqueue(0, y);
            enqueue(Width - 1, y);
        }

        while (head < tail)
        {
            int index = queue[head++];
            int x = index % Width;
            int y = index / Width;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int nextY = y + offsetY;
                if (nextY < 0 || nextY >= Height)
                {
                    continue;
                }
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }
                    int nextX = x + offsetX;
                    if (nextX >= 0 && nextX < Width)
                    {
                        enqueue(nextX, nextY);
                    }
                }
            }
        }

        return background;
    }

    private static bool[] CreateProtectedArtworkMask(
        byte[] pixels,
        bool[] sourceDefinedRightHandInterior)
    {
        bool[] protectedArtwork = new bool[Width * Height];
        for (int y = ArtworkMinimumY; y <= ArtworkMaximumY; y++)
        {
            for (int x = ArtworkMinimumX; x <= ArtworkMaximumX; x++)
            {
                if (IsFloorOrReflection(x, y))
                {
                    continue;
                }

                int pixelIndex = y * Width + x;
                if (sourceDefinedRightHandInterior[pixelIndex])
                {
                    protectedArtwork[pixelIndex] = true;
                    continue;
                }

                int offset = pixelIndex * 4;
                int blue = pixels[offset];
                int green = pixels[offset + 1];
                int red = pixels[offset + 2];
                int maximum = Math.Max(red, Math.Max(green, blue));
                int minimum = Math.Min(red, Math.Min(green, blue));
                int luminance = (red * 54 + green * 183 + blue * 19) / 256;
                int chroma = maximum - minimum;
                if (
                    luminance >= 64
                    || (chroma >= 15 && maximum >= 28)
                )
                {
                    protectedArtwork[pixelIndex] = true;
                }
            }
        }
        return protectedArtwork;
    }

    private static bool[] CreateSourceDefinedRightHandInteriorMask(byte[] source)
    {
        bool[] candidates = new bool[Width * Height];
        for (int y = RightHandSourceMinimumY; y <= RightHandSourceMaximumY; y++)
        {
            for (int x = RightHandSourceMinimumX; x <= RightHandSourceMaximumX; x++)
            {
                int pixelIndex = y * Width + x;
                candidates[pixelIndex] = IsHandSkinTone(source, x, y);
            }
        }

        bool[] exterior = new bool[candidates.Length];
        for (int index = 0; index < candidates.Length; index++)
        {
            exterior[index] = !candidates[index];
        }

        int[] distance;
        int[] nearestExterior;
        CalculateBackgroundDistance(exterior, out distance, out nearestExterior);

        bool[] interior = new bool[candidates.Length];
        for (int index = 0; index < candidates.Length; index++)
        {
            interior[index] =
                candidates[index]
                && distance[index] >= SourceInteriorMinimumEdgeDistance;
        }
        return interior;
    }

    private static bool IsEstimatedBackgroundPixel(byte[] pixels, int pixelIndex)
    {
        int offset = pixelIndex * 4;
        int blue = pixels[offset];
        int green = pixels[offset + 1];
        int red = pixels[offset + 2];
        int maximum = Math.Max(red, Math.Max(green, blue));
        int minimum = Math.Min(red, Math.Min(green, blue));
        int luminance = (red * 54 + green * 183 + blue * 19) / 256;
        return maximum <= 80 && luminance <= 72 && maximum - minimum <= 28;
    }

    private static byte[] CreateForeground(
        byte[] source,
        bool[] background,
        bool[] sourceDefinedRightHandInterior)
    {
        bool[] matte = new bool[background.Length];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pixelIndex = y * Width + x;
                matte[pixelIndex] = background[pixelIndex] || IsFloorOrReflection(x, y);
            }
        }

        int[] distance;
        int[] nearestBackground;
        CalculateBackgroundDistance(matte, out distance, out nearestBackground);

        byte[] foreground = new byte[source.Length];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pixelIndex = y * Width + x;
                int offset = pixelIndex * 4;
                if (sourceDefinedRightHandInterior[pixelIndex])
                {
                    foreground[offset] = source[offset];
                    foreground[offset + 1] = source[offset + 1];
                    foreground[offset + 2] = source[offset + 2];
                    foreground[offset + 3] = 255;
                    continue;
                }

                bool restoresLeftShoulderContour =
                    background[pixelIndex]
                    && PointInPolygon(x, y, LeftShoulderContourBridge);
                if (
                    IsFloorOrReflection(x, y)
                    || (background[pixelIndex] && !restoresLeftShoulderContour)
                )
                {
                    continue;
                }

                int alpha = restoresLeftShoulderContour
                    ? 255
                    : FeatherAlpha(distance[pixelIndex]);
                int darkEdgeAlpha = 255;
                if (
                    !restoresLeftShoulderContour
                    && distance[pixelIndex] <= DarkEdgeFeatherRadius
                )
                {
                    darkEdgeAlpha = DarkEdgeAlpha(source, offset);
                    alpha = Math.Min(alpha, darkEdgeAlpha);
                }
                if (alpha <= 0)
                {
                    continue;
                }

                int backgroundIndex = nearestBackground[pixelIndex];
                int backgroundOffset = backgroundIndex >= 0 ? backgroundIndex * 4 : offset;
                foreground[offset] = Decontaminate(
                    source[offset],
                    source[backgroundOffset],
                    alpha);
                foreground[offset + 1] = Decontaminate(
                    source[offset + 1],
                    source[backgroundOffset + 1],
                    alpha);
                foreground[offset + 2] = Decontaminate(
                    source[offset + 2],
                    source[backgroundOffset + 2],
                    alpha);
                if (alpha >= 180 && darkEdgeAlpha == 192)
                {
                    EnsureMinimumChroma(source, foreground, offset, 48);
                }
                if (
                    alpha < 255
                    && IsAmbiguousShoulderMattePixel(foreground, offset, x, y)
                )
                {
                    alpha = Math.Min(alpha, 160);
                }
                foreground[offset + 3] = (byte)alpha;
            }
        }
        RestoreSourceSeamContinuity(source, foreground);
        return foreground;
    }

    private static void RestoreSourceSeamContinuity(
        byte[] source,
        byte[] foreground)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 4;
                if (foreground[offset + 3] >= 32)
                {
                    continue;
                }

                bool shoulderBridge =
                    IsLeftShoulderContinuityBridgePixel(x, y)
                    || IsRightShoulderContinuityBridgePixel(x, y);
                bool qameezBridge = IsRightQameezContinuityBridgePixel(x, y);
                if (!shoulderBridge && !qameezBridge)
                {
                    continue;
                }

                foreground[offset] = source[offset];
                foreground[offset + 1] = source[offset + 1];
                foreground[offset + 2] = source[offset + 2];
                foreground[offset + 3] = 255;
            }
        }
    }

    private static bool IsLeftShoulderContinuityBridgePixel(int x, int y)
    {
        return
            (y == 253 && x >= 344 && x <= 348)
            || (y == 254 && x >= 344 && x <= 346)
            || (y == 265 && x >= 332 && x <= 334);
    }

    private static bool IsRightShoulderContinuityBridgePixel(int x, int y)
    {
        if (y == 240 && x == 564)
        {
            return true;
        }
        if (y < 250 || y > 266)
        {
            return false;
        }
        int start = 576 - ((y - 250) * 3 / 16);
        return x >= start && x <= start + 2;
    }

    private static bool IsRightQameezContinuityBridgePixel(int x, int y)
    {
        int upperEnd = -1;
        switch (y)
        {
            case 807:
            case 808:
            case 809:
            case 810:
            case 811:
            case 812:
            case 813:
                upperEnd = 595;
                break;
            case 814:
                upperEnd = 596;
                break;
            case 815:
                upperEnd = 598;
                break;
            case 816:
                upperEnd = 601;
                break;
            case 817:
                upperEnd = 603;
                break;
            case 818:
                upperEnd = 605;
                break;
            case 819:
                upperEnd = 606;
                break;
            case 820:
                upperEnd = 608;
                break;
            case 821:
                upperEnd = 609;
                break;
        }
        if (upperEnd >= 595 && x >= 595 && x <= upperEnd)
        {
            return true;
        }

        switch (y)
        {
            case 858:
                return x >= 571 && x <= 575;
            case 859:
                return x >= 568 && x <= 571;
            case 860:
                return x >= 564 && x <= 568;
            case 861:
                return x >= 561 && x <= 564;
            case 862:
                return x >= 558 && x <= 561;
            case 863:
                return (x >= 555 && x <= 559) || (x >= 580 && x <= 589);
            case 864:
                return x >= 552 && x <= 555;
            case 865:
                return x >= 549 && x <= 552;
            default:
                return false;
        }
    }

    private static bool IsAmbiguousShoulderMattePixel(
        byte[] foreground,
        int offset,
        int x,
        int y)
    {
        if (
            !PointInAnyPolygon(x, y, LeftUpperArmStaticShoulderMasks)
            && !PointInAnyPolygon(x, y, RightUpperArmStaticShoulderMasks)
        )
        {
            return false;
        }

        int blue = foreground[offset];
        int green = foreground[offset + 1];
        int red = foreground[offset + 2];
        int maximum = Math.Max(red, Math.Max(green, blue));
        int minimum = Math.Min(red, Math.Min(green, blue));
        bool white =
            red >= 105
            && green >= 105
            && blue >= 105
            && maximum - minimum <= 85;
        bool greenGarment =
            green >= 45
            && green >= red + 12
            && green >= blue + 8;
        return white && greenGarment;
    }

    private static bool IsFloorOrReflection(int x, int y)
    {
        return PointInPolygon(x, y, FloorReflectionMask);
    }

    private static void CalculateBackgroundDistance(
        bool[] background,
        out int[] distance,
        out int[] nearestBackground)
    {
        distance = new int[Width * Height];
        nearestBackground = new int[Width * Height];
        int[] queue = new int[Width * Height];
        int head = 0;
        int tail = 0;
        for (int index = 0; index < distance.Length; index++)
        {
            distance[index] = Int32.MaxValue;
            nearestBackground[index] = -1;
            if (background[index])
            {
                distance[index] = 0;
                nearestBackground[index] = index;
                queue[tail++] = index;
            }
        }

        while (head < tail)
        {
            int index = queue[head++];
            int nextDistance = distance[index] + 1;
            if (nextDistance > DistanceLimit)
            {
                continue;
            }

            int x = index % Width;
            int y = index / Width;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int nextY = y + offsetY;
                if (nextY < 0 || nextY >= Height)
                {
                    continue;
                }
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }
                    int nextX = x + offsetX;
                    if (nextX < 0 || nextX >= Width)
                    {
                        continue;
                    }
                    int nextIndex = nextY * Width + nextX;
                    if (distance[nextIndex] <= nextDistance)
                    {
                        continue;
                    }
                    distance[nextIndex] = nextDistance;
                    nearestBackground[nextIndex] = nearestBackground[index];
                    queue[tail++] = nextIndex;
                }
            }
        }
    }

    private static int FeatherAlpha(int distance)
    {
        if (distance <= 1) return 64;
        if (distance == 2) return 128;
        if (distance == 3) return 192;
        return 255;
    }

    private static int DarkEdgeAlpha(byte[] source, int offset)
    {
        int blue = source[offset];
        int green = source[offset + 1];
        int red = source[offset + 2];
        int maximum = Math.Max(red, Math.Max(green, blue));
        int minimum = Math.Min(red, Math.Min(green, blue));
        int luminance = (red * 54 + green * 183 + blue * 19) / 256;
        int chroma = maximum - minimum;
        if (luminance > 115 || chroma >= 45)
        {
            return 255;
        }

        // Moderately chromatic dark artwork (notably the brown shoes) remains
        // opaque enough to satisfy dark-detail invariants. Decontamination at
        // alpha 192 plus the hue-preserving chroma floor keeps it distinct
        // from the neutral source matte.
        return chroma >= 20 ? 192 : 160;
    }

    private static void EnsureMinimumChroma(
        byte[] source,
        byte[] foreground,
        int offset,
        int minimumChroma)
    {
        int outputBlue = foreground[offset];
        int outputGreen = foreground[offset + 1];
        int outputRed = foreground[offset + 2];
        int maximum = Math.Max(outputRed, Math.Max(outputGreen, outputBlue));
        int minimum = Math.Min(outputRed, Math.Min(outputGreen, outputBlue));
        if (maximum - minimum >= minimumChroma)
        {
            return;
        }

        byte targetMinimum = (byte)Math.Max(0, maximum - minimumChroma);
        int sourceBlue = source[offset];
        int sourceGreen = source[offset + 1];
        int sourceRed = source[offset + 2];
        if (sourceBlue <= sourceGreen && sourceBlue <= sourceRed)
        {
            foreground[offset] = Math.Min(foreground[offset], targetMinimum);
        }
        else if (sourceGreen <= sourceRed)
        {
            foreground[offset + 1] = Math.Min(foreground[offset + 1], targetMinimum);
        }
        else
        {
            foreground[offset + 2] = Math.Min(foreground[offset + 2], targetMinimum);
        }
    }

    private static byte Decontaminate(byte value, byte background, int alpha)
    {
        if (alpha >= 255) return value;
        int numerator = value * 255 - background * (255 - alpha);
        int decontaminated = (numerator + alpha / 2) / alpha;
        return (byte)Math.Max(0, Math.Min(255, decontaminated));
    }

    private static AvatarAssetResult CreateAsset(
        string name,
        byte[] foreground,
        Point[][] polygons,
        string outputDirectory,
        bool writeFiles)
    {
        byte[] layerPixels = new byte[foreground.Length];
        int opaquePixels = 0;
        int darkOpaquePixels = 0;
        int minimumVisibleY = Height;
        int maximumVisibleY = -1;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (!ShouldIncludeAssetPixel(name, foreground, x, y, polygons))
                {
                    continue;
                }

                int offset = (y * Width + x) * 4;
                byte alpha = foreground[offset + 3];
                if (alpha == 0)
                {
                    continue;
                }

                byte blue = foreground[offset];
                byte green = foreground[offset + 1];
                byte red = foreground[offset + 2];
                if (
                    String.Equals(name, "torsoHead.png", StringComparison.Ordinal)
                    && alpha == 255
                    && IsRightWaistBackingPixel(foreground, x, y)
                )
                {
                    byte backingBlue;
                    byte backingGreen;
                    byte backingRed;
                    if (
                        !TryFindNearbyStaticGarmentColor(
                            foreground,
                            x,
                            y,
                            true,
                            out backingBlue,
                            out backingGreen,
                            out backingRed)
                    )
                    {
                        throw new InvalidOperationException(
                            String.Format(
                                "Unable to derive right-waist garment backing at ({0},{1}).",
                                x,
                                y));
                    }
                    blue = backingBlue;
                    green = backingGreen;
                    red = backingRed;
                }

                layerPixels[offset] = blue;
                layerPixels[offset + 1] = green;
                layerPixels[offset + 2] = red;
                layerPixels[offset + 3] = alpha;
                if (alpha >= 32)
                {
                    opaquePixels++;
                    minimumVisibleY = Math.Min(minimumVisibleY, y);
                    maximumVisibleY = Math.Max(maximumVisibleY, y);
                }
                if (alpha >= 180)
                {
                    double luminance =
                        foreground[offset + 2] * 0.2126
                        + foreground[offset + 1] * 0.7152
                        + foreground[offset] * 0.0722;
                    if (luminance <= 70)
                    {
                        darkOpaquePixels++;
                    }
                }
            }
        }

        byte[] pngBytes;
        using (Bitmap output = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
        {
            Rectangle rectangle = new Rectangle(0, 0, Width, Height);
            BitmapData data = output.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(layerPixels, 0, data.Scan0, layerPixels.Length);
            }
            finally
            {
                output.UnlockBits(data);
            }

            using (MemoryStream stream = new MemoryStream())
            {
                output.Save(stream, ImageFormat.Png);
                pngBytes = NormalizePng(stream.ToArray());
            }
        }

        if (writeFiles)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(Path.Combine(outputDirectory, name), pngBytes);
        }

        string hash;
        using (SHA256 algorithm = SHA256.Create())
        {
            hash = BitConverter.ToString(algorithm.ComputeHash(pngBytes)).Replace("-", String.Empty);
        }

        return new AvatarAssetResult
        {
            Name = name,
            Sha256 = hash,
            OpaquePixels = opaquePixels,
            DarkOpaquePixels = darkOpaquePixels,
            MinimumVisibleY = minimumVisibleY == Height ? -1 : minimumVisibleY,
            MaximumVisibleY = maximumVisibleY
        };
    }

    private static bool ShouldIncludeAssetPixel(
        string name,
        byte[] foreground,
        int x,
        int y,
        Point[][] polygons)
    {
        if (String.Equals(name, "idle.png", StringComparison.Ordinal))
        {
            return true;
        }

        if (String.Equals(name, "torsoHead.png", StringComparison.Ordinal))
        {
            return !PointInAnyMovingLayer(foreground, x, y)
                || PointInAnyPolygon(x, y, TorsoJointOverlaps)
                || IsNarrowShoulderContourOverlapPixel(x, y)
                || PointInAnyPolygon(x, y, TorsoUnderarmContinuityMasks)
                || IsRightWaistBackingPixel(foreground, x, y);
        }

        if (polygons == null || !PointInAnyPolygon(x, y, polygons))
        {
            return false;
        }

        if (!IsSemanticMovingPixel(name, foreground, x, y))
        {
            return false;
        }

        return true;
    }

    private static bool IsNarrowShoulderContourOverlapPixel(int x, int y)
    {
        if (y == 265 && x >= 332 && x <= 334)
        {
            return true;
        }
        if ((y == 251 || y == 252) && x >= 575 && x <= 576)
        {
            return true;
        }
        if (y == 257 && x == 576)
        {
            return true;
        }
        if (y >= 258 && y <= 262 && x >= 574 && x <= 576)
        {
            return true;
        }
        return y >= 263
            && y <= 266
            && x >= 573
            && x <= 575;
    }

    private static bool IsRightWaistBackingPixel(byte[] foreground, int x, int y)
    {
        if (x < 555 || x > 575 || y < 650 || y > 690)
        {
            return false;
        }
        if (!IsMovingArmPixel("rightArm.png", foreground, x, y))
        {
            return false;
        }

        int offset = (y * Width + x) * 4;
        int alpha = foreground[offset + 3];
        if (alpha != 255 || !IsRightWaistBackingLatticePixel(foreground, x, y))
        {
            return false;
        }

        byte blue;
        byte green;
        byte red;
        return TryFindNearbyStaticGarmentColor(
            foreground,
            x,
            y,
            true,
            out blue,
            out green,
            out red);
    }

    private static bool IsRightWaistBackingLatticePixel(
        byte[] foreground,
        int x,
        int y)
    {
        if (
            x <= 560
            && y >= 650
            && y <= 670
            && IsOpaqueMovingArmPixel("rightArm.png", foreground, x, y)
        )
        {
            return true;
        }

        if (
            y == 659
            && x >= 557
            && x <= 574
            && IsOpaqueMovingArmPixel("rightArm.png", foreground, x, y)
        )
        {
            return true;
        }
        if (
            y == 678
            && x == 568
            && IsOpaqueMovingArmPixel("rightArm.png", foreground, x, y)
        )
        {
            return true;
        }

        int firstRunStart;
        int firstRunEnd;
        if (
            ((y >= 660 && y <= 670) || y == 690)
            && TryFindFirstOpaqueArmRun(
                foreground,
                y,
                out firstRunStart,
                out firstRunEnd)
            && x >= firstRunStart
            && x <= firstRunEnd
        )
        {
            return true;
        }

        int longestRunStart;
        int longestRunEnd;
        return y >= 660
            && y <= 690
            && TryFindLongestOpaqueArmRun(
                foreground,
                y,
                out longestRunStart,
                out longestRunEnd)
            && x >= longestRunStart
            && x <= Math.Min(longestRunStart + 1, longestRunEnd);
    }

    private static bool TryFindFirstOpaqueArmRun(
        byte[] foreground,
        int y,
        out int runStart,
        out int runEnd)
    {
        runStart = -1;
        runEnd = -1;
        for (int x = 555; x <= 606; x++)
        {
            if (!IsOpaqueMovingArmPixel("rightArm.png", foreground, x, y))
            {
                if (runStart >= 0)
                {
                    return true;
                }
                continue;
            }
            if (runStart < 0)
            {
                runStart = x;
            }
            runEnd = x;
        }
        return runStart >= 0;
    }

    private static bool TryFindLongestOpaqueArmRun(
        byte[] foreground,
        int y,
        out int longestRunStart,
        out int longestRunEnd)
    {
        longestRunStart = -1;
        longestRunEnd = -1;
        int runStart = -1;
        for (int x = 555; x <= 607; x++)
        {
            bool opaque =
                x <= 606
                && IsOpaqueMovingArmPixel("rightArm.png", foreground, x, y);
            if (opaque)
            {
                if (runStart < 0)
                {
                    runStart = x;
                }
                continue;
            }
            if (
                runStart >= 0
                && (
                    longestRunStart < 0
                    || x - runStart > longestRunEnd - longestRunStart + 1
                )
            )
            {
                longestRunStart = runStart;
                longestRunEnd = x - 1;
            }
            runStart = -1;
        }
        return longestRunStart >= 0;
    }

    private static bool IsMovingArmPixel(
        string name,
        byte[] foreground,
        int x,
        int y)
    {
        Point[][] polygons = FindLayerPolygons(name);
        return PointInAnyPolygon(x, y, polygons)
            && IsSemanticMovingPixel(name, foreground, x, y)
            && foreground[(y * Width + x) * 4 + 3] >= 32;
    }

    private static bool IsOpaqueMovingArmPixel(
        string name,
        byte[] foreground,
        int x,
        int y)
    {
        return IsMovingArmPixel(name, foreground, x, y)
            && foreground[(y * Width + x) * 4 + 3] == 255;
    }

    private static bool TryFindNearbyStaticGarmentColor(
        byte[] foreground,
        int x,
        int y,
        bool whiteOnly,
        out byte blue,
        out byte green,
        out byte red)
    {
        const int maximumDistance = 36;
        for (int distance = 0; distance <= maximumDistance; distance++)
        {
            int yMinimum = Math.Max(0, y - distance);
            int yMaximum = Math.Min(Height - 1, y + distance);
            int xMinimum = Math.Max(0, x - distance);
            int xMaximum = Math.Min(Width - 1, x + distance);
            for (int candidateY = yMinimum; candidateY <= yMaximum; candidateY++)
            {
                for (int candidateX = xMinimum; candidateX <= xMaximum; candidateX++)
                {
                    if (
                        Math.Max(
                            Math.Abs(candidateX - x),
                            Math.Abs(candidateY - y)) != distance
                    )
                    {
                        continue;
                    }

                    int candidateOffset =
                        (candidateY * Width + candidateX) * 4;
                    if (
                        foreground[candidateOffset + 3] != 255
                        || PointInAnyMovingLayer(
                            foreground,
                            candidateX,
                            candidateY)
                        || IsHandArtworkPixel(
                            foreground,
                            candidateX,
                            candidateY)
                    )
                    {
                        continue;
                    }

                    int candidateBlue = foreground[candidateOffset];
                    int candidateGreen = foreground[candidateOffset + 1];
                    int candidateRed = foreground[candidateOffset + 2];
                    int maximum = Math.Max(
                        candidateRed,
                        Math.Max(candidateGreen, candidateBlue));
                    int minimum = Math.Min(
                        candidateRed,
                        Math.Min(candidateGreen, candidateBlue));
                    bool whiteGarment =
                        candidateRed >= 105
                        && candidateGreen >= 105
                        && candidateBlue >= 105
                        && maximum - minimum <= 85;
                    bool greenGarment =
                        candidateGreen >= 45
                        && candidateGreen >= candidateRed + 12
                        && candidateGreen >= candidateBlue + 8;
                    if (
                        (!whiteOnly && (whiteGarment || greenGarment))
                        || (whiteOnly && whiteGarment)
                    )
                    {
                        blue = (byte)candidateBlue;
                        green = (byte)candidateGreen;
                        red = (byte)candidateRed;
                        return true;
                    }
                }
            }
        }

        blue = 0;
        green = 0;
        red = 0;
        return false;
    }

    private static bool TryFindNearbyStaticGreenGarmentColor(
        byte[] foreground,
        int x,
        int y,
        out byte blue,
        out byte green,
        out byte red)
    {
        const int maximumDistance = 36;
        for (int distance = 0; distance <= maximumDistance; distance++)
        {
            int yMinimum = Math.Max(0, y - distance);
            int yMaximum = Math.Min(Height - 1, y + distance);
            int xMinimum = Math.Max(0, x - distance);
            int xMaximum = Math.Min(Width - 1, x + distance);
            for (int candidateY = yMinimum; candidateY <= yMaximum; candidateY++)
            {
                for (int candidateX = xMinimum; candidateX <= xMaximum; candidateX++)
                {
                    if (
                        Math.Max(
                            Math.Abs(candidateX - x),
                            Math.Abs(candidateY - y)) != distance
                    )
                    {
                        continue;
                    }

                    int candidateOffset =
                        (candidateY * Width + candidateX) * 4;
                    if (
                        foreground[candidateOffset + 3] != 255
                        || PointInAnyMovingLayer(
                            foreground,
                            candidateX,
                            candidateY)
                        || IsHandArtworkPixel(
                            foreground,
                            candidateX,
                            candidateY)
                    )
                    {
                        continue;
                    }

                    int candidateBlue = foreground[candidateOffset];
                    int candidateGreen = foreground[candidateOffset + 1];
                    int candidateRed = foreground[candidateOffset + 2];
                    int maximum = Math.Max(
                        candidateRed,
                        Math.Max(candidateGreen, candidateBlue));
                    int minimum = Math.Min(
                        candidateRed,
                        Math.Min(candidateGreen, candidateBlue));
                    bool whiteGarment =
                        candidateRed >= 105
                        && candidateGreen >= 105
                        && candidateBlue >= 105
                        && maximum - minimum <= 85;
                    bool greenGarment =
                        candidateGreen >= 45
                        && candidateGreen >= candidateRed + 12
                        && candidateGreen >= candidateBlue + 8;
                    if (greenGarment && !whiteGarment)
                    {
                        blue = (byte)candidateBlue;
                        green = (byte)candidateGreen;
                        red = (byte)candidateRed;
                        return true;
                    }
                }
            }
        }

        blue = 0;
        green = 0;
        red = 0;
        return false;
    }

    private static bool IsSemanticMovingPixel(string name, byte[] foreground, int x, int y)
    {
        if (IsRootSleeveContourPixel(name, foreground, x, y))
        {
            return true;
        }

        if (
            String.Equals(name, "rightArm.png", StringComparison.Ordinal)
            && x >= 568
            && x <= 593
            && y >= 682
            && y <= 689
            && foreground[(y * Width + x) * 4 + 3] >= 32
        )
        {
            return true;
        }

        if (IsWhiteSleeveCapPixel(name, foreground, x, y))
        {
            return true;
        }

        if (
            (String.Equals(name, "leftArm.png", StringComparison.Ordinal) && y <= 279)
            || (String.Equals(name, "rightArm.png", StringComparison.Ordinal) && y <= 250)
        )
        {
            return false;
        }

        if (IsSourcePinnedStaticShoulderPixel(name, foreground, x, y))
        {
            return false;
        }

        if (IsSourcePinnedStaticTunicPixel(name, foreground, x, y))
        {
            return false;
        }

        bool excludesWaistcoat =
            name.IndexOf("Arm", StringComparison.Ordinal) >= 0
            || name.IndexOf("Forearm", StringComparison.Ordinal) >= 0
            || name.IndexOf("Hand", StringComparison.Ordinal) >= 0;
        return !excludesWaistcoat || !IsWaistcoatGreen(foreground, x, y);
    }

    private static bool IsWhiteSleeveCapPixel(
        string name,
        byte[] foreground,
        int x,
        int y)
    {
        bool insideShoulder = false;
        if (String.Equals(name, "leftArm.png", StringComparison.Ordinal))
        {
            insideShoulder = PointInAnyPolygon(x, y, LeftUpperArmStaticShoulderMasks);
        }
        else if (String.Equals(name, "rightArm.png", StringComparison.Ordinal))
        {
            insideShoulder = PointInAnyPolygon(x, y, RightUpperArmStaticShoulderMasks);
        }
        if (!insideShoulder)
        {
            return false;
        }

        int offset = (y * Width + x) * 4;
        int blue = foreground[offset];
        int green = foreground[offset + 1];
        int red = foreground[offset + 2];
        int alpha = foreground[offset + 3];
        int maximum = Math.Max(red, Math.Max(green, blue));
        int minimum = Math.Min(red, Math.Min(green, blue));
        return alpha >= 32
            && red >= 105
            && green >= 105
            && blue >= 105
            && maximum - minimum <= 85;
    }

    private static bool IsRootSleeveContourPixel(
        string name,
        byte[] foreground,
        int x,
        int y)
    {
        bool insideContour =
            (
                String.Equals(name, "leftArm.png", StringComparison.Ordinal)
                && x >= 320
                && x <= 322
                && y >= 280
                && y <= 284
            )
            || (
                String.Equals(name, "rightArm.png", StringComparison.Ordinal)
                && (
                    (x >= 571 && x <= 573 && y >= 247 && y <= 252)
                    || (x == 573 && y == 253)
                )
            );
        if (!insideContour)
        {
            return false;
        }

        int offset = (y * Width + x) * 4;
        int blue = foreground[offset];
        int green = foreground[offset + 1];
        int red = foreground[offset + 2];
        int alpha = foreground[offset + 3];
        int luminance = (red * 54 + green * 183 + blue * 19) / 256;
        return alpha >= 32 && luminance < 110;
    }

    private static bool IsSourcePinnedStaticShoulderPixel(
        string name,
        byte[] foreground,
        int x,
        int y)
    {
        bool insideStaticShoulder = false;
        if (String.Equals(name, "leftArm.png", StringComparison.Ordinal))
        {
            insideStaticShoulder = PointInAnyPolygon(x, y, LeftUpperArmStaticShoulderMasks);
        }
        else if (String.Equals(name, "rightArm.png", StringComparison.Ordinal))
        {
            insideStaticShoulder = PointInAnyPolygon(x, y, RightUpperArmStaticShoulderMasks);
        }
        if (!insideStaticShoulder)
        {
            return false;
        }

        int offset = (y * Width + x) * 4;
        int blue = foreground[offset];
        int green = foreground[offset + 1];
        int red = foreground[offset + 2];
        int alpha = foreground[offset + 3];
        int maximum = Math.Max(red, Math.Max(green, blue));
        int minimum = Math.Min(red, Math.Min(green, blue));
        int luminance = (red * 54 + green * 183 + blue * 19) / 256;
        bool protectedGarment =
            IsWaistcoatGreen(foreground, x, y)
            || (
                alpha >= 180
                && red >= 105
                && green >= 105
                && blue >= 105
                && maximum - minimum <= 85
            );
        bool insidePinnedBrightCap =
            (
                String.Equals(name, "leftArm.png", StringComparison.Ordinal)
                && x >= 313
                && x <= 323
                && y >= 274
                && y <= 295
            )
            || (
                String.Equals(name, "rightArm.png", StringComparison.Ordinal)
                && x >= 572
                && x <= 599
                && y >= 248
                && y <= 295
            );
        bool insideFixedUpperCap =
            (
                String.Equals(name, "leftArm.png", StringComparison.Ordinal)
                && y <= 279
            )
            || (
                String.Equals(name, "rightArm.png", StringComparison.Ordinal)
                && y <= 250
            );
        return protectedGarment
            || insideFixedUpperCap
            || (alpha >= 32 && insidePinnedBrightCap && luminance >= 110);
    }

    private static bool IsSourcePinnedStaticTunicPixel(
        string name,
        byte[] foreground,
        int x,
        int y)
    {
        if (String.Equals(name, "leftLeg.png", StringComparison.Ordinal))
        {
            return PointInAnyPolygon(x, y, LeftUpperLegStaticTunicMasks);
        }
        if (String.Equals(name, "rightLeg.png", StringComparison.Ordinal))
        {
            return PointInAnyPolygon(x, y, RightUpperLegStaticTunicMasks);
        }
        if (String.Equals(name, "leftArm.png", StringComparison.Ordinal))
        {
            return PointInAnyPolygon(x, y, LeftHandStaticTunicMasks)
                && !IsHandArtworkPixel(foreground, x, y);
        }
        if (String.Equals(name, "rightArm.png", StringComparison.Ordinal))
        {
            return PointInAnyPolygon(x, y, RightHandStaticTunicMasks)
                && (y >= 691 || !IsHandArtworkPixel(foreground, x, y));
        }
        return false;
    }

    private static bool IsHandArtworkPixel(byte[] foreground, int x, int y)
    {
        if (IsHandSkinTone(foreground, x, y))
        {
            return true;
        }

        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            int neighborY = y + offsetY;
            if (neighborY < 0 || neighborY >= Height)
            {
                continue;
            }
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int neighborX = x + offsetX;
                if (
                    neighborX >= 0
                    && neighborX < Width
                    && IsHandSkinTone(foreground, neighborX, neighborY)
                )
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsHandSkinTone(byte[] foreground, int x, int y)
    {
        int offset = (y * Width + x) * 4;
        int blue = foreground[offset];
        int green = foreground[offset + 1];
        int red = foreground[offset + 2];
        int alpha = foreground[offset + 3];
        return alpha >= 32
            && red >= 70
            && red >= green + 8
            && green >= blue + 4
            && red >= blue + 18;
    }

    private static bool IsWaistcoatGreen(byte[] foreground, int x, int y)
    {
        int offset = (y * Width + x) * 4;
        int blue = foreground[offset];
        int green = foreground[offset + 1];
        int red = foreground[offset + 2];
        int alpha = foreground[offset + 3];
        return alpha >= 32 && green >= 45 && green >= red + 12 && green >= blue + 8;
    }

    private static bool PointInAnyMovingLayer(byte[] foreground, int x, int y)
    {
        foreach (AvatarLayerDefinition layer in Layers)
        {
            if (
                layer.Polygons != null
                && PointInAnyPolygon(x, y, layer.Polygons)
                && IsSemanticMovingPixel(layer.Name, foreground, x, y)
            )
            {
                return true;
            }
        }
        return false;
    }

    private static bool PointInAnyPolygon(int x, int y, Point[][] polygons)
    {
        foreach (Point[] polygon in polygons)
        {
            if (PointInPolygon(x, y, polygon))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] NormalizePng(byte[] encoded)
    {
        byte[] signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (encoded.Length < signature.Length)
        {
            throw new InvalidOperationException("Generated PNG is truncated.");
        }
        for (int index = 0; index < signature.Length; index++)
        {
            if (encoded[index] != signature[index])
            {
                throw new InvalidOperationException("Generated image is not a PNG.");
            }
        }

        bool sawHeader = false;
        bool sawImageData = false;
        bool sawEnd = false;
        int offset = signature.Length;
        using (MemoryStream normalized = new MemoryStream())
        {
            normalized.Write(signature, 0, signature.Length);
            while (offset < encoded.Length)
            {
                if (offset + 12 > encoded.Length)
                {
                    throw new InvalidOperationException("Generated PNG contains a truncated chunk.");
                }

                int length =
                    (encoded[offset] << 24)
                    | (encoded[offset + 1] << 16)
                    | (encoded[offset + 2] << 8)
                    | encoded[offset + 3];
                int chunkLength = checked(length + 12);
                if (length < 0 || offset + chunkLength > encoded.Length)
                {
                    throw new InvalidOperationException("Generated PNG contains an invalid chunk length.");
                }

                string type = Encoding.ASCII.GetString(encoded, offset + 4, 4);
                bool keep =
                    String.Equals(type, "IHDR", StringComparison.Ordinal)
                    || String.Equals(type, "IDAT", StringComparison.Ordinal)
                    || String.Equals(type, "IEND", StringComparison.Ordinal);
                if (keep)
                {
                    normalized.Write(encoded, offset, chunkLength);
                }

                if (String.Equals(type, "IHDR", StringComparison.Ordinal))
                {
                    if (length != 13 || encoded[offset + 16] != 8 || encoded[offset + 17] != 6)
                    {
                        throw new InvalidOperationException(
                            "Generated PNG must use 8-bit RGBA color.");
                    }
                    sawHeader = true;
                }
                else if (String.Equals(type, "IDAT", StringComparison.Ordinal))
                {
                    sawImageData = true;
                }
                else if (String.Equals(type, "IEND", StringComparison.Ordinal))
                {
                    sawEnd = true;
                    break;
                }

                offset += chunkLength;
            }

            if (!sawHeader || !sawImageData || !sawEnd)
            {
                throw new InvalidOperationException(
                    "Generated PNG is missing IHDR, IDAT, or IEND.");
            }
            return normalized.ToArray();
        }
    }

    private static bool PointInPolygon(int x, int y, Point[] polygon)
    {
        bool inside = false;
        int previous = polygon.Length - 1;
        for (int current = 0; current < polygon.Length; current++)
        {
            int currentX = polygon[current].X;
            int currentY = polygon[current].Y;
            int previousX = polygon[previous].X;
            int previousY = polygon[previous].Y;
            bool crosses = ((currentY > y) != (previousY > y))
                && (x < (double)(previousX - currentX) * (y - currentY)
                    / (double)(previousY - currentY) + currentX);
            if (crosses)
            {
                inside = !inside;
            }
            previous = current;
        }
        return inside;
    }
}
'@

Add-Type -TypeDefinition $compilerSource -ReferencedAssemblies System.Drawing
if ($SelfTestRightShoeValidation) {
    $rejection = [DeterministicAvatarCompiler]::SelfTestClippedRightShoeValidation()
    Write-Output "AVATAR_VALIDATION_SELF_TEST=$rejection"
    exit 0
}

$results = [DeterministicAvatarCompiler]::Compile(
    $SourcePath,
    $OutputDirectory,
    [bool] $WriteFiles
)
$rightShoeValidation = [DeterministicAvatarCompiler]::LastRightShoeValidationStats

$payload = @{
    sourceHash = $actualHash
    width = 896
    height = 1195
    floorReflectionMask = 'source-space polygon'
    rightShoeValidation = @{
        pixelsBelowFloorBoundary = $rightShoeValidation.PixelsBelowFloorBoundary
        pixelsBelowContour = $rightShoeValidation.PixelsBelowContour
        pixelsOutsideLowerCorridor = $rightShoeValidation.PixelsOutsideLowerCorridor
        ankleOverlapPixels = $rightShoeValidation.AnkleOverlapPixels
        darkOpaquePixels = $rightShoeValidation.DarkOpaquePixels
        minimumVisibleY = $rightShoeValidation.MinimumVisibleY
        maximumVisibleY = $rightShoeValidation.MaximumVisibleY
    }
    files = @($results | ForEach-Object {
        @{
            name = $_.Name
            sha256 = $_.Sha256
            opaquePixels = $_.OpaquePixels
            darkOpaquePixels = $_.DarkOpaquePixels
            minimumVisibleY = $_.MinimumVisibleY
            maximumVisibleY = $_.MaximumVisibleY
        }
    })
}

Write-Output "AVATAR_RESULT_JSON=$($payload | ConvertTo-Json -Compress -Depth 5)"

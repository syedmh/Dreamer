"""Read-only release validation for TCFAnimation assets and export hygiene."""

from __future__ import annotations

import ast
import hashlib
import json
import math
import re
import struct
import sys
from dataclasses import dataclass
from enum import Enum
from pathlib import Path

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parents[1]
FRAME_EXTRACTION = ROOT / "FrameExtraction"
SUPPORT_PACKAGES = ROOT / ".tools" / "python"
for package_path in (FRAME_EXTRACTION, SUPPORT_PACKAGES):
    if package_path.is_dir():
        sys.path.insert(0, str(package_path))

import cv2  # type: ignore  # noqa: E402
import numpy as np  # type: ignore  # noqa: E402
from PIL import Image  # type: ignore  # noqa: E402

import extract_clap  # noqa: E402
import extract_cross_arm  # noqa: E402
import extract_directional_turns  # noqa: E402
import extract_right_walk  # noqa: E402
from file_integrity import sha256_file  # noqa: E402


CANVAS_SIZE = (512, 864)
SOURCE_CONTRACTS = {
    "LTurning.png": (
        "9EDD38F303B17CD043EDCCABF2E6C2BC50F182B9A2B918B4BDECF1B2861E3A91",
        1502641,
        "PNG",
        "RGBA",
        (1086, 1448),
    ),
    "RTurning.png": (
        "2D20B97B4BC630DBFF9D6DD932F3314A9BC6FE0587013E6C12E72BF1D40D5840",
        1390487,
        "PNG",
        "RGB",
        (1086, 1448),
    ),
    "RWalking2.png": (
        "CD56287A4830D068292793256DBEB5A29E1EB9D888520A5339FE3957E7B7FA3A",
        495754,
        "JPEG",
        "RGB",
        (1536, 1024),
    ),
    "Clapping2.png": (
        "FBB46FBEAD0D5815F4E23307240535C600C29D0DF650B493137D05E762319C00",
        654450,
        "JPEG",
        "RGB",
        (1086, 1448),
    ),
    "CrossArm3.png": (
        "276413B76F13D4940FD8B746D3AEA13D27922A47EACD750DCCC6FF622A6A8192",
        628721,
        "JPEG",
        "RGB",
        (1086, 1448),
    ),
    "CrossArm4.png": (
        "475614A7B2DB0B7469FA88E9B7B5F5C8548F8095B56DAD99F6270098E9174A3F",
        632179,
        "JPEG",
        "RGB",
        (1086, 1448),
    ),
    "WalkingGirlFrames.png": (
        "524F6F3CA6F55B5062433766734BF304D0285E5994B54A9AD12915C4FBF4D881",
        2_120_344,
        "PNG",
        "RGB",
        (1942, 809),
    ),
    "School1.png": (
        "2FED5C0AD5D5927BF22272634F2E879703436291962A46CBB5CB96C4434A986A",
        1186592,
        "JPEG",
        "RGB",
        (1908, 824),
    ),
    "School2.png": (
        "81468B2FA3E392F0EDDBC6F4FA9C5A961597C99F83ACA7669EAAC9938CC00A4C",
        1156503,
        "JPEG",
        "RGB",
        (1536, 1024),
    ),
    "School3.png": (
        "D231C7EC01DC340313DF1F3E261F5EDDD138118F80DDA935E3599A1895212727",
        1208533,
        "JPEG",
        "RGB",
        (1540, 1021),
    ),
    "School4.png": (
        "036F8ACC3084CF2FFEF366E0E939F7802F082E2D21A4A7A236C5488FAB7C7190",
        1182762,
        "JPEG",
        "RGB",
        (1540, 1021),
    ),
    "School5.png": (
        "5F6CE380A5B9198B7D5B18AAF91258E9EDC57AFD78B11931E9E9ADE4B9AB7AB1",
        1138687,
        "JPEG",
        "RGB",
        (1540, 1021),
    ),
    "School6.png": (
        "5F6CE380A5B9198B7D5B18AAF91258E9EDC57AFD78B11931E9E9ADE4B9AB7AB1",
        1138687,
        "JPEG",
        "RGB",
        (1540, 1021),
    ),
}

FRAME_GROUPS = {
    "LeftTurn": tuple(f"turn_{index}.png" for index in range(3)),
    "RightTurn": tuple(f"turn_{index}.png" for index in range(3)),
    "LeftWalk": tuple(f"walk_{index:02d}.png" for index in range(6)),
    "RightWalk": tuple(f"walk_{index:02d}.png" for index in range(6)),
    "Clap": tuple(f"clap_{index:02d}.png" for index in range(6)),
    "CrossArm": tuple(f"cross_{index:02d}.png" for index in range(3)),
    "CrossArmRelease": tuple(
        f"release_{index:02d}.png" for index in range(6)
    ),
}

EXPECTED_FRAME_PATHS = tuple(
    f"Frames/{directory}/{name}"
    for directory, names in FRAME_GROUPS.items()
    for name in names
)
GIRL_SOURCE_VALID_Y = (0, 516)
GIRL_SOURCE_X_INTERVALS = (
    (0, 158),
    (160, 317),
    (320, 480),
    (483, 642),
    (645, 805),
    (808, 967),
    (970, 1130),
    (1133, 1294),
    (1297, 1457),
    (1460, 1619),
    (1621, 1780),
    (1783, 1942),
)
GIRL_FRAME_CONTRACTS = {
    "Frames/GirlWalk/girl_00.png": (
        "AF0EDE5A0081F4D11D38024B34554AB23391424AACC47B837C7DBD12D3974572",
        196_886,
        (136, 59, 359, 840),
    ),
    "Frames/GirlWalk/girl_01.png": (
        "4F0D8F81BADABA85E1E3527BB8BEEE0EE98694590F333EF3D7B97E6A18E30D31",
        228_147,
        (114, 61, 353, 840),
    ),
    "Frames/GirlWalk/girl_02.png": (
        "108DB273CA4E3B956CFBB4E4BD2F7BF4C7930D0C8FF41BD4B2E0F3649358A229",
        225_464,
        (120, 59, 391, 840),
    ),
    "Frames/GirlWalk/girl_03.png": (
        "4A535BBCDCF1EFF88F6803C57BCF6EF7EDB4E03157E0A27DBC7E5EB169CF17A1",
        231_870,
        (120, 61, 389, 840),
    ),
    "Frames/GirlWalk/girl_04.png": (
        "BD5F4B9CFF5CC3D5D2EA021334CFA87318CE4F060A446769FBCD60BF09DC034C",
        238_119,
        (135, 59, 406, 840),
    ),
    "Frames/GirlWalk/girl_05.png": (
        "0D22F60047F5ADCC6730AE740C86D70FC97A08D74CF1C2F7E63203FD530CF269",
        231_070,
        (123, 57, 355, 840),
    ),
    "Frames/GirlWalk/girl_06.png": (
        "67EE8E54578B61B7313545F63DACCED810D3204A2698C0E94C40CB477F1D568F",
        234_046,
        (127, 57, 398, 840),
    ),
    "Frames/GirlWalk/girl_07.png": (
        "38C43EE47DD120033FEFA3284B42FF13EEEFF02E7311707F34A506532705C2A1",
        236_638,
        (120, 62, 393, 840),
    ),
    "Frames/GirlWalk/girl_08.png": (
        "B17BACA436B3B9B63B492BD09E80641639C55677729A1CCC296D8868FE86A2C7",
        234_929,
        (108, 61, 379, 840),
    ),
    "Frames/GirlWalk/girl_09.png": (
        "B0ECBF290A5DDF18C5FC85C123D7966D2A50AAEE17565DBDCF7EC5110D2CAD5E",
        224_746,
        (113, 59, 382, 840),
    ),
    "Frames/GirlWalk/girl_10.png": (
        "5D1C152EC8742585BE21C33F414808F6E0F012D46DADDCF8A6F6819D9B88B0ED",
        236_507,
        (109, 57, 378, 840),
    ),
    "Frames/GirlWalk/girl_11.png": (
        "64EBB8C977AD08BD247D65644DC024DD152BD7F732D2D8D20EB487B4416AA53C",
        201_451,
        (119, 59, 368, 840),
    ),
}
EXPECTED_GIRL_FRAME_PATHS = tuple(GIRL_FRAME_CONTRACTS)
GIRL_ALPHA_UNION_BOUNDS = (108, 57, 406, 840)
GIRL_TORSO_TARGET_X = 256.0
GIRL_MAXIMUM_TORSO_DRIFT = 5.0
GIRL_EXPECTED_TORSO_CENTERS = (
    255.5,
    252.0,
    256.5,
    256.0,
    257.5,
    256.5,
    256.5,
    256.0,
    255.5,
    257.0,
    257.0,
    257.0,
)
GIRL_TORSO_METRIC_TOLERANCE = 0.01
GIRL_TARGET_BASELINE_Y = 840
GIRL_MAXIMUM_BASELINE_DRIFT = 0
GIRL_MINIMUM_ALPHA_PIXELS = 70_000
GIRL_MAXIMUM_ALPHA_PIXELS = 180_000
GIRL_MINIMUM_ADJACENT_CHANGED_PIXELS = 12_000
GIRL_MAXIMUM_GREEN_PIXELS = 1_000
GIRL_MAXIMUM_GREEN_COMPONENT = 256
EXPECTED_SCHOOL_CHARACTER_PATHS = tuple(
    f"Frames/SchoolCharacter/{directory}/{name}"
    for directory, names in FRAME_GROUPS.items()
    for name in names
)
EXPECTED_BACKGROUNDS = {
    "Frames/Backgrounds/school1.png": (
        "FE0BB92D1BFE0172678CD317D0A5A625EE6033EF08005CDA91B3E14A7FDA2D58",
        3_073_605,
        (1908, 824),
        "School1.png",
    ),
    "Frames/Backgrounds/school2.png": (
        "3D721C35E45BED2FF21AE3FDC7EF2E0763901ECD9FA6D6BE6EE7942EC8AE5AE8",
        3_007_673,
        (1536, 1024),
        "School2.png",
    ),
    "Frames/Backgrounds/school3.png": (
        "91EF24E48C3AB3AAEC16FB29F5F4EC616EB655608CAAAADDE1EBDB5C14DCFAEA",
        3_091_284,
        (1540, 1021),
        "School3.png",
    ),
    "Frames/Backgrounds/school4.png": (
        "8B1A166BD1893221D6BB8B66EABBE5B66F46CFCCFB1748AB34EC10C839ECBCED",
        3_050_404,
        (1540, 1021),
        "School4.png",
    ),
    "Frames/Backgrounds/school5.png": (
        "115C59AE61FA2CBA75F89BEBC6BC968DB13A70EDEF52624E97F9F61FB3A892BE",
        2_964_932,
        (1540, 1021),
        "School5.png",
    ),
    "Frames/Backgrounds/school6.png": (
        "115C59AE61FA2CBA75F89BEBC6BC968DB13A70EDEF52624E97F9F61FB3A892BE",
        2_964_932,
        (1540, 1021),
        "School6.png",
    ),
}
EXPECTED_BACKGROUND_PATHS = tuple(EXPECTED_BACKGROUNDS)
EXPECTED_EFFECT_PATHS = (
    "Frames/Effects/logo-small.png",
    "Frames/Effects/tcf-neon-background.png",
)
EXPECTED_TEXTURE_PATHS = (
    *EXPECTED_FRAME_PATHS,
    *EXPECTED_GIRL_FRAME_PATHS,
    *EXPECTED_BACKGROUND_PATHS,
    *EXPECTED_EFFECT_PATHS,
    *EXPECTED_SCHOOL_CHARACTER_PATHS,
)
EXPECTED_EXPORT_RESOURCES = (
    "res://Main.tscn",
    "res://AnimationConfig.json",
    "res://ActionMessages.json",
    *(f"res://{path}" for path in EXPECTED_TEXTURE_PATHS),
)
EXPECTED_STAGE_NON_TEXTURE_ENTRIES = (
    "project.godot",
    "Main.tscn",
    "TCFAnimation.csproj",
    "export_presets.cfg",
    "AnimationConfig.json",
    "ActionMessages.json",
    "AnimationConfig.cs",
    "ActionLegendLayout.cs",
    "ActionLegendUi.cs",
    "ActionMessageCatalog.cs",
    "AnimationGeometry.cs",
    "AvatarPresenceStateMachine.cs",
    "CapturePathPolicy.cs",
    "CelebrationStateMachine.cs",
    "DialogueModel.cs",
    "DialogueUi.cs",
    "DirectionalTurnStateMachine.cs",
    "FireworksLayer.cs",
    "FireworksSimulation.cs",
    "GirlEntranceStateMachine.cs",
    "GlobalInputPolicy.cs",
    "LogoRainLayer.cs",
    "LogoRainSimulation.cs",
    "NeonLogoBackground.cs",
    "PresentationInputPolicy.cs",
    "SchoolSceneStateMachine.cs",
    "TurnController.cs",
    "AnimationConfig.cs.uid",
    "ActionLegendLayout.cs.uid",
    "ActionLegendUi.cs.uid",
    "ActionMessageCatalog.cs.uid",
    "AnimationGeometry.cs.uid",
    "AvatarPresenceStateMachine.cs.uid",
    "CapturePathPolicy.cs.uid",
    "CelebrationStateMachine.cs.uid",
    "DialogueModel.cs.uid",
    "DialogueUi.cs.uid",
    "DirectionalTurnStateMachine.cs.uid",
    "FireworksLayer.cs.uid",
    "FireworksSimulation.cs.uid",
    "GirlEntranceStateMachine.cs.uid",
    "LogoRainLayer.cs.uid",
    "LogoRainSimulation.cs.uid",
    "NeonLogoBackground.cs.uid",
    "PresentationInputPolicy.cs.uid",
    "SchoolSceneStateMachine.cs.uid",
    "TurnController.cs.uid",
)
EXPECTED_STAGE_ENTRIES = (
    *EXPECTED_STAGE_NON_TEXTURE_ENTRIES,
    *EXPECTED_FRAME_PATHS,
    *EXPECTED_GIRL_FRAME_PATHS,
    *EXPECTED_BACKGROUND_PATHS,
    EXPECTED_EFFECT_PATHS[0],
    EXPECTED_EFFECT_PATHS[1],
    f"{EXPECTED_EFFECT_PATHS[1]}.import",
    *EXPECTED_SCHOOL_CHARACTER_PATHS,
)
EXPECTED_CHARACTER_TEXTURE_COUNT = (
    len(EXPECTED_FRAME_PATHS)
    + len(EXPECTED_GIRL_FRAME_PATHS)
    + len(EXPECTED_SCHOOL_CHARACTER_PATHS)
)
EXPECTED_SCRIPT_PAYLOADS = (
    "DialogueUi.cs",
    "TurnController.cs",
)
APPROVED_REQUIREMENTS_INDEX = (
    "https://packagefeedproxy.microsoft.io/pypi/simple/"
)
EXPECTED_REQUIREMENTS = {
    "numpy": (
        "2.5.2",
        "85aaccb24182c25df891ad0ec333585967e115269d5f1b17f2c9ae005bc96657",
    ),
    "opencv-python": (
        "5.0.0.93",
        "f90ba04b8f73bc5c3814037699739f0156f597338a98f05956c684e7c3ca10d2",
    ),
    "Pillow": (
        "12.3.0",
        "1cca606cd25738df4ed873d5ad46bbdb3d83b5cbca291f6b4ff13a4df6b0bbe8",
    ),
}
PCK_MAGIC = 0x43504447
PCK_DIRECTORY_ENCRYPTED = 1 << 0
MAXIMUM_METADATA_ENTRY_SIZE = 4 * 1024 * 1024
MAXIMUM_METADATA_AGGREGATE_SIZE = 16 * 1024 * 1024
ARTIFICIAL_CHARCOAL_VALUE = 22
CHARCOAL_SPIKE_RATIO = 2.0
CHARCOAL_SPIKE_MINIMUM = 100
STATIONARY_PLATE_SEAM_Y = 640
STATIONARY_BLEND_BAND_TOP_Y = 600
STATIONARY_HEAD_LIMIT = 4.0
STATIONARY_TORSO_LIMIT = 4.0
STATIONARY_BASELINE_LIMIT = 2.0
STATIONARY_SILHOUETTE_LIMIT = 0.12
WALK_HEAD_LIMIT = 6.0
WALK_TORSO_LIMIT = 14.0
WALK_LANDMARK_LIMIT = 10.0
WALK_BASELINE_LIMIT = 16.0
WALK_HEIGHT_LIMIT = 18.0


class BoundaryKind(Enum):
    STATIONARY = "stationary"
    TURNING = "turning"
    WALKING = "walking"


@dataclass(frozen=True)
class ContinuityBoundary:
    label: str
    from_path: str
    to_path: str
    kind: BoundaryKind
DARK_SHOE_MAXIMUM_VALUE = 96
Rect = tuple[int, int, int, int]


@dataclass(frozen=True)
class PackEntry:
    data_offset: int
    size: int


@dataclass(frozen=True)
class LowerAnatomyReference:
    """Validator-owned anchors and negatives from accepted source geometry."""

    positive_anchors: tuple[Rect, ...]
    sole_envelopes: tuple[Rect, ...]
    floor_negative_rects: tuple[Rect, ...]
    garment_anchors: tuple[Rect, ...] = ()
    dark_shoe_anchors: tuple[Rect, ...] = ()


LOWER_ANATOMY_REFERENCES = {
    "Frames/LeftTurn/turn_0.png": LowerAnatomyReference(
        ((213, 778, 218, 783), (311, 808, 316, 813)),
        ((166, 699, 240, 845), (277, 699, 349, 845)),
        (
            (0, 844, 512, 864),
            (185, 764, 196, 791),
            (242, 730, 275, 844),
        ),
    ),
    "Frames/LeftTurn/turn_1.png": LowerAnatomyReference(
        ((226, 780, 231, 785), (305, 816, 310, 821)),
        ((164, 699, 257, 845), (255, 699, 332, 846)),
        ((0, 845, 512, 864), (191, 820, 258, 845)),
    ),
    "Frames/LeftTurn/turn_2.png": LowerAnatomyReference(
        ((240, 815, 245, 820), (270, 773, 275, 778)),
        ((208, 699, 259, 836), (257, 699, 291, 836)),
        ((0, 835, 512, 864), (212, 808, 223, 812)),
    ),
    "Frames/RightTurn/turn_0.png": LowerAnatomyReference(
        ((217, 805, 222, 810), (310, 778, 315, 783)),
        ((167, 699, 254, 844), (281, 699, 347, 842)),
        (
            (0, 841, 512, 864),
            (215, 838, 221, 841),
            (247, 766, 253, 775),
            (256, 730, 279, 840),
        ),
    ),
    "Frames/RightTurn/turn_1.png": LowerAnatomyReference(
        ((208, 779, 213, 784), (291, 777, 296, 782)),
        ((182, 699, 242, 849), (267, 699, 364, 834)),
        ((0, 848, 512, 864), (244, 730, 265, 833)),
        (
            (179, 576, 184, 581),
            (180, 630, 185, 635),
            (185, 670, 190, 675),
        ),
    ),
    "Frames/RightTurn/turn_2.png": LowerAnatomyReference(
        ((235, 804, 240, 809), (279, 804, 284, 809)),
        ((210, 699, 259, 840), (257, 699, 330, 843)),
        ((0, 836, 512, 864), (215, 836, 329, 843)),
    ),
    "Frames/RightWalk/walk_00.png": LowerAnatomyReference(
        ((152, 778, 157, 783), (348, 785, 353, 790)),
        ((117, 699, 221, 834), (294, 699, 439, 833)),
        ((0, 833, 512, 864), (223, 730, 292, 832)),
    ),
    "Frames/RightWalk/walk_01.png": LowerAnatomyReference(
        ((136, 773, 141, 778), (333, 778, 338, 783)),
        ((104, 699, 211, 834), (289, 699, 427, 830)),
        ((0, 833, 512, 864), (213, 730, 287, 829)),
    ),
    "Frames/RightWalk/walk_02.png": LowerAnatomyReference(
        ((133, 766, 138, 771), (291, 780, 296, 785)),
        ((109, 699, 196, 811), (259, 699, 378, 839)),
        ((0, 838, 512, 864), (199, 730, 256, 809)),
    ),
    "Frames/RightWalk/walk_03.png": LowerAnatomyReference(
        ((179, 770, 184, 775), (274, 803, 279, 808)),
        ((149, 699, 257, 824), (255, 699, 347, 839)),
        ((0, 838, 512, 864),),
    ),
    "Frames/RightWalk/walk_04.png": LowerAnatomyReference(
        ((202, 779, 207, 784), (275, 767, 280, 772)),
        ((177, 699, 257, 828), (255, 699, 316, 806)),
        ((0, 827, 512, 864),),
    ),
    "Frames/RightWalk/walk_05.png": LowerAnatomyReference(
        ((183, 796, 188, 801), (323, 776, 328, 781)),
        ((154, 699, 231, 836), (289, 699, 386, 827)),
        ((0, 835, 512, 864), (233, 730, 287, 826)),
    ),
    "Frames/Clap/clap_00.png": LowerAnatomyReference(
        ((220, 796, 225, 801), (307, 776, 312, 781)),
        ((169, 699, 250, 845), (283, 699, 346, 845)),
        (
            (0, 843, 512, 864),
            (217, 835, 245, 843),
            (252, 730, 281, 842),
        ),
        dark_shoe_anchors=((206, 790, 209, 793),),
    ),
    "Frames/Clap/clap_01.png": LowerAnatomyReference(
        ((220, 792, 225, 797), (309, 778, 314, 783)),
        ((168, 699, 246, 845), (284, 699, 346, 845)),
        (
            (0, 843, 512, 864),
            (217, 835, 245, 843),
            (248, 730, 282, 842),
        ),
    ),
    "Frames/Clap/clap_02.png": LowerAnatomyReference(
        ((214, 806, 219, 811), (310, 778, 315, 783)),
        ((172, 699, 245, 845), (284, 699, 346, 845)),
        (
            (0, 843, 512, 864),
            (217, 835, 245, 843),
            (247, 730, 282, 842),
        ),
    ),
    "Frames/Clap/clap_03.png": LowerAnatomyReference(
        ((220, 776, 225, 781), (306, 774, 311, 779)),
        ((167, 699, 245, 845), (285, 699, 347, 845)),
        (
            (0, 843, 512, 864),
            (217, 835, 245, 843),
            (247, 730, 283, 842),
        ),
    ),
    "Frames/Clap/clap_04.png": LowerAnatomyReference(
        ((222, 775, 227, 780), (308, 775, 313, 780)),
        ((167, 699, 249, 845), (287, 699, 346, 845)),
        (
            (0, 843, 512, 864),
            (217, 835, 245, 843),
            (251, 730, 285, 842),
        ),
    ),
    "Frames/Clap/clap_05.png": LowerAnatomyReference(
        ((220, 779, 225, 784), (311, 777, 316, 782)),
        ((167, 699, 246, 845), (285, 699, 347, 845)),
        (
            (0, 843, 512, 864),
            (217, 835, 245, 843),
            (248, 730, 283, 842),
        ),
    ),
    "Frames/CrossArm/cross_00.png": LowerAnatomyReference(
        ((203, 819, 208, 824), (314, 778, 319, 783)),
        ((178, 699, 247, 843), (285, 699, 345, 845)),
        (
            (0, 843, 512, 864),
            (210, 835, 245, 843),
            (249, 730, 283, 841),
        ),
    ),
    "Frames/CrossArm/cross_01.png": LowerAnatomyReference(
        ((227, 776, 232, 781), (316, 779, 321, 784)),
        ((180, 699, 250, 845), (287, 699, 345, 845)),
        (
            (0, 843, 512, 864),
            (210, 835, 245, 843),
            (252, 730, 285, 842),
        ),
    ),
    "Frames/CrossArm/cross_02.png": LowerAnatomyReference(
        ((226, 776, 231, 781), (316, 778, 321, 783)),
        ((178, 699, 249, 845), (287, 699, 345, 845)),
        ((0, 844, 512, 864), (251, 730, 285, 843)),
    ),
    "Frames/CrossArmRelease/release_00.png": LowerAnatomyReference(
        ((203, 774, 208, 779), (292, 774, 297, 779)),
        ((166, 699, 229, 834), (267, 699, 328, 845)),
        ((0, 844, 512, 864), (231, 730, 265, 832)),
    ),
    "Frames/CrossArmRelease/release_01.png": LowerAnatomyReference(
        ((212, 775, 217, 780), (303, 776, 308, 781)),
        ((166, 699, 237, 833), (275, 699, 332, 845)),
        ((0, 844, 512, 864), (239, 730, 273, 831)),
    ),
    "Frames/CrossArmRelease/release_02.png": LowerAnatomyReference(
        ((208, 794, 213, 799), (309, 775, 314, 780)),
        ((166, 699, 243, 834), (282, 699, 331, 845)),
        ((0, 844, 512, 864), (245, 730, 280, 832)),
    ),
    "Frames/CrossArmRelease/release_03.png": LowerAnatomyReference(
        ((210, 789, 215, 794), (301, 775, 306, 780)),
        ((163, 699, 235, 834), (275, 699, 334, 845)),
        ((0, 844, 512, 864), (237, 730, 273, 832)),
    ),
    "Frames/CrossArmRelease/release_04.png": LowerAnatomyReference(
        ((210, 778, 215, 783), (308, 816, 313, 821)),
        ((164, 699, 235, 832), (274, 699, 334, 845)),
        ((0, 844, 512, 864), (237, 730, 272, 830)),
    ),
    "Frames/CrossArmRelease/release_05.png": LowerAnatomyReference(
        ((211, 775, 216, 780), (312, 820, 317, 825)),
        ((163, 699, 236, 834), (276, 699, 334, 845)),
        ((0, 844, 512, 864), (238, 730, 274, 832)),
    ),
}


def validator_rect_mask(shape: tuple[int, int], rect: Rect) -> np.ndarray:
    height, width = shape
    x0, y0, x1, y1 = rect
    if not (0 <= x0 < x1 <= width and 0 <= y0 < y1 <= height):
        raise RuntimeError(
            f"Invalid validator rectangle {rect} for {width}x{height}."
        )
    mask = np.zeros(shape, dtype=bool)
    mask[y0:y1, x0:x1] = True
    return mask


def mirror_rect(rect: Rect) -> Rect:
    x0, y0, x1, y1 = rect
    return (CANVAS_SIZE[0] - x1, y0, CANVAS_SIZE[0] - x0, y1)


def lower_reference_for(relative_path: str) -> LowerAnatomyReference:
    if (
        relative_path == "Frames/LeftTurn/turn_0.png"
        or relative_path == "Frames/CrossArm/cross_02.png"
        or relative_path.startswith("Frames/CrossArmRelease/")
    ):
        return LOWER_ANATOMY_REFERENCES["Frames/RightTurn/turn_0.png"]
    reference = LOWER_ANATOMY_REFERENCES.get(relative_path)
    if reference is not None:
        return reference
    if relative_path.startswith("Frames/LeftWalk/"):
        right_path = relative_path.replace(
            "Frames/LeftWalk/",
            "Frames/RightWalk/",
            1,
        )
        right = LOWER_ANATOMY_REFERENCES[right_path]
        return LowerAnatomyReference(
            tuple(mirror_rect(rect) for rect in right.positive_anchors),
            tuple(mirror_rect(rect) for rect in right.sole_envelopes),
            tuple(mirror_rect(rect) for rect in right.floor_negative_rects),
            tuple(mirror_rect(rect) for rect in right.garment_anchors),
            tuple(mirror_rect(rect) for rect in right.dark_shoe_anchors),
        )
    raise RuntimeError(
        f"{relative_path} has no independent lower-anatomy reference."
    )


def validator_component_gap(
    anchor: np.ndarray,
    component: np.ndarray,
) -> float:
    if np.any(anchor & component):
        return 0.0
    distance = cv2.distanceTransform(
        np.where(anchor, 0, 1).astype(np.uint8),
        cv2.DIST_L2,
        3,
    )
    return float(np.min(distance[component]))


def validate_extractor_segmentation_contract() -> None:
    source = (FRAME_EXTRACTION / "foreground_cutout.py").read_text(
        encoding="utf-8"
    )
    forbidden_fragments = (
        "definite_foreground |=",
        "probable_foreground |=",
        "localized = cv2.morphologyEx",
        "provenance",
        "_source_anatomy_reference",
        "admission_reference",
    )
    present = [
        fragment
        for fragment in forbidden_fragments
        if fragment in source
    ]
    if present:
        raise RuntimeError(
            "Production segmentation still contains additive/circular lower "
            f"support paths: {present}."
        )
    required_fragments = (
        "def _segment_local_anatomy(",
        "attachment_seed",
        "def project_source_exclusion_mask(",
        "source_exclusion_mask",
        "grabcut_foreground |= segmentation.astype(np.uint8)",
    )
    missing = [
        fragment
        for fragment in required_fragments
        if fragment not in source
    ]
    if missing:
        raise RuntimeError(
            "Production segmentation is missing the independently seeded "
            f"lower-anatomy path: {missing}."
        )
    print(
        "extractor_contract_ok=main_grabcut_lower_support_non_additive "
        "local_segmentation=seeded attachment=bounded "
        "source_exclusions=precomposition provenance=omitted"
    )


def validate_independent_lower_frame(
    relative_path: str,
    frame: np.ndarray,
    matte: np.ndarray | None,
) -> None:
    reference = lower_reference_for(relative_path)
    rgb = frame[:, :, :3]
    visible = np.max(rgb, axis=2) > 0

    required_anchor_mask = np.zeros(visible.shape, dtype=bool)
    for anchor in (*reference.positive_anchors, *reference.garment_anchors):
        anchor_mask = validator_rect_mask(visible.shape, anchor)
        required_anchor_mask |= anchor_mask
        if not np.all(visible[anchor_mask]):
            missing = int(np.count_nonzero(~visible[anchor_mask]))
            raise RuntimeError(
                f"{relative_path} misses {missing} independently calibrated "
                f"anatomy pixels in anchor {anchor}."
            )

    for anchor in reference.dark_shoe_anchors:
        anchor_mask = validator_rect_mask(visible.shape, anchor)
        required_anchor_mask |= anchor_mask
        if not np.all(visible[anchor_mask]):
            missing = int(np.count_nonzero(~visible[anchor_mask]))
            raise RuntimeError(
                f"{relative_path} misses {missing} required dark-shoe "
                f"anchor pixels in {anchor}."
            )
        maximum = int(np.max(rgb[anchor_mask]))
        if maximum > DARK_SHOE_MAXIMUM_VALUE:
            raise RuntimeError(
                f"{relative_path} required dark-shoe anchor {anchor} has "
                f"value {maximum}; maximum is {DARK_SHOE_MAXIMUM_VALUE}."
            )

    for negative in reference.floor_negative_rects:
        negative_mask = validator_rect_mask(visible.shape, negative)
        if np.any(rgb[negative_mask]):
            count = int(np.count_nonzero(np.any(rgb != 0, axis=2) & negative_mask))
            raise RuntimeError(
                f"{relative_path} has {count} visible pixels in independent "
                f"floor-negative region {negative}."
            )
        if matte is not None and np.any(matte[negative_mask]):
            count = int(np.count_nonzero((matte != 0) & negative_mask))
            raise RuntimeError(
                f"{relative_path} has {count} matte pixels in independent "
                f"floor-negative region {negative}."
            )

    approved_lower = np.zeros(visible.shape, dtype=bool)
    for envelope in reference.sole_envelopes:
        approved_lower |= validator_rect_mask(visible.shape, envelope)
    rows = np.indices(visible.shape)[0]
    lower_band = rows >= 700
    visible_overrun = visible & lower_band & ~approved_lower
    if np.any(visible_overrun):
        ys, xs = np.where(visible_overrun)
        raise RuntimeError(
            f"{relative_path} visible lower pixels exceed validator sole "
            f"envelopes at x={int(xs.min())}..{int(xs.max())}, "
            f"y={int(ys.min())}..{int(ys.max())}."
        )
    if matte is not None:
        matte_overrun = (matte != 0) & lower_band & ~approved_lower
        if np.any(matte_overrun):
            ys, xs = np.where(matte_overrun)
            raise RuntimeError(
                f"{relative_path} regenerated matte exceeds validator sole "
                f"envelopes at x={int(xs.min())}..{int(xs.max())}, "
                f"y={int(ys.min())}..{int(ys.max())}."
            )

    lower_rgb = rgb[700:]
    lower_visible = visible[700:]
    colors, counts = np.unique(
        lower_rgb[lower_visible],
        axis=0,
        return_counts=True,
    )
    for color, count in zip(colors, counts, strict=True):
        if count < 12 or int(np.max(color)) > 64:
            continue
        exact = (
            np.all(rgb == color, axis=2)
            & visible
            & lower_band
            & approved_lower
        )
        component_count, component_labels, component_stats, _ = (
            cv2.connectedComponentsWithStats(
                exact.astype(np.uint8),
                8,
            )
        )
        for label in range(1, component_count):
            x, y, width, height, area = map(
                int,
                component_stats[label],
            )
            if area < 12 or width < 4 or height < 3:
                continue
            fill_ratio = area / (width * height)
            component = component_labels == label
            if (
                fill_ratio >= 0.75
                and not np.any(component & required_anchor_mask)
            ):
                raise RuntimeError(
                    f"{relative_path} has rectangular lower patch "
                    f"{(x, y, width, height, area)} color="
                    f"{tuple(map(int, color))} fill={fill_ratio:.3f}."
                )

    component_count, labels, stats, _ = cv2.connectedComponentsWithStats(
        visible.astype(np.uint8),
        8,
    )
    if component_count <= 1:
        raise RuntimeError(f"{relative_path} has no visible character.")
    primary_label = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    attached = labels == primary_label
    pending: list[tuple[int, np.ndarray]] = []
    for label in range(1, component_count):
        if label == primary_label:
            continue
        component = labels == label
        x, y, width, height, area = map(int, stats[label])
        if y >= 650 and width >= 16 and height <= 3:
            raise RuntimeError(
                f"{relative_path} has a narrow detached lower component "
                f"{(x, y, width, height, area)}."
            )
        if y + height < 650:
            raise RuntimeError(
                f"{relative_path} has a detached upper component "
                f"{(x, y, width, height, area)}."
            )
        if not np.any(component & approved_lower):
            raise RuntimeError(
                f"{relative_path} detached component "
                f"{(x, y, width, height, area)} has no approved anatomy."
            )
        if not np.any(component & required_anchor_mask):
            raise RuntimeError(
                f"{relative_path} detached lower component "
                f"{(x, y, width, height, area)} misses every required "
                "shoe anchor."
            )
        pending.append((label, component))

    changed = True
    while changed:
        changed = False
        remaining = []
        for label, component in pending:
            if validator_component_gap(attached, component) <= 20:
                attached |= component
                changed = True
            else:
                remaining.append((label, component))
        pending = remaining
    if pending:
        labels_left = [label for label, _ in pending]
        raise RuntimeError(
            f"{relative_path} has detached anatomy components beyond the "
            f"20px attachment limit: {labels_left}."
        )


def validate_independent_lower_anatomy(
    regenerated: dict[str, tuple[np.ndarray, np.ndarray]],
) -> None:
    if len(LOWER_ANATOMY_REFERENCES) != 27:
        raise RuntimeError(
            "Validator must contain exactly 27 unique calibrated pose records."
        )
    for relative_path in EXPECTED_FRAME_PATHS:
        frame, matte = regenerated[relative_path]
        validate_independent_lower_frame(relative_path, frame, matte)
        disk = cv2.imread(str(ROOT / relative_path), cv2.IMREAD_UNCHANGED)
        if disk is None:
            raise RuntimeError(f"Could not decode disk frame {relative_path}.")
        disk_visible_matte = (
            np.max(disk[:, :, :3], axis=2) > 0
        ).astype(np.uint8)
        validate_independent_lower_frame(
            relative_path,
            disk,
            disk_visible_matte,
        )

    negative_path = "Frames/Clap/clap_00.png"
    negative_frame = regenerated[negative_path][0].copy()
    negative_matte = regenerated[negative_path][1].copy()
    negative_frame[850, 20, :3] = (12, 12, 12)
    negative_matte[850, 20] = 1
    try:
        validate_independent_lower_frame(
            negative_path,
            negative_frame,
            negative_matte,
        )
    except RuntimeError as exception:
        if "floor-negative region" not in str(exception):
            raise RuntimeError(
                "Independent negative control failed for the wrong reason."
            ) from exception
        print(
            "negative_control_ok=synthetic_floor_pixel rejected=true "
            f"reason={exception}"
        )
    else:
        raise RuntimeError(
            "Independent lower-anatomy validator accepted a synthetic floor "
            "pixel."
        )

    rectangle_frame = regenerated[negative_path][0].copy()
    rectangle_matte = regenerated[negative_path][1].copy()
    rectangle_frame[828:834, 198:208, :3] = (12, 12, 12)
    rectangle_matte[828:834, 198:208] = 1
    try:
        validate_independent_lower_frame(
            negative_path,
            rectangle_frame,
            rectangle_matte,
        )
    except RuntimeError as exception:
        if "rectangular lower patch" not in str(exception):
            raise RuntimeError(
                "In-envelope rectangle control failed for the wrong reason."
            ) from exception
        print(
            "negative_control_ok=in_envelope_dark_rectangle rejected=true "
            f"reason={exception}"
        )
    else:
        raise RuntimeError(
            "Independent lower-anatomy validator accepted an in-envelope "
            "dark rectangle."
        )

    cool_frame = regenerated[negative_path][0].copy()
    cool_matte = regenerated[negative_path][1].copy()
    cool_frame[730:734, 180:185, :3] = (48, 18, 42)
    cool_matte[730:734, 180:185] = 1
    try:
        validate_independent_lower_frame(
            negative_path,
            cool_frame,
            cool_matte,
        )
    except RuntimeError as exception:
        if (
            "misses every required shoe anchor" not in str(exception)
            and "rectangular lower patch" not in str(exception)
        ):
            raise RuntimeError(
                "Detached cool-fragment control failed for the wrong reason."
            ) from exception
        print(
            "negative_control_ok=detached_cool_fragment rejected=true "
            f"reason={exception}"
        )
    else:
        raise RuntimeError(
            "Independent lower-anatomy validator accepted a detached cool "
            "fragment."
        )

    attached_path = "Frames/LeftTurn/turn_1.png"
    attached_frame = regenerated[attached_path][0].copy()
    attached_matte = regenerated[attached_path][1].copy()
    attached_frame[820:822, 195:207, :3] = (18, 18, 18)
    attached_matte[820:822, 195:207] = 1
    try:
        validate_independent_lower_frame(
            attached_path,
            attached_frame,
            attached_matte,
        )
    except RuntimeError as exception:
        if "floor-negative region" not in str(exception):
            raise RuntimeError(
                "Shoe-attached dark reflection control failed for the wrong "
                "reason."
            ) from exception
        print(
            "adversarial_control_ok=shoe_attached_dark_reflection_strip "
            "height_px=2 rejected=true reason=lower_silhouette_exclusion "
            f"detail={exception}"
        )
    else:
        raise RuntimeError(
            "Independent validator accepted a 2px shoe-attached dark "
            "reflection strip."
        )

    bridged_frame = regenerated[attached_path][0].copy()
    bridged_matte = regenerated[attached_path][1].copy()
    bridged_frame[820:824, 191:196, :3] = (48, 18, 42)
    bridged_matte[820:824, 191:196] = 1
    bridged_frame[820, 189:191, :3] = (48, 18, 42)
    bridged_matte[820, 189:191] = 1
    try:
        validate_independent_lower_frame(
            attached_path,
            bridged_frame,
            bridged_matte,
        )
    except RuntimeError as exception:
        if "floor-negative region" not in str(exception):
            raise RuntimeError(
                "One-pixel-bridged cool-fragment control failed for the "
                "wrong reason."
            ) from exception
        print(
            "adversarial_control_ok=one_pixel_bridged_cool_fragment "
            "bridge_height_px=1 rejected=true "
            "reason=lower_silhouette_exclusion "
            f"detail={exception}"
        )
    else:
        raise RuntimeError(
            "Independent validator accepted a one-pixel-bridged cool "
            "fragment."
        )

    dark_anchor_path = "Frames/Clap/clap_00.png"
    dark_anchor_frame = regenerated[dark_anchor_path][0].copy()
    dark_anchor_matte = regenerated[dark_anchor_path][1].copy()
    dark_anchor_frame[790:793, 206:209, :3] = 0
    dark_anchor_matte[790:793, 206:209] = 0
    try:
        validate_independent_lower_frame(
            dark_anchor_path,
            dark_anchor_frame,
            dark_anchor_matte,
        )
    except RuntimeError as exception:
        if "required dark-shoe anchor" not in str(exception):
            raise RuntimeError(
                "Dark-shoe anchor deletion control failed for the wrong "
                "reason."
            ) from exception
        print(
            "adversarial_control_ok=dark_shoe_anchor_deletion "
            f"rejected=true reason=lost_anatomy detail={exception}"
        )
    else:
        raise RuntimeError(
            "Independent validator accepted deletion of a required "
            "dark-shoe anchor."
        )

    garment_path = "Frames/RightTurn/turn_1.png"
    garment_frame = regenerated[garment_path][0].copy()
    garment_matte = regenerated[garment_path][1].copy()
    garment_frame[650:675, 168:190, :3] = 0
    garment_matte[650:675, 168:190] = 0
    try:
        validate_independent_lower_frame(
            garment_path,
            garment_frame,
            garment_matte,
        )
    except RuntimeError as exception:
        if "anatomy pixels in anchor" not in str(exception):
            raise RuntimeError(
                "Garment-tail negative control failed for the wrong reason."
            ) from exception
        print(
            "garment_negative_control_ok=distal_tail_truncation "
            f"rejected=true reason={exception}"
        )
    else:
        raise RuntimeError(
            "Independent lower-anatomy validator accepted a truncated "
            "RightTurn turn_1 garment tail."
        )

    print(
        "independent_lower_validation_ok=frames:33 unique_pose_records:27 "
        "disk_and_regenerated=true anchors=required floor_negatives=black "
        "sole_envelopes=bounded components=attached"
    )


def validate_sources() -> None:
    for name, contract in SOURCE_CONTRACTS.items():
        (
            expected_hash,
            expected_bytes,
            expected_format,
            expected_mode,
            expected_size,
        ) = contract
        path = ROOT / name
        actual_hash = sha256_file(path, expected_bytes)
        if actual_hash != expected_hash:
            raise RuntimeError(
                f"{name} SHA-256 is {actual_hash}; expected {expected_hash}."
            )
        with Image.open(path) as image:
            actual = (image.format, image.mode, image.size)
        expected = (expected_format, expected_mode, expected_size)
        if actual != expected:
            raise RuntimeError(f"{name} is {actual}; expected {expected}.")
        print(
            f"source_ok={name} format={expected_format} mode={expected_mode} "
            f"size={expected_size[0]}x{expected_size[1]} "
            f"bytes={expected_bytes} sha256={actual_hash}"
        )
        if name.startswith("School"):
            import_text = (ROOT / f"{name}.import").read_text(
                encoding="utf-8"
            )
            if import_text.replace("\r\n", "\n") != (
                '[remap]\n\nimporter="keep"\n'
            ):
                raise RuntimeError(
                    f"{name}.import must be keep-only metadata."
                )


def validate_walking_girl_source_geometry() -> None:
    if GIRL_SOURCE_VALID_Y != (0, 516):
        raise RuntimeError(
            "Walking-girl source y contract must remain exactly [0,516)."
        )
    if len(GIRL_SOURCE_X_INTERVALS) != 12:
        raise RuntimeError(
            "Walking-girl source contract must contain exactly 12 intervals."
        )

    source_width, source_height = SOURCE_CONTRACTS[
        "WalkingGirlFrames.png"
    ][4]
    y0, y1 = GIRL_SOURCE_VALID_Y
    if not (0 <= y0 < y1 <= source_height):
        raise RuntimeError(
            "Walking-girl source y contract exceeds the source dimensions."
        )

    previous_x1 = -1
    for index, (x0, x1) in enumerate(GIRL_SOURCE_X_INTERVALS):
        if not (0 <= x0 < x1 <= source_width):
            raise RuntimeError(
                f"Walking-girl interval {index} {(x0, x1)} is invalid."
            )
        if x0 <= previous_x1:
            raise RuntimeError(
                f"Walking-girl interval {index} overlaps its predecessor."
            )
        previous_x1 = x1
    if (
        GIRL_SOURCE_X_INTERVALS[0][0] != 0
        or GIRL_SOURCE_X_INTERVALS[-1][1] != source_width
    ):
        raise RuntimeError(
            "Walking-girl intervals must retain the frozen sheet endpoints."
        )
    print(
        "girl_source_geometry_ok=frames:12 "
        f"x_intervals={GIRL_SOURCE_X_INTERVALS} "
        "valid_source_y=[0,516) extractor_state_trusted=false"
    )


def validate_walking_girl_extractor_contract() -> None:
    path = FRAME_EXTRACTION / "extract_walking_girl.py"
    source = path.read_text(encoding="utf-8")
    tree = ast.parse(source, filename=str(path))
    assignments: dict[str, object] = {}
    for node in tree.body:
        if not isinstance(node, ast.Assign) or len(node.targets) != 1:
            continue
        target = node.targets[0]
        if not isinstance(target, ast.Name):
            continue
        try:
            assignments[target.id] = ast.literal_eval(node.value)
        except (ValueError, TypeError):
            continue

    expected_assignments = {
        "SOURCE_SHA256": SOURCE_CONTRACTS["WalkingGirlFrames.png"][0],
        "SOURCE_SIZE_BYTES": SOURCE_CONTRACTS["WalkingGirlFrames.png"][1],
        "SOURCE_FORMAT": SOURCE_CONTRACTS["WalkingGirlFrames.png"][2],
        "SOURCE_MODE": SOURCE_CONTRACTS["WalkingGirlFrames.png"][3],
        "SOURCE_SIZE": SOURCE_CONTRACTS["WalkingGirlFrames.png"][4],
        "VALID_SOURCE_Y": GIRL_SOURCE_VALID_Y,
        "X_INTERVALS": GIRL_SOURCE_X_INTERVALS,
        "FRAME_COUNT": len(EXPECTED_GIRL_FRAME_PATHS),
    }
    actual = {
        name: assignments.get(name)
        for name in expected_assignments
    }
    if actual != expected_assignments:
        raise RuntimeError(
            "Walking-girl extractor source/crop constants differ from the "
            f"independent validator contract: {actual}."
        )

    required_fragments = (
        'SOURCE_PATH = ROOT / "WalkingGirlFrames.png"',
        'OUTPUT_DIR = ROOT / "Frames" / "GirlWalk"',
        "crop_y0, crop_y1 = VALID_SOURCE_Y",
        "for index, (x0, x1) in enumerate(X_INTERVALS):",
        "panel = source[crop_y0:crop_y1, x0:x1].copy()",
    )
    missing = [
        fragment for fragment in required_fragments if fragment not in source
    ]
    if missing:
        raise RuntimeError(
            "Walking-girl extractor no longer applies the frozen crop "
            f"contract: {missing}."
        )
    print(
        "girl_extractor_contract_ok=source:WalkingGirlFrames.png "
        "output:Frames/GirlWalk frames:12 exact_crops=true "
        "valid_source_y=[0,516) validator_owned=true"
    )


def measure_girl_torso_x(pixels: np.ndarray) -> float:
    rgb = pixels[:, :, :3]
    alpha = pixels[:, :, 3]
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    hue, saturation, value = cv2.split(hsv)
    rows = np.indices(alpha.shape)[0]
    red = rgb[:, :, 0].astype(np.int16)
    blue = rgb[:, :, 2].astype(np.int16)
    garment = (
        (alpha > 0)
        & (hue >= 6)
        & (hue <= 32)
        & (saturation >= 28)
        & (value >= 45)
        & (red > blue * 1.15)
        & (rows >= 250)
        & (rows < 650)
    )
    centers = []
    for y in range(280, 600):
        xs = np.flatnonzero(garment[y])
        if xs.size >= 20:
            centers.append(
                (
                    float(np.percentile(xs, 10))
                    + float(np.percentile(xs, 90))
                )
                / 2.0
            )
    if not centers:
        raise RuntimeError(
            "Walking-girl frame has no independently measurable torso."
        )
    return float(np.median(centers))


def validate_girl_green_background(
    relative_path: str,
    pixels: np.ndarray,
) -> tuple[int, int]:
    alpha = pixels[:, :, 3]
    hsv = cv2.cvtColor(pixels[:, :, :3], cv2.COLOR_RGB2HSV)
    green = (
        (alpha == 255)
        & (hsv[:, :, 0] >= 30)
        & (hsv[:, :, 0] <= 110)
        & (hsv[:, :, 1] >= 70)
        & (hsv[:, :, 2] >= 30)
    )
    green[760:, :] = False
    count, labels, stats, _ = cv2.connectedComponentsWithStats(
        green.astype(np.uint8),
        8,
    )
    largest = max(
        (
            int(stats[label, cv2.CC_STAT_AREA])
            for label in range(1, count)
        ),
        default=0,
    )
    for label in range(1, count):
        x, y, width, height, area = map(int, stats[label])
        fill_ratio = area / (width * height)
        if (
            area >= 64
            and width >= 4
            and height >= 4
            and fill_ratio >= 0.80
        ):
            raise RuntimeError(
                f"{relative_path} retains an opaque green rectangle "
                f"{(x, y, width, height, area)} fill={fill_ratio:.3f}."
            )
    green_pixels = int(np.count_nonzero(green))
    if (
        green_pixels > GIRL_MAXIMUM_GREEN_PIXELS
        or largest > GIRL_MAXIMUM_GREEN_COMPONENT
    ):
        raise RuntimeError(
            f"{relative_path} retains green-screen contamination: "
            f"pixels={green_pixels}, largest_component={largest}."
        )
    return green_pixels, largest


def validate_walking_girl_frames() -> None:
    hashes = []
    bounds = []
    frames = []
    torso_centers = []
    baselines = []
    green_counts = []
    for index, (relative_path, contract) in enumerate(
        GIRL_FRAME_CONTRACTS.items()
    ):
        expected_hash, expected_bytes, expected_bounds = contract
        path = ROOT / relative_path
        actual_hash = sha256_file(path, expected_bytes)
        if actual_hash != expected_hash:
            raise RuntimeError(
                f"{relative_path} SHA-256 is {actual_hash}; "
                f"expected {expected_hash}."
            )
        with Image.open(path) as image:
            if (
                image.format != "PNG"
                or image.mode != "RGBA"
                or image.size != CANVAS_SIZE
            ):
                raise RuntimeError(
                    f"{relative_path} is {image.format} {image.mode} "
                    f"{image.size}; expected PNG RGBA {CANVAS_SIZE}."
                )
            pixels = np.asarray(image, dtype=np.uint8)

        alpha = pixels[:, :, 3]
        if (
            not np.any(alpha == 0)
            or not np.any(alpha == 255)
            or not np.any((alpha > 0) & (alpha < 255))
        ):
            raise RuntimeError(
                f"{relative_path} does not contain true RGBA transparency."
            )
        alpha_pixels = int(np.count_nonzero(alpha))
        if not (
            GIRL_MINIMUM_ALPHA_PIXELS
            <= alpha_pixels
            <= GIRL_MAXIMUM_ALPHA_PIXELS
        ):
            raise RuntimeError(
                f"{relative_path} alpha population {alpha_pixels} is outside "
                f"[{GIRL_MINIMUM_ALPHA_PIXELS},"
                f"{GIRL_MAXIMUM_ALPHA_PIXELS}]."
            )
        if np.any(pixels[:, :, :3][alpha == 0] != 0):
            raise RuntimeError(
                f"{relative_path} has nonzero RGB where alpha is zero."
            )
        alpha_y, alpha_x = np.where(alpha > 0)
        if alpha_x.size == 0:
            raise RuntimeError(f"{relative_path} has empty alpha.")
        measured_bounds = (
            int(alpha_x.min()),
            int(alpha_y.min()),
            int(alpha_x.max()),
            int(alpha_y.max()),
        )
        if measured_bounds != expected_bounds:
            raise RuntimeError(
                f"{relative_path} alpha bounds are {measured_bounds}; "
                f"expected {expected_bounds}."
            )

        torso_x = measure_girl_torso_x(pixels)
        if abs(torso_x - GIRL_TORSO_TARGET_X) > GIRL_MAXIMUM_TORSO_DRIFT:
            raise RuntimeError(
                f"{relative_path} torso center is {torso_x}; expected within "
                f"{GIRL_MAXIMUM_TORSO_DRIFT} of {GIRL_TORSO_TARGET_X}."
            )
        expected_torso_x = GIRL_EXPECTED_TORSO_CENTERS[index]
        if (
            abs(torso_x - expected_torso_x)
            > GIRL_TORSO_METRIC_TOLERANCE
        ):
            raise RuntimeError(
                f"{relative_path} independent torso metric is {torso_x}; "
                f"expected {expected_torso_x}."
            )
        baseline_y = int(alpha_y.max())
        if (
            abs(baseline_y - GIRL_TARGET_BASELINE_Y)
            > GIRL_MAXIMUM_BASELINE_DRIFT
        ):
            raise RuntimeError(
                f"{relative_path} baseline is {baseline_y}; expected "
                f"{GIRL_TARGET_BASELINE_Y}."
            )
        green_pixels, largest_green = validate_girl_green_background(
            relative_path,
            pixels,
        )
        print(
            f"girl_frame_ok={relative_path} bytes={expected_bytes} "
            f"sha256={actual_hash} alpha_pixels={alpha_pixels} "
            f"alpha_bounds={measured_bounds} torso_x={torso_x:.2f} "
            f"baseline_y={baseline_y} green_pixels={green_pixels} "
            f"largest_green_component={largest_green}"
        )
        hashes.append(actual_hash)
        bounds.append(measured_bounds)
        frames.append(pixels)
        torso_centers.append(torso_x)
        baselines.append(baseline_y)
        green_counts.append(green_pixels)

    if len(set(hashes)) != 12:
        raise RuntimeError(
            "Walking-girl output hashes must be exactly 12 distinct values."
        )

    adjacent_changes = []
    for index in range(len(frames) - 1):
        changed = int(
            np.count_nonzero(
                np.any(frames[index] != frames[index + 1], axis=2)
            )
        )
        if changed < GIRL_MINIMUM_ADJACENT_CHANGED_PIXELS:
            raise RuntimeError(
                f"Walking-girl frames {index:02d}/{index + 1:02d} differ "
                f"in only {changed} pixels."
            )
        adjacent_changes.append(changed)

    union_bounds = (
        min(bound[0] for bound in bounds),
        min(bound[1] for bound in bounds),
        max(bound[2] for bound in bounds),
        max(bound[3] for bound in bounds),
    )
    if union_bounds != GIRL_ALPHA_UNION_BOUNDS:
        raise RuntimeError(
            f"Walking-girl alpha union is {union_bounds}; "
            f"expected {GIRL_ALPHA_UNION_BOUNDS}."
        )
    print(
        "girl_frames_ok=count:12 distinct_hashes:12 "
        f"union_bounds={union_bounds} adjacent_changes={adjacent_changes} "
        f"torso_target={GIRL_TORSO_TARGET_X:.0f} "
        "torso_alignment_maximum_drift="
        f"{max(abs(value - GIRL_TORSO_TARGET_X) for value in torso_centers):.2f} "
        "torso_contract_maximum_drift="
        f"{max(abs(actual - expected) for actual, expected in zip(torso_centers, GIRL_EXPECTED_TORSO_CENTERS, strict=True)):.2f} "
        f"baseline_y={GIRL_TARGET_BASELINE_Y} "
        "baseline_maximum_drift="
        f"{max(abs(value - GIRL_TARGET_BASELINE_Y) for value in baselines)} "
        f"green_pixels_max={max(green_counts, default=0)}"
    )


def validate_frame_tree() -> None:
    frames_root = ROOT / "Frames"
    actual_directories = {
        path.name for path in frames_root.iterdir() if path.is_dir()
    }
    expected_directories = {
        *FRAME_GROUPS,
        "Backgrounds",
        "Effects",
        "GirlWalk",
        "SchoolCharacter",
    }
    if actual_directories != expected_directories:
        raise RuntimeError(
            f"Frame directories are {sorted(actual_directories)}; "
            f"expected {sorted(expected_directories)}."
        )

    girl_png_names = {
        Path(relative_path).name
        for relative_path in EXPECTED_GIRL_FRAME_PATHS
    }
    girl_entries = {
        path.name
        for path in (frames_root / "GirlWalk").iterdir()
        if path.is_file()
    }
    girl_entries_without_imports = girl_png_names
    girl_entries_with_imports = girl_png_names | {
        f"{name}.import" for name in girl_png_names
    }
    if girl_entries not in (
        girl_entries_without_imports,
        girl_entries_with_imports,
    ):
        raise RuntimeError(
        f"Frames/GirlWalk files are {sorted(girl_entries)}; expected "
        "exactly 12 PNGs, with either zero or all 12 Godot import "
        "sidecars."
        )

    for directory, png_names in FRAME_GROUPS.items():
        expected_entries = {
            *png_names,
            *(f"{name}.import" for name in png_names),
        }
        actual_entries = {
            path.name
            for path in (frames_root / directory).iterdir()
            if path.is_file()
        }
        if actual_entries != expected_entries:
            raise RuntimeError(
                f"Frames/{directory} files are {sorted(actual_entries)}; "
                f"expected {sorted(expected_entries)}."
            )

        school_directory = frames_root / "SchoolCharacter" / directory
        school_entries = {
            path.name
            for path in school_directory.iterdir()
            if path.is_file()
        }
        if school_entries != expected_entries:
            raise RuntimeError(
                f"Frames/SchoolCharacter/{directory} files are "
                f"{sorted(school_entries)}; expected "
                f"{sorted(expected_entries)}."
            )

    background_entries = {
        path.name
        for path in (frames_root / "Backgrounds").iterdir()
        if path.is_file()
    }
    expected_background_entries = {
        *(
            f"school{school_number}.png"
            for school_number in range(1, 7)
        ),
        *(
            f"school{school_number}.png.import"
            for school_number in range(1, 7)
        ),
    }
    if background_entries != expected_background_entries:
        raise RuntimeError(
            f"Frames/Backgrounds files are {sorted(background_entries)}; "
            f"expected {sorted(expected_background_entries)}."
        )

    effect_entries = {
        path.name
        for path in (frames_root / "Effects").iterdir()
        if path.is_file()
    }
    expected_effect_entries = {
        "logo-small.png",
        "logo-small.png.import",
        "tcf-neon-background.png",
        "tcf-neon-background.png.import",
    }
    if effect_entries != expected_effect_entries:
        raise RuntimeError(
            f"Frames/Effects files are {sorted(effect_entries)}; "
            f"expected {sorted(expected_effect_entries)}."
        )

    actual_pngs = tuple(
        path.relative_to(ROOT).as_posix()
        for path in sorted(frames_root.rglob("*.png"))
    )
    if (
        set(actual_pngs) != set(EXPECTED_TEXTURE_PATHS)
        or len(actual_pngs) != 86
    ):
        raise RuntimeError(
            f"Runtime PNG set has {len(actual_pngs)} files; expected exact 86."
        )


def validate_inventory_counts() -> None:
    counts = {
        "sources": len(SOURCE_CONTRACTS),
        "character_textures": EXPECTED_CHARACTER_TEXTURE_COUNT,
        "runtime_textures": len(EXPECTED_TEXTURE_PATHS),
        "export_resources": len(EXPECTED_EXPORT_RESOURCES),
        "stage_entries": len(EXPECTED_STAGE_ENTRIES),
    }
    expected = {
        "sources": 13,
        "character_textures": 78,
        "runtime_textures": 86,
        "export_resources": 89,
        "stage_entries": 134,
    }
    if counts != expected:
        raise RuntimeError(
            f"Calculated release counts are {counts}; expected {expected}."
        )
    print(
        "inventory_counts_ok="
        + ",".join(f"{name}:{value}" for name, value in counts.items())
    )


def validate_action_messages() -> None:
    path = ROOT / "ActionMessages.json"
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        raise RuntimeError("ActionMessages.json must not contain a UTF-8 BOM.")
    try:
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise RuntimeError(
            f"ActionMessages.json is invalid: {exception}."
        ) from exception
    expected_keys = {str(number) for number in range(1, 7)} | {"F", "S"}
    if not isinstance(document, dict) or set(document) != expected_keys:
        raise RuntimeError(
            "ActionMessages.json must contain exactly keys 1-6, F, and S."
        )
    for key, value in document.items():
        if not isinstance(value, str):
            raise RuntimeError(
                f"ActionMessages.json value {key} must be a string."
            )
        normalized = " ".join(value.split())
        if not normalized or len(normalized) > 500:
            raise RuntimeError(
                f"ActionMessages.json value {key} must normalize to 1-500 "
                "Unicode scalar values."
            )
    print("action_messages_ok=keys:1,2,3,4,5,6,F,S max_scalars=500")


def validate_animation_config() -> None:
    path = ROOT / "AnimationConfig.json"
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        raise RuntimeError("AnimationConfig.json must not contain a UTF-8 BOM.")
    try:
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise RuntimeError(
            f"AnimationConfig.json is invalid: {exception}."
        ) from exception
    expected_keys = {
        "turnFps",
        "walkFps",
        "girlWalkFps",
        "girlEntrySeconds",
        "clapFps",
        "crossArmFps",
        "crossArmReleaseFps",
        "avatarEntrySeconds",
        "avatarExitFullSpanSeconds",
        "neonIntensity",
        "neonHueCycleSeconds",
        "neonPulseSeconds",
        "schoolPrepositionSeconds",
        "schoolEntrySeconds",
        "schoolClapSeconds",
        "schoolBackgroundNormalizationSeconds",
        "schoolExitSeconds",
        "celebrationWalkSeconds",
        "celebrationClapSeconds",
        "fireworksSpawnIntervalSeconds",
        "fireworksBurstSeconds",
        "logoRainSpawnSeconds",
        "logoRainSpawnIntervalSeconds",
    }
    if not isinstance(document, dict) or set(document) != expected_keys:
        raise RuntimeError(
            "AnimationConfig.json must contain exactly the supported keys."
        )
    for key, value in document.items():
        if (
            not isinstance(value, (int, float))
            or isinstance(value, bool)
            or not math.isfinite(value)
            or value <= 0
        ):
            raise RuntimeError(
                f"AnimationConfig.json value {key} must be a finite "
                "positive number."
            )
    if document["girlWalkFps"] != 8.0:
        raise RuntimeError(
            "AnimationConfig.json girlWalkFps must remain exactly 8.0."
        )
    if document["girlEntrySeconds"] != 6.0:
        raise RuntimeError(
            "AnimationConfig.json girlEntrySeconds must remain exactly 6.0."
        )
    if document["girlEntrySeconds"] * document["girlWalkFps"] < 12:
        raise RuntimeError(
            "AnimationConfig.json girl timing product must be at least 12."
        )

    config_source = (ROOT / "AnimationConfig.cs").read_text(
        encoding="utf-8"
    )
    for name, expected in (
        ("GirlWalkFps", "8.0"),
        ("GirlEntrySeconds", "6.0"),
    ):
        if re.search(
            rf"\b{name}\s*=\s*{re.escape(expected)}\s*,",
            config_source,
        ) is None:
            raise RuntimeError(
                f"AnimationConfig.cs default {name} must remain {expected}."
            )
    if re.search(
        r"GirlEntrySeconds\s*\*\s*GirlWalkFps\s*"
        r"<\s*GirlEntranceStateMachine\.FrameCount",
        config_source,
    ) is None:
        raise RuntimeError(
            "AnimationConfig.cs must reject girl timing products below "
            "GirlEntranceStateMachine.FrameCount."
        )
    print(
        "animation_config_ok=config_keys:23 positive=true "
        "girl_walk_fps=8 girl_entry_seconds=6 "
        "girl_timing_product=48 minimum=12 strict=true"
    )


def validate_girl_geometry_contract() -> None:
    source = (ROOT / "AnimationGeometry.cs").read_text(encoding="utf-8")
    expected_constants = {
        "ViewportWidth": 1920.0,
        "CanvasCenterX": 256.0,
        "CharacterScale": 1.25,
        "GirlLeftVisibleX": 108.0,
        "GirlTopVisibleY": 57.0,
        "GirlRightVisibleX": 406.0,
        "GirlBottomVisibleY": 840.0,
    }
    actual_constants = {}
    for name, expected in expected_constants.items():
        match = re.search(
            rf"public const float {name}\s*=\s*([0-9]+(?:\.[0-9]+)?)f\s*;",
            source,
        )
        if match is None:
            raise RuntimeError(
                f"AnimationGeometry.cs is missing float constant {name}."
            )
        actual = float(match.group(1))
        if actual != expected:
            raise RuntimeError(
                f"AnimationGeometry.cs {name} is {actual}; "
                f"expected {expected}."
            )
        actual_constants[name] = actual

    if re.search(
        r"GirlOffscreenCenters\s*=>\s*CalculateOffscreenCenters\s*\("
        r".*?leftVisibleX:\s*GirlLeftVisibleX\s*,"
        r".*?rightVisibleX:\s*GirlRightVisibleX\s*\)",
        source,
        flags=re.DOTALL,
    ) is None:
        raise RuntimeError(
            "AnimationGeometry.GirlOffscreenCenters must route through "
            "CalculateOffscreenCenters with the frozen girl extrema."
        )

    expected_bounds = (
        int(actual_constants["GirlLeftVisibleX"]),
        int(actual_constants["GirlTopVisibleY"]),
        int(actual_constants["GirlRightVisibleX"]),
        int(actual_constants["GirlBottomVisibleY"]),
    )
    if expected_bounds != GIRL_ALPHA_UNION_BOUNDS:
        raise RuntimeError(
            f"AnimationGeometry girl bounds are {expected_bounds}; "
            f"expected {GIRL_ALPHA_UNION_BOUNDS}."
        )
    offscreen_left = -(
        actual_constants["GirlRightVisibleX"]
        - actual_constants["CanvasCenterX"]
    ) * actual_constants["CharacterScale"]
    if offscreen_left != -187.5:
        raise RuntimeError(
            f"Girl offscreen-left center is {offscreen_left}; "
            "expected -187.5."
        )
    print(
        "girl_geometry_ok=bounds:108,57,406,840 "
        "offscreen_left=-187.5 viewport_center=960 fixed_constants=true"
    )


def validate_logo_effect() -> None:
    path = ROOT / EXPECTED_EFFECT_PATHS[0]
    with Image.open(path) as image:
        if (
            image.format != "PNG"
            or image.mode != "RGBA"
            or image.size != (128, 102)
        ):
            raise RuntimeError(
                f"{path.relative_to(ROOT)} is {image.format} {image.mode} "
                f"{image.size}; expected PNG RGBA (128, 102)."
            )
        alpha = np.asarray(image, dtype=np.uint8)[:, :, 3]
    if not np.any(alpha == 0) or not np.any(alpha == 255):
        raise RuntimeError(
            "Small logo must contain both transparent and opaque pixels."
        )
    print("logo_effect_ok=size:128x102 transparent=true duration_seconds=10")


def validate_neon_background() -> None:
    path = ROOT / EXPECTED_EFFECT_PATHS[1]
    if path.stat().st_size != 1_246_534:
        raise RuntimeError(
            f"{path.relative_to(ROOT)} has unexpected byte length."
        )
    if hashlib.sha256(path.read_bytes()).hexdigest().upper() != (
        "F32624AE81B2C0C30D88B9673C4D7A3A2939BF75003EC5FDE32C15FDF302BE08"
    ):
        raise RuntimeError(
            f"{path.relative_to(ROOT)} has unexpected content."
        )
    with Image.open(path) as image:
        if (
            image.format != "PNG"
            or image.mode != "RGBA"
            or image.size != (2062, 763)
        ):
            raise RuntimeError(
                f"{path.relative_to(ROOT)} is {image.format} {image.mode} "
                f"{image.size}; expected PNG RGBA (2062, 763)."
            )
    print(
        "neon_background_ok=size:2062x763 true_png=true transparent=true"
    )


def validate_runtime_pngs() -> None:
    for relative_path in EXPECTED_FRAME_PATHS:
        path = ROOT / relative_path
        with Image.open(path) as image:
            if (
                image.format != "PNG"
                or image.mode != "RGBA"
                or image.size != CANVAS_SIZE
            ):
                raise RuntimeError(
                    f"{relative_path} is {image.format} {image.mode} "
                    f"{image.size}; expected PNG RGBA {CANVAS_SIZE}."
                )
            pixels = np.asarray(image, dtype=np.uint8)
        if not np.all(pixels[:, :, 3] == 255):
            raise RuntimeError(f"{relative_path} is not fully opaque.")

        values = np.max(pixels[:, :, :3], axis=2)
        charcoal_count = int(
            np.count_nonzero(values == ARTIFICIAL_CHARCOAL_VALUE)
        )
        adjacent_count = max(
            int(np.count_nonzero(values == ARTIFICIAL_CHARCOAL_VALUE - 1)),
            int(np.count_nonzero(values == ARTIFICIAL_CHARCOAL_VALUE + 1)),
            1,
        )
        if (
            charcoal_count >= CHARCOAL_SPIKE_MINIMUM
            and charcoal_count > adjacent_count * CHARCOAL_SPIKE_RATIO
        ):
            raise RuntimeError(
                f"{relative_path} has an artificial value-"
                f"{ARTIFICIAL_CHARCOAL_VALUE} plateau: "
                f"{charcoal_count} pixels versus {adjacent_count} in an "
                "adjacent value bin."
            )


def add_generated(
    regenerated: dict[str, tuple[np.ndarray, np.ndarray]],
    relative_path: str,
    frame: np.ndarray,
    matte: np.ndarray,
) -> None:
    if relative_path in regenerated:
        raise RuntimeError(f"Duplicate regenerated frame: {relative_path}")
    regenerated[relative_path] = (frame, matte)


def regenerate_in_memory() -> dict[str, tuple[np.ndarray, np.ndarray]]:
    regenerated: dict[str, tuple[np.ndarray, np.ndarray]] = {}

    extract_directional_turns.cv2.setRNGSeed(0)
    turn_generated: dict[
        str,
        list[extract_directional_turns.GeneratedFrame],
    ] = {}
    turn_hashes: dict[str, str] = {}
    for config in extract_directional_turns.SHEET_CONFIGS:
        source, hash_before = extract_directional_turns.validate_source(config)
        generated = extract_directional_turns.generate_frames(source, config)
        extract_directional_turns.validate_frames(source, generated, config)
        turn_generated[config.name] = generated
        turn_hashes[config.name] = hash_before
    extract_directional_turns.canonicalize_front_identity(turn_generated)
    for config in extract_directional_turns.SHEET_CONFIGS:
        generated = turn_generated[config.name]
        for index, item in enumerate(generated):
            add_generated(
                regenerated,
                f"Frames/{config.name}/turn_{index}.png",
                item.frame,
                item.cutout.matte,
            )
        if (
            extract_directional_turns.source_sha256(config)
            != turn_hashes[config.name]
        ):
            raise RuntimeError(f"{config.source_path.name} changed in memory check.")

    walk_source, walk_hash = extract_right_walk.validate_source()
    extract_right_walk.validate_source_geometry(walk_source)
    extract_right_walk.cv2.setRNGSeed(0)
    walk_frames = extract_right_walk.generate_frames(walk_source)
    extract_right_walk.validate_generated_frames(walk_frames)
    for index, item in enumerate(walk_frames):
        name = f"walk_{index:02d}.png"
        add_generated(
            regenerated,
            f"Frames/RightWalk/{name}",
            item.frame,
            item.cutout.matte,
        )
        add_generated(
            regenerated,
            f"Frames/LeftWalk/{name}",
            item.frame[:, ::-1],
            item.cutout.matte[:, ::-1],
        )
    if extract_right_walk.source_sha256() != walk_hash:
        raise RuntimeError("RWalking2.png changed in memory check.")

    clap_source, clap_hash = extract_clap.validate_source()
    extract_clap.validate_source_geometry()
    clap_frames = extract_clap.generate_frames(clap_source)
    extract_clap.validate_generated(clap_frames)
    for index, item in enumerate(clap_frames):
        add_generated(
            regenerated,
            f"Frames/Clap/clap_{index:02d}.png",
            item.frame,
            item.cutout.matte,
        )
    if extract_clap.source_sha256() != clap_hash:
        raise RuntimeError("Clapping2.png changed in memory check.")

    cross_generated: dict[
        str,
        list[extract_cross_arm.GeneratedFrame],
    ] = {}
    cross_hashes: dict[str, str] = {}
    for spec in (
        extract_cross_arm.CROSS_SPEC,
        extract_cross_arm.RELEASE_SPEC,
    ):
        source, source_hash = extract_cross_arm.validate_source(spec)
        extract_cross_arm.validate_source_geometry(spec)
        extract_cross_arm.cv2.setRNGSeed(0)
        generated = extract_cross_arm.generate_frames(source, spec)
        extract_cross_arm.validate_generated(generated, spec)
        cross_generated[spec.output_directory] = generated
        cross_hashes[spec.output_directory] = source_hash
        if extract_cross_arm.source_sha256(spec) != source_hash:
            raise RuntimeError(f"{spec.source_name} changed in memory check.")

    cross_frames, release_frames, selected_offsets = (
        extract_cross_arm.align_cross_release_continuity(
            cross_generated["CrossArm"],
            cross_generated["CrossArmRelease"],
        )
    )
    extract_cross_arm.validate_final_continuity(
        cross_frames,
        release_frames,
        selected_offsets,
    )
    for spec, generated in (
        (extract_cross_arm.CROSS_SPEC, cross_frames),
        (extract_cross_arm.RELEASE_SPEC, release_frames),
    ):
        for index, item in enumerate(generated):
            add_generated(
                regenerated,
                (
                    f"Frames/{spec.output_directory}/"
                    f"{spec.output_prefix}_{index:02d}.png"
                ),
                item.frame,
                item.cutout.matte,
            )
        if (
            extract_cross_arm.source_sha256(spec)
            != cross_hashes[spec.output_directory]
        ):
            raise RuntimeError(f"{spec.source_name} changed in memory check.")

    if set(regenerated) != set(EXPECTED_FRAME_PATHS):
        missing = sorted(set(EXPECTED_FRAME_PATHS) - set(regenerated))
        extra = sorted(set(regenerated) - set(EXPECTED_FRAME_PATHS))
        raise RuntimeError(
            f"Regeneration set mismatch; missing={missing} extra={extra}."
        )
    return regenerated


def validate_generated_contracts(
    regenerated: dict[str, tuple[np.ndarray, np.ndarray]],
) -> None:
    for relative_path, (frame, matte) in regenerated.items():
        if frame.shape != (CANVAS_SIZE[1], CANVAS_SIZE[0], 4):
            raise RuntimeError(
                f"{relative_path} regenerated shape is {frame.shape}."
            )
        if matte.shape != (CANVAS_SIZE[1], CANVAS_SIZE[0]):
            raise RuntimeError(
                f"{relative_path} matte shape is {matte.shape}."
            )
        if not np.all(frame[:, :, 3] == 255):
            raise RuntimeError(f"{relative_path} regenerated alpha is not opaque.")
        if np.any(frame[:, :, :3][matte == 0] != 0):
            raise RuntimeError(
                f"{relative_path} has non-black RGB outside regenerated matte."
            )
        if (
            np.any(frame[0, :, :3])
            or np.any(frame[-1, :, :3])
            or np.any(frame[:, 0, :3])
            or np.any(frame[:, -1, :3])
        ):
            raise RuntimeError(
                f"{relative_path} visible character touches a canvas edge."
            )
        component_count, _, component_stats, _ = cv2.connectedComponentsWithStats(
            matte.astype(np.uint8),
            8,
        )
        component_areas = sorted(
            (
                int(area)
                for area in component_stats[1:, cv2.CC_STAT_AREA]
            ),
            reverse=True,
        )
        if not component_areas:
            raise RuntimeError(
                f"{relative_path} matte has no character component."
            )
        primary_label = 1 + int(
            np.argmax(component_stats[1:, cv2.CC_STAT_AREA])
        )
        for label in range(1, component_count):
            if label == primary_label:
                continue
            x, y, width, height, area = map(
                int,
                component_stats[label],
            )
            if y < 650 or area > 1_000:
                raise RuntimeError(
                    f"{relative_path} matte component {label} has "
                    f"unapproved bounds {(x, y, width, height, area)}."
                )
            if width >= 24 and height <= 3:
                raise RuntimeError(
                    f"{relative_path} has a long narrow lower run "
                    f"{(x, y, width, height, area)}."
                )

        visible = np.max(frame[:, :, :3], axis=2) > 0
        visible_count, _, visible_stats, _ = (
            cv2.connectedComponentsWithStats(
                visible.astype(np.uint8),
                8,
            )
        )
        visible_primary = 1 + int(
            np.argmax(visible_stats[1:, cv2.CC_STAT_AREA])
        )
        for label in range(1, visible_count):
            if label == visible_primary:
                continue
            x, y, width, height, area = map(int, visible_stats[label])
            if (y < 650 and area > 30) or area > 1_000:
                raise RuntimeError(
                    f"{relative_path} visible component {label} has "
                    f"unapproved bounds {(x, y, width, height, area)}."
                )
            if width >= 24 and height <= 3:
                raise RuntimeError(
                    f"{relative_path} has a visible floor-like run "
                    f"{(x, y, width, height, area)}."
                )

        disk = cv2.imread(
            str(ROOT / relative_path),
            cv2.IMREAD_UNCHANGED,
        )
        if disk is None or not np.array_equal(disk, frame):
            raise RuntimeError(
                f"{relative_path} pixels differ from in-memory regeneration."
            )
        encoded_ok, encoded = cv2.imencode(
            ".png",
            frame,
            [cv2.IMWRITE_PNG_COMPRESSION, 9],
        )
        if not encoded_ok:
            raise RuntimeError(f"Could not encode regenerated {relative_path}.")
        if encoded.tobytes() != (ROOT / relative_path).read_bytes():
            raise RuntimeError(
                f"{relative_path} encoded PNG bytes are not deterministic."
            )

    for index in range(6):
        name = f"walk_{index:02d}.png"
        left = regenerated[f"Frames/LeftWalk/{name}"][0]
        right = regenerated[f"Frames/RightWalk/{name}"][0]
        if not np.array_equal(left, right[:, ::-1]):
            raise RuntimeError(f"{name} is not an exact horizontal mirror.")

    walk_extrema = {}
    for direction in ("LeftWalk", "RightWalk"):
        xs = []
        for name in FRAME_GROUPS[direction]:
            frame = regenerated[f"Frames/{direction}/{name}"][0]
            _, frame_xs = np.where(np.max(frame[:, :, :3], axis=2) > 0)
            xs.extend(frame_xs.tolist())
        walk_extrema[direction] = (min(xs), max(xs))
    if walk_extrema["LeftWalk"][0] != 74:
        raise RuntimeError(
            f"LeftWalk visible minimum x is {walk_extrema['LeftWalk'][0]}; "
            "expected 74."
        )
    if walk_extrema["RightWalk"][1] != 437:
        raise RuntimeError(
            f"RightWalk visible maximum x is {walk_extrema['RightWalk'][1]}; "
            "expected 437."
        )

    extract_cross_arm.validate_existing_height_assets("release-check")
    print(
        "regeneration_ok=pixels_and_png_bytes "
        "mirror=exact walk_extrema=74,437 "
        "components=segmented_and_bounded_lower_anatomy "
        "sole_envelopes=clear floor_runs=absent edges=clear"
    )


def validate_school_runtime_assets(
    regenerated: dict[str, tuple[np.ndarray, np.ndarray]],
) -> None:
    background_summaries = []
    for relative_path, contract in EXPECTED_BACKGROUNDS.items():
        expected_hash, expected_bytes, expected_size, source_name = contract
        background_path = ROOT / relative_path
        actual_hash = sha256_file(background_path, expected_bytes)
        if actual_hash != expected_hash:
            raise RuntimeError(
                f"{relative_path} differs from its deterministic hash."
            )
        with Image.open(background_path) as background:
            if (
                background.format != "PNG"
                or background.mode != "RGB"
                or background.size != expected_size
            ):
                raise RuntimeError(
                    f"{relative_path} is {background.format} "
                    f"{background.mode} {background.size}; expected PNG RGB "
                    f"{expected_size}."
                )
            background_rgb = np.asarray(background, dtype=np.uint8)
        with Image.open(ROOT / source_name) as source:
            source_rgb = np.asarray(source.convert("RGB"), dtype=np.uint8)
        if not np.array_equal(background_rgb, source_rgb):
            raise RuntimeError(
                f"{relative_path} does not preserve {source_name} RGB."
            )
        background_summaries.append(
            f"{Path(relative_path).name}:{expected_size[0]}x"
            f"{expected_size[1]}:{actual_hash}"
        )

    dark_foreground_pixels = 0
    for relative_path, (frame, matte) in regenerated.items():
        overlay_path = (
            ROOT
            / "Frames"
            / "SchoolCharacter"
            / Path(relative_path).relative_to("Frames")
        )
        overlay = cv2.imread(str(overlay_path), cv2.IMREAD_UNCHANGED)
        if overlay is None or overlay.shape != (864, 512, 4):
            raise RuntimeError(
                f"{overlay_path.relative_to(ROOT).as_posix()} is not "
                "a 512x864 RGBA PNG."
            )
        alpha = overlay[:, :, 3]
        expected_alpha = (matte != 0).astype(np.uint8) * 255
        if not np.array_equal(alpha, expected_alpha):
            raise RuntimeError(
                f"{overlay_path.relative_to(ROOT).as_posix()} alpha differs "
                "from the validated extraction matte."
            )
        if not np.array_equal(overlay[:, :, :3], frame[:, :, :3]):
            raise RuntimeError(
                f"{overlay_path.relative_to(ROOT).as_posix()} changes "
                "validated character RGB."
            )
        if np.any(alpha[matte == 0] != 0):
            raise RuntimeError(
                f"{overlay_path.relative_to(ROOT).as_posix()} retains an "
                "opaque black canvas outside the matte."
            )
        dark_foreground = (
            (np.max(frame[:, :, :3], axis=2) <= DARK_SHOE_MAXIMUM_VALUE)
            & (matte != 0)
        )
        if np.any(alpha[dark_foreground] != 255):
            raise RuntimeError(
                f"{overlay_path.relative_to(ROOT).as_posix()} loses dark "
                "foreground pixels."
            )
        dark_foreground_pixels += int(np.count_nonzero(dark_foreground))

    print(
        "school_overlay_ok=frames:33 opaque_canvas=false "
        f"dark_foreground_preserved={dark_foreground_pixels} "
        "backgrounds=6 aspect_source_preserved=true "
        f"background_contracts={','.join(background_summaries)}"
    )


def visible_continuity_metrics(frame: np.ndarray) -> dict[str, float]:
    """Measure validator-owned visible geometry without extractor constants."""

    rgb = frame[:, :, :3]
    visible = np.max(frame[:, :, :3], axis=2) > 0
    ys, _ = np.where(visible)
    if ys.size == 0:
        raise RuntimeError("Continuity frame has no visible foreground.")
    top = int(ys.min())
    bottom = int(ys.max())
    height = bottom - top + 1
    rows = np.indices(visible.shape)[0]
    head = visible & (rows <= top + round(height * 0.20))
    torso = visible & (
        (rows >= top + round(height * 0.30))
        & (rows <= top + round(height * 0.66))
    )
    head_y, head_x = np.where(head)
    torso_y, torso_x = np.where(torso)

    hsv = cv2.cvtColor(rgb, cv2.COLOR_BGR2HSV)
    vest = (
        (hsv[:, :, 0] >= 35)
        & (hsv[:, :, 0] <= 100)
        & (hsv[:, :, 1] >= 45)
        & (hsv[:, :, 2] >= 25)
        & visible
    )
    vest_y, _ = np.where(vest)
    if vest_y.size == 0:
        raise RuntimeError(
            "Continuity frame has no independently measured vest landmarks."
        )
    shoulder_y = float(
        np.percentile(vest_y, 5, method="nearest")
    )
    waist_y = float(
        np.percentile(vest_y, 95, method="nearest")
    )

    return {
        "head_x": float(head_x.mean()),
        "head_y": float(head_y.mean()),
        "torso_x": float(torso_x.mean()),
        "torso_y": float(torso_y.mean()),
        "shoulder_y": shoulder_y,
        "waist_y": waist_y,
        "baseline_y": float(bottom),
        "height": float(height),
    }


def binary_silhouette_symmetric_difference(
    before_frame: np.ndarray,
    after_frame: np.ndarray,
) -> float:
    before = (
        np.max(
            before_frame[
                STATIONARY_BLEND_BAND_TOP_Y:STATIONARY_PLATE_SEAM_Y,
                :,
                :3,
            ],
            axis=2,
        )
        > 0
    )
    after = (
        np.max(
            after_frame[
                STATIONARY_BLEND_BAND_TOP_Y:STATIONARY_PLATE_SEAM_Y,
                :,
                :3,
            ],
            axis=2,
        )
        > 0
    )
    union = int(np.count_nonzero(before | after))
    if union == 0:
        return 0.0
    symmetric_difference = int(np.count_nonzero(before ^ after))
    return symmetric_difference / union


def validate_shared_stationary_plate(
    relative_path: str,
    frame: np.ndarray,
    canonical: np.ndarray,
) -> None:
    frame_rgb = frame[STATIONARY_PLATE_SEAM_Y:, :, :3]
    canonical_rgb = canonical[STATIONARY_PLATE_SEAM_Y:, :, :3]
    if not np.array_equal(frame_rgb, canonical_rgb):
        raise RuntimeError(
            f"{relative_path} shared-plate pixel mismatch at or below row "
            f"{STATIONARY_PLATE_SEAM_Y}."
        )
    frame_visible = np.max(frame_rgb, axis=2) > 0
    canonical_visible = np.max(canonical_rgb, axis=2) > 0
    if not np.array_equal(frame_visible, canonical_visible):
        raise RuntimeError(
            f"{relative_path} shared-plate visibility mismatch at or below "
            f"row {STATIONARY_PLATE_SEAM_Y}."
        )


def validate_continuity_edge(
    label: str,
    before_frame: np.ndarray,
    after_frame: np.ndarray,
    kind: BoundaryKind,
) -> dict[str, float]:
    before = visible_continuity_metrics(before_frame)
    after = visible_continuity_metrics(after_frame)
    head_delta = max(
        abs(after["head_x"] - before["head_x"]),
        abs(after["head_y"] - before["head_y"]),
    )
    torso_delta = max(
        abs(after["torso_x"] - before["torso_x"]),
        abs(after["torso_y"] - before["torso_y"]),
    )
    shoulder_delta = abs(after["shoulder_y"] - before["shoulder_y"])
    waist_delta = abs(after["waist_y"] - before["waist_y"])
    baseline_delta = abs(after["baseline_y"] - before["baseline_y"])
    height_delta = abs(after["height"] - before["height"])
    silhouette_delta = binary_silhouette_symmetric_difference(
        before_frame,
        after_frame,
    )

    if kind is BoundaryKind.STATIONARY:
        if (
            head_delta > STATIONARY_HEAD_LIMIT
            or torso_delta > STATIONARY_TORSO_LIMIT
        ):
            raise RuntimeError(
                f"Stationary boundary {label} exceeds head/torso continuity: "
                f"head={head_delta:.2f}/{STATIONARY_HEAD_LIMIT:.0f} "
                f"torso={torso_delta:.2f}/{STATIONARY_TORSO_LIMIT:.0f}."
            )
        if baseline_delta > STATIONARY_BASELINE_LIMIT:
            raise RuntimeError(
                f"Stationary boundary {label} exceeds shoe-baseline "
                f"continuity: baseline={baseline_delta:.2f}/"
                f"{STATIONARY_BASELINE_LIMIT:.0f}."
            )
        if silhouette_delta > STATIONARY_SILHOUETTE_LIMIT:
            raise RuntimeError(
                f"Stationary boundary {label} exceeds lower-band binary "
                "silhouette symmetric difference / union: "
                f"value={silhouette_delta:.4f}/"
                f"{STATIONARY_SILHOUETTE_LIMIT:.2f}."
            )
    else:
        if head_delta > WALK_HEAD_LIMIT:
            raise RuntimeError(
                f"Moving boundary {label} exceeds head continuity: "
                f"head={head_delta:.2f}/{WALK_HEAD_LIMIT:.0f}."
            )
        if torso_delta > WALK_TORSO_LIMIT:
            raise RuntimeError(
                f"Moving {kind.value} boundary {label} exceeds torso-axis "
                "continuity: "
                f"torso={torso_delta:.2f}/{WALK_TORSO_LIMIT:.0f}."
            )
        if (
            shoulder_delta > WALK_LANDMARK_LIMIT
            or waist_delta > WALK_LANDMARK_LIMIT
        ):
            raise RuntimeError(
                f"Moving {kind.value} boundary {label} exceeds actual "
                "shoulder/waist "
                f"landmark continuity: shoulder={shoulder_delta:.2f}/"
                f"{WALK_LANDMARK_LIMIT:.0f} waist={waist_delta:.2f}/"
                f"{WALK_LANDMARK_LIMIT:.0f}."
            )
        if baseline_delta > WALK_BASELINE_LIMIT:
            raise RuntimeError(
                f"Moving boundary {label} exceeds walking shoe-baseline "
                f"continuity: baseline={baseline_delta:.2f}/"
                f"{WALK_BASELINE_LIMIT:.0f}."
            )
        if height_delta > WALK_HEIGHT_LIMIT:
            raise RuntimeError(
                f"Moving boundary {label} exceeds body-height continuity: "
                f"height={height_delta:.2f}/{WALK_HEIGHT_LIMIT:.0f}."
            )

    return {
        "head": head_delta,
        "torso": torso_delta,
        "shoulder": shoulder_delta,
        "waist": waist_delta,
        "baseline": baseline_delta,
        "height": height_delta,
        "silhouette": silhouette_delta,
    }


def continuity_boundaries() -> tuple[ContinuityBoundary, ...]:
    boundaries: list[ContinuityBoundary] = []
    for side in ("Left", "Right"):
        boundaries.extend(
            (
                ContinuityBoundary(
                    f"{side}_front_to_turn1",
                    f"Frames/{side}Turn/turn_0.png",
                    f"Frames/{side}Turn/turn_1.png",
                    BoundaryKind.TURNING,
                ),
                ContinuityBoundary(
                    f"{side}_turn1_to_turn2",
                    f"Frames/{side}Turn/turn_1.png",
                    f"Frames/{side}Turn/turn_2.png",
                    BoundaryKind.TURNING,
                ),
                ContinuityBoundary(
                    f"{side}_turn2_to_walk0",
                    f"Frames/{side}Turn/turn_2.png",
                    f"Frames/{side}Walk/walk_00.png",
                    BoundaryKind.TURNING,
                ),
            )
        )
        for index in range(6):
            boundaries.append(
                ContinuityBoundary(
                    f"{side}_walk{index:02d}_to_{(index + 1) % 6:02d}",
                    f"Frames/{side}Walk/walk_{index:02d}.png",
                    f"Frames/{side}Walk/walk_{(index + 1) % 6:02d}.png",
                    BoundaryKind.WALKING,
                )
            )

    boundaries.append(
        ContinuityBoundary(
            "front_handoff_left_to_right",
            "Frames/LeftTurn/turn_0.png",
            "Frames/RightTurn/turn_0.png",
            BoundaryKind.STATIONARY,
        )
    )
    clap_sequence = (0, 1, 2, 3, 4, 3, 2, 1, 2, 3, 4, 3, 2, 1, 0)
    boundaries.append(
        ContinuityBoundary(
            "front_to_clap00",
            "Frames/LeftTurn/turn_0.png",
            "Frames/Clap/clap_00.png",
            BoundaryKind.STATIONARY,
        )
    )
    for index, (left, right) in enumerate(
        zip(clap_sequence, clap_sequence[1:])
    ):
        boundaries.append(
            ContinuityBoundary(
                f"clap_step_{index:02d}_{left}_to_{right}",
                f"Frames/Clap/clap_{left:02d}.png",
                f"Frames/Clap/clap_{right:02d}.png",
                BoundaryKind.STATIONARY,
            )
        )
    boundaries.append(
        ContinuityBoundary(
            "clap_final_to_front",
            "Frames/Clap/clap_00.png",
            "Frames/LeftTurn/turn_0.png",
            BoundaryKind.STATIONARY,
        )
    )
    boundaries.extend(
        (
            ContinuityBoundary(
                "front_to_cross00",
                "Frames/LeftTurn/turn_0.png",
                "Frames/CrossArm/cross_00.png",
                BoundaryKind.STATIONARY,
            ),
            ContinuityBoundary(
                "cross00_to_cross01",
                "Frames/CrossArm/cross_00.png",
                "Frames/CrossArm/cross_01.png",
                BoundaryKind.STATIONARY,
            ),
            ContinuityBoundary(
                "cross01_to_cross02",
                "Frames/CrossArm/cross_01.png",
                "Frames/CrossArm/cross_02.png",
                BoundaryKind.STATIONARY,
            ),
            ContinuityBoundary(
                "cross02_to_release00",
                "Frames/CrossArm/cross_02.png",
                "Frames/CrossArmRelease/release_00.png",
                BoundaryKind.STATIONARY,
            ),
        )
    )
    for index in range(5):
        boundaries.append(
            ContinuityBoundary(
                f"release{index:02d}_to_{index + 1:02d}",
                f"Frames/CrossArmRelease/release_{index:02d}.png",
                f"Frames/CrossArmRelease/release_{index + 1:02d}.png",
                BoundaryKind.STATIONARY,
            )
        )
    boundaries.append(
        ContinuityBoundary(
            "release05_to_front",
            "Frames/CrossArmRelease/release_05.png",
            "Frames/LeftTurn/turn_0.png",
            BoundaryKind.STATIONARY,
        )
    )
    if len(boundaries) != 45:
        raise RuntimeError(
            f"Continuity boundary set has {len(boundaries)} entries; "
            "expected 45."
        )
    return tuple(boundaries)


def translate_control_frame(
    frame: np.ndarray,
    delta_x: int,
    delta_y: int,
) -> np.ndarray:
    height, width = frame.shape[:2]
    source_x0 = max(0, -delta_x)
    source_x1 = min(width, width - delta_x)
    source_y0 = max(0, -delta_y)
    source_y1 = min(height, height - delta_y)
    target_x0 = source_x0 + delta_x
    target_x1 = source_x1 + delta_x
    target_y0 = source_y0 + delta_y
    target_y1 = source_y1 + delta_y
    shifted = np.zeros_like(frame)
    shifted[:, :, 3] = 255
    shifted[target_y0:target_y1, target_x0:target_x1, :3] = frame[
        source_y0:source_y1,
        source_x0:source_x1,
        :3,
    ]
    return shifted


def shift_control_band_x(
    frame: np.ndarray,
    top: int,
    bottom: int,
    delta_x: int,
) -> np.ndarray:
    shifted = frame.copy()
    shifted[top:bottom, :, :3] = 0
    if delta_x > 0:
        shifted[top:bottom, delta_x:, :3] = frame[
            top:bottom,
            :-delta_x,
            :3,
        ]
    elif delta_x < 0:
        shifted[top:bottom, :delta_x, :3] = frame[
            top:bottom,
            -delta_x:,
            :3,
        ]
    return shifted


def shift_control_lower_band_y(
    frame: np.ndarray,
    top: int,
    delta_y: int,
) -> np.ndarray:
    if delta_y <= 0:
        raise RuntimeError("Control lower-band shift must be positive.")
    shifted = frame.copy()
    shifted[top:, :, :3] = 0
    shifted[top + delta_y :, :, :3] = frame[
        top:-delta_y,
        :,
        :3,
    ]
    return shifted


def validate_adversarial_continuity_controls(
    regenerated: dict[str, tuple[np.ndarray, np.ndarray]],
) -> None:
    canonical = regenerated["Frames/RightTurn/turn_0.png"][0]
    plate_path = "Frames/CrossArm/cross_02.png"
    plate_mutation = regenerated[plate_path][0].copy()
    plate_mutation[STATIONARY_PLATE_SEAM_Y, 256, 0] ^= 1
    try:
        validate_shared_stationary_plate(
            plate_path,
            plate_mutation,
            canonical,
        )
    except RuntimeError as exception:
        if "shared-plate pixel mismatch" not in str(exception):
            raise RuntimeError(
                "Shared-plate pixel control failed for the wrong reason."
            ) from exception
        print(
            "adversarial_control_ok=shared_plate_pixel_mutation "
            f"row={STATIONARY_PLATE_SEAM_Y} rejected=true "
            f"reason=plate_mismatch detail={exception}"
        )
    else:
        raise RuntimeError(
            "Validator accepted a shared stationary-plate pixel mutation."
        )

    stationary_before = regenerated["Frames/LeftTurn/turn_0.png"][0]
    stationary_after = translate_control_frame(
        regenerated["Frames/RightTurn/turn_0.png"][0],
        5,
        0,
    )
    try:
        validate_continuity_edge(
            "stationary_shift_5px",
            stationary_before,
            stationary_after,
            BoundaryKind.STATIONARY,
        )
    except RuntimeError as exception:
        if "head/torso continuity" not in str(exception):
            raise RuntimeError(
                "Five-pixel stationary shift control failed for the wrong "
                "reason."
            ) from exception
        print(
            "adversarial_control_ok=stationary_shift_5px "
            f"rejected=true reason=stationary_continuity detail={exception}"
        )
    else:
        raise RuntimeError(
            "Validator accepted a synthetic 5px stationary shift."
        )

    walk_before = regenerated["Frames/RightWalk/walk_00.png"][0]
    walk_after = shift_control_band_x(
        regenerated["Frames/RightWalk/walk_01.png"][0],
        320,
        640,
        15,
    )
    try:
        validate_continuity_edge(
            "walking_torso_shift_15px",
            walk_before,
            walk_after,
            BoundaryKind.WALKING,
        )
    except RuntimeError as exception:
        if "walking boundary" not in str(exception) or (
            "torso-axis continuity" not in str(exception)
        ):
            raise RuntimeError(
                "Fifteen-pixel walking torso/shoulder shift control failed "
                "for the wrong reason."
            ) from exception
        print(
            "adversarial_control_ok=walking_torso_shift_15px "
            f"rejected=true reason=walking_torso_continuity "
            f"detail={exception}"
        )
    else:
        raise RuntimeError(
            "Validator accepted a synthetic 15px walking torso/shoulder "
            "shift."
        )

    turning_before = regenerated["Frames/RightTurn/turn_1.png"][0]
    turning_after = regenerated["Frames/RightTurn/turn_2.png"][0].copy()
    turning_rgb = turning_after[:, :, :3]
    turning_visible = np.max(turning_rgb, axis=2) > 0
    turning_hsv = cv2.cvtColor(turning_rgb, cv2.COLOR_BGR2HSV)
    turning_vest = (
        (turning_hsv[:, :, 0] >= 35)
        & (turning_hsv[:, :, 0] <= 100)
        & (turning_hsv[:, :, 1] >= 45)
        & (turning_hsv[:, :, 2] >= 25)
        & turning_visible
    )
    turning_rows = np.indices(turning_vest.shape)[0]
    turning_after[
        turning_vest & (turning_rows < 335),
        :3,
    ] = (128, 128, 128)
    turning_metrics = visible_continuity_metrics(turning_after)
    turning_before_metrics = visible_continuity_metrics(turning_before)
    turning_shoulder_delta = abs(
        turning_metrics["shoulder_y"]
        - turning_before_metrics["shoulder_y"]
    )
    if turning_shoulder_delta != 41:
        raise RuntimeError(
            "Turning landmark mutation calibration drifted: "
            f"shoulder={turning_shoulder_delta:.2f}, expected 41.00."
        )
    try:
        validate_continuity_edge(
            "turning_landmark_shift_41px",
            turning_before,
            turning_after,
            BoundaryKind.TURNING,
        )
    except RuntimeError as exception:
        if "turning boundary" not in str(exception) or (
            "shoulder/waist landmark continuity" not in str(exception)
        ):
            raise RuntimeError(
                "Forty-one-pixel turning landmark control failed for the "
                "wrong reason."
            ) from exception
        print(
            "adversarial_control_ok=turning_landmark_shift_41px "
            "rejected=true reason=turning_landmark_continuity "
            f"detail={exception}"
        )
    else:
        raise RuntimeError(
            "Validator accepted a synthetic 41px turning shoulder shift."
        )

    baseline_after = shift_control_lower_band_y(
        regenerated["Frames/RightWalk/walk_01.png"][0],
        700,
        17,
    )
    try:
        validate_continuity_edge(
            "walking_shoe_baseline_shift_17px",
            walk_before,
            baseline_after,
            BoundaryKind.WALKING,
        )
    except RuntimeError as exception:
        if "walking shoe-baseline continuity" not in str(exception):
            raise RuntimeError(
                "Seventeen-pixel walking shoe-baseline control failed for "
                "the wrong reason."
            ) from exception
        print(
            "adversarial_control_ok=walking_shoe_baseline_shift_17px "
            f"rejected=true reason=walking_baseline_continuity "
            f"detail={exception}"
        )
    else:
        raise RuntimeError(
            "Validator accepted a synthetic 17px walking shoe-baseline "
            "shift."
        )


def validate_identity_and_continuity(
    regenerated: dict[str, tuple[np.ndarray, np.ndarray]],
) -> None:
    left_path = "Frames/LeftTurn/turn_0.png"
    right_path = "Frames/RightTurn/turn_0.png"
    left = regenerated[left_path][0]
    right = regenerated[right_path][0]
    if not np.array_equal(left, right):
        raise RuntimeError("Left and right front pixels are not identical.")
    if (ROOT / left_path).read_bytes() != (ROOT / right_path).read_bytes():
        raise RuntimeError("Left and right front PNG bytes are not identical.")

    shared_paths = (
        "Frames/CrossArm/cross_02.png",
        *(
            f"Frames/CrossArmRelease/release_{index:02d}.png"
            for index in range(6)
        ),
    )
    for relative_path in shared_paths:
        frame = regenerated[relative_path][0]
        validate_shared_stationary_plate(relative_path, frame, right)

    for boundary in continuity_boundaries():
        measured = validate_continuity_edge(
            boundary.label,
            regenerated[boundary.from_path][0],
            regenerated[boundary.to_path][0],
            boundary.kind,
        )
        print(
            f"boundary_ok={boundary.label} class={boundary.kind.value} "
            f"head={measured['head']:.2f} "
            f"torso={measured['torso']:.2f} "
            f"shoulder={measured['shoulder']:.2f} "
            f"waist={measured['waist']:.2f} "
            f"baseline={measured['baseline']:.2f} "
            f"height={measured['height']:.2f} "
            "lower_binary_symmetric_difference_union="
            f"{measured['silhouette']:.4f}"
        )

    validate_adversarial_continuity_controls(regenerated)

    print(
        "continuity_ok=boundaries:45 front_identity=bytes_and_pixels "
        f"shared_lower_plate=7 seam_y={STATIONARY_PLATE_SEAM_Y} "
        "stationary=head4 torso4 baseline2 "
        "lower_binary_symmetric_difference_union12pct "
        "moving=head6 torso14 shoulder10 waist10 baseline16 height18"
    )


def validate_viewport_fit_configuration() -> None:
    text = (ROOT / "project.godot").read_text(encoding="utf-8")
    expected = {
        "window/size/viewport_width": "1920",
        "window/size/viewport_height": "1080",
        "window/size/window_width_override": "1280",
        "window/size/window_height_override": "720",
        "window/stretch/mode": '"canvas_items"',
        "window/stretch/aspect": '"keep"',
    }
    for key, value in expected.items():
        matches = re.findall(
            rf"(?m)^{re.escape(key)}=(.+?)\r?$",
            text,
        )
        if matches != [value]:
            raise RuntimeError(
                f"project.godot must contain exactly {key}={value}; "
                f"found {matches}."
            )

    logical_width = 1920.0
    logical_height = 1080.0
    clients = (
        (1920, 1080),
        (1536, 960),
        (1536, 864),
        (1280, 720),
        (1000, 1000),
    )
    matrix = []
    for client_width, client_height in clients:
        scale = min(
            client_width / logical_width,
            client_height / logical_height,
        )
        fitted_width = logical_width * scale
        fitted_height = logical_height * scale
        offset_x = (client_width - fitted_width) / 2.0
        offset_y = (client_height - fitted_height) / 2.0
        if (
            scale <= 0.0
            or offset_x < -1e-6
            or offset_y < -1e-6
            or fitted_width > client_width + 1e-6
            or fitted_height > client_height + 1e-6
        ):
            raise RuntimeError(
                "Viewport fit matrix produced clipping for "
                f"{client_width}x{client_height}."
            )
        matrix.append(
            f"{client_width}x{client_height}:"
            f"scale={scale:.6f},offset={offset_x:.1f},{offset_y:.1f}"
        )
    print(
        "viewport_fit_ok=logical:1920x1080 override:1280x720 "
        "mode:canvas_items aspect:keep matrix="
        + ";".join(matrix)
    )


def preset_zero_assignment_section(text: str) -> str:
    match = re.search(
        r"(?ms)^\[preset\.0\]\r?\n(.*?)(?=^\[|\Z)",
        text,
    )
    if match is None:
        raise RuntimeError("Export preset 0 assignment section is missing.")
    return match.group(1)


def validate_empty_export_filters(text: str) -> None:
    section = preset_zero_assignment_section(text)
    for name in ("include_filter", "exclude_filter"):
        matches = re.findall(
            rf'(?m)^{re.escape(name)}="([^"]*)"\r?$',
            section,
        )
        if len(matches) != 1:
            raise RuntimeError(
                f"Export preset must contain exactly one {name} assignment."
            )
        if matches[0] != "":
            raise RuntimeError(f"Export preset {name} must be empty.")


def validate_export_filter_negative_controls(text: str) -> None:
    controls = (
        (
            "nonempty_include",
            text.replace(
                'include_filter=""',
                'include_filter="*.png"',
                1,
            ),
        ),
        (
            "nonempty_exclude",
            text.replace(
                'exclude_filter=""',
                'exclude_filter="*.cs"',
                1,
            ),
        ),
        (
            "duplicate_include",
            text.replace(
                'include_filter=""',
                'include_filter=""\ninclude_filter=""',
                1,
            ),
        ),
        (
            "duplicate_exclude",
            text.replace(
                'exclude_filter=""',
                'exclude_filter=""\nexclude_filter=""',
                1,
            ),
        ),
    )
    for name, control in controls:
        try:
            validate_empty_export_filters(control)
        except RuntimeError:
            print(f"export_filter_negative_control={name} rejected=true")
        else:
            raise RuntimeError(
                f"Export-filter negative control was accepted: {name}."
            )


def parse_export_resources() -> tuple[str, ...]:
    text = (ROOT / "export_presets.cfg").read_text(encoding="utf-8")
    validate_empty_export_filters(text)
    if 'export_filter="resources"' not in text:
        raise RuntimeError("Export preset must use selected resources.")
    if 'export_filter="all_resources"' in text:
        raise RuntimeError("Export preset still uses all_resources.")
    match = re.search(
        r"export_files=PackedStringArray\((.*?)\)",
        text,
        flags=re.DOTALL,
    )
    if match is None:
        raise RuntimeError("Export preset has no selected resource list.")
    resources = tuple(re.findall(r'"([^"]+)"', match.group(1)))
    if resources != EXPECTED_EXPORT_RESOURCES:
        raise RuntimeError(
            "Export whitelist differs from the exact release resource order."
        )
    if len(resources) != len(set(resources)):
        raise RuntimeError("Export whitelist contains duplicates.")
    return resources


def validate_release_stage_inventory() -> None:
    manifest_path = ROOT / "release-stage-manifest.txt"
    manifest_bytes = manifest_path.read_bytes()
    if manifest_bytes.startswith(b"\xef\xbb\xbf"):
        raise RuntimeError(
            "release-stage-manifest.txt must not contain a UTF-8 BOM."
        )
    try:
        manifest_entries = tuple(
            manifest_bytes.decode("utf-8").splitlines()
        )
    except UnicodeDecodeError as exception:
        raise RuntimeError(
            "release-stage-manifest.txt must be valid UTF-8."
        ) from exception
    if manifest_entries != EXPECTED_STAGE_ENTRIES:
        raise RuntimeError(
            "Release stage manifest differs from the exact 134-entry "
            "validator contract."
        )
    if len(manifest_entries) != len(set(manifest_entries)):
        raise RuntimeError("Release stage manifest contains duplicates.")

    tooling_text = (ROOT / "release-tooling.ps1").read_text(
        encoding="utf-8"
    )
    tooling_match = re.search(
        r"function Get-FrozenReleaseStageEntries\s*\{\s*return @\("
        r"(.*?)\)\s*\}",
        tooling_text,
        flags=re.DOTALL,
    )
    if tooling_match is None:
        raise RuntimeError(
            "release-tooling.ps1 has no frozen stage-entry function."
        )
    tooling_entries = tuple(
        re.findall(r'"([^"]+)"', tooling_match.group(1))
    )
    if tooling_entries != EXPECTED_STAGE_ENTRIES:
        raise RuntimeError(
            "release-tooling.ps1 frozen stage inventory differs from the "
            "exact 134-entry validator contract."
        )
    if len(tooling_entries) != len(set(tooling_entries)):
        raise RuntimeError(
            "release-tooling.ps1 frozen stage inventory contains duplicates."
        )

    denied_tokens = (
        "WalkingGirlFrames.png",
        "extract_walking_girl.py",
        "extract_walking_girl",
        "FrameExtraction",
        "ControllerProbe",
        ".ai-org",
        "Build",
        "README",
    )
    denied = [
        entry
        for entry in manifest_entries
        if any(token.casefold() in entry.casefold() for token in denied_tokens)
    ]
    if denied:
        raise RuntimeError(
            f"Release stage inventory contains denied entries: {denied}."
        )
    print(
        "release_stage_inventory_ok=manifest_entries:134 "
        "tooling_entries:134 duplicates:false raw_girl_source:false "
        "girl_extractor:false"
    )


def validate_export_hygiene() -> None:
    resources = parse_export_resources()
    preset_text = (ROOT / "export_presets.cfg").read_text(encoding="utf-8")
    validate_export_filter_negative_controls(preset_text)
    required_release_options = (
        "dotnet/include_scripts_content=false",
        "dotnet/include_debug_symbols=false",
    )
    missing_release_options = [
        option
        for option in required_release_options
        if option not in preset_text
    ]
    if missing_release_options:
        raise RuntimeError(
            "Export preset is missing release hygiene options: "
            f"{missing_release_options}."
        )
    deny_tokens = (
        "Waving",
        "WalkingGirlFrames.png",
        "extract_walking_girl.py",
        "extract_walking_girl",
        "ControllerProbe",
        "FrameExtraction",
        "LTurning.png",
        "RTurning.png",
        "RWalking2.png",
        "Clapping2.png",
        "CrossArm3.png",
        "CrossArm4.png",
        ".ai-org",
        "Build",
        "README",
    )
    denied = [
        resource
        for resource in resources
        if any(token.casefold() in resource.casefold() for token in deny_tokens)
    ]
    if denied:
        raise RuntimeError(f"Denied resources are whitelisted: {denied}.")
    root_runtime_images = [
        resource
        for resource in resources
        if resource.lower().endswith((".png", ".jpg", ".jpeg"))
        and not resource.startswith("res://Frames/")
    ]
    if root_runtime_images:
        raise RuntimeError(
            f"Unrelated root images are whitelisted: {root_runtime_images}."
        )
    print(
        f"export_whitelist_ok=selected_resources count={len(resources)} "
        "denied_development_assets=false source_content=false "
        "debug_symbols=false"
    )


def logical_requirement_lines(text: str) -> tuple[str, ...]:
    logical = []
    pending = ""
    for physical in text.splitlines():
        stripped = physical.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if stripped.endswith("\\"):
            pending += stripped[:-1].rstrip() + " "
            continue
        logical.append((pending + stripped).strip())
        pending = ""
    if pending:
        raise RuntimeError("requirements.txt has an incomplete continuation.")
    return tuple(logical)


def validate_requirements_lock() -> None:
    path = ROOT / "requirements.txt"
    text = path.read_text(encoding="utf-8")
    lines = logical_requirement_lines(text)
    index_directive = f"--index-url {APPROVED_REQUIREMENTS_INDEX}"
    if lines.count(index_directive) != 1:
        raise RuntimeError(
            "requirements.txt must contain one approved Microsoft PyPI "
            "proxy index."
        )
    if lines.count("--only-binary=:all:") != 1:
        raise RuntimeError(
            "requirements.txt must require binary distributions."
        )
    directives = tuple(line for line in lines if line.startswith("-"))
    if set(directives) != {
        index_directive,
        "--only-binary=:all:",
    } or len(directives) != 2:
        raise RuntimeError(
            f"requirements.txt contains unapproved directives: {directives}."
        )

    requirement_pattern = re.compile(
        r"^([A-Za-z0-9][A-Za-z0-9._-]*)==([A-Za-z0-9][A-Za-z0-9._-]*) "
        r"--hash=sha256:([0-9a-f]{64})$"
    )
    actual = {}
    for line in lines:
        if line.startswith("-"):
            continue
        match = requirement_pattern.fullmatch(line)
        if match is None:
            raise RuntimeError(
                f"requirements.txt contains an unapproved requirement: {line}."
            )
        name, version, digest = match.groups()
        if name in actual:
            raise RuntimeError(
                f"requirements.txt contains duplicate package {name}."
            )
        actual[name] = (version, digest)
    if actual != EXPECTED_REQUIREMENTS:
        raise RuntimeError(
            "requirements.txt package/version/hash set differs from the "
            f"approved CPython 3.13 Windows x86-64 lock: {actual}."
        )
    print(
        "requirements_lock_ok=index=microsoft_proxy binary_only=true "
        "packages=3 "
        "python=cp313 platform=win_amd64 hashes=required"
    )


def read_exact(stream, size: int) -> bytes:
    data = stream.read(size)
    if len(data) != size:
        raise RuntimeError("Built pack ended before its manifest was complete.")
    return data


def read_u32(stream) -> int:
    return struct.unpack("<I", read_exact(stream, 4))[0]


def read_u64(stream) -> int:
    return struct.unpack("<Q", read_exact(stream, 8))[0]


def locate_pack(path: Path) -> tuple[int, int]:
    file_size = path.stat().st_size
    if file_size < 4:
        raise RuntimeError(f"{path.name} is too small to contain a Godot pack.")
    with path.open("rb") as stream:
        if read_u32(stream) == PCK_MAGIC:
            return (0, file_size)
        if file_size < 12:
            raise RuntimeError(f"{path.name} has no embedded Godot pack trailer.")
        stream.seek(file_size - 12)
        pack_size = read_u64(stream)
        trailer_magic = read_u32(stream)
    if trailer_magic != PCK_MAGIC:
        raise RuntimeError(f"{path.name} has no embedded Godot pack trailer.")
    pack_start = file_size - pack_size - 12
    if pack_start < 0:
        raise RuntimeError(f"{path.name} has an invalid embedded pack size.")
    return (pack_start, pack_size)


def validate_pack_entry_bounds(
    pack_start: int,
    pack_size: int,
    entry: PackEntry,
    maximum_size: int,
) -> None:
    if pack_start < 0 or pack_size < 0 or maximum_size <= 0:
        raise RuntimeError("Pack bounds are invalid.")
    if entry.size > maximum_size:
        raise RuntimeError(
            f"Pack metadata entry is {entry.size} bytes; "
            f"maximum is {maximum_size}."
        )
    pack_end = pack_start + pack_size
    entry_end = entry.data_offset + entry.size
    if (
        entry.data_offset < pack_start
        or entry.size < 0
        or entry_end < entry.data_offset
        or entry_end > pack_end
    ):
        raise RuntimeError("Pack entry range is outside the containing pack.")


def read_pack_entry(
    path: Path,
    pack_start: int,
    pack_size: int,
    entry: PackEntry,
    maximum_size: int = MAXIMUM_METADATA_ENTRY_SIZE,
) -> bytes:
    validate_pack_entry_bounds(
        pack_start,
        pack_size,
        entry,
        maximum_size,
    )
    with path.open("rb") as stream:
        stream.seek(entry.data_offset)
        return read_exact(stream, entry.size)


def read_pack_manifest(path: Path) -> dict[str, PackEntry]:
    pack_start, pack_size = locate_pack(path)
    pack_end = pack_start + pack_size
    with path.open("rb") as stream:
        stream.seek(pack_start)
        if read_u32(stream) != PCK_MAGIC:
            raise RuntimeError(f"{path.name} pack header magic is invalid.")
        pack_version = read_u32(stream)
        engine_version = (
            read_u32(stream),
            read_u32(stream),
            read_u32(stream),
        )
        pack_flags = read_u32(stream)
        read_u64(stream)
        if pack_flags & PCK_DIRECTORY_ENCRYPTED:
            raise RuntimeError(
                f"{path.name} has an encrypted manifest that cannot be audited."
            )
        if pack_version >= 3:
            directory_offset = read_u64(stream)
            directory_position = pack_start + directory_offset
            if not pack_start <= directory_position < pack_end:
                raise RuntimeError(
                    f"{path.name} has an invalid manifest offset."
                )
            stream.seek(directory_position)
        else:
            read_exact(stream, 16 * 4)

        file_count = read_u32(stream)
        if file_count > 10_000:
            raise RuntimeError(
                f"{path.name} manifest count is unreasonable: {file_count}."
            )
        entries = {}
        for _ in range(file_count):
            path_length = read_u32(stream)
            if path_length == 0 or path_length > 16_384:
                raise RuntimeError(
                    f"{path.name} has an invalid manifest path length."
                )
            raw_path = read_exact(stream, path_length)
            try:
                entry = raw_path.rstrip(b"\0").decode("utf-8")
            except UnicodeDecodeError as exception:
                raise RuntimeError(
                    f"{path.name} has a non-UTF-8 manifest path."
                ) from exception
            if not entry:
                raise RuntimeError(f"{path.name} has an empty manifest path.")
            stored_offset = read_u64(stream)
            entry_size = read_u64(stream)
            read_exact(stream, 16)
            read_u32(stream)
            if entry in entries:
                raise RuntimeError(
                    f"{path.name} manifest contains duplicate entry {entry}."
                )
            absolute_offset = pack_start + stored_offset
            descriptor = PackEntry(absolute_offset, entry_size)
            validate_pack_entry_bounds(
                pack_start,
                pack_size,
                descriptor,
                pack_size,
            )
            entries[entry] = descriptor

    print(
        f"pack_manifest_read={path.name} pack_version={pack_version} "
        f"engine={engine_version[0]}.{engine_version[1]}.{engine_version[2]} "
        f"entries={len(entries)}"
    )
    return entries


def expected_imported_textures() -> set[str]:
    imported = set()
    for relative_path in EXPECTED_TEXTURE_PATHS:
        import_path = ROOT / f"{relative_path}.import"
        if not import_path.is_file():
            raise RuntimeError(
                f"{relative_path}.import is missing; run the authenticated "
                "Godot import before validating built artifacts."
            )
        text = import_path.read_text(encoding="utf-8")
        match = re.search(
            r'^path="res://(\.godot/imported/[^"]+\.ctex)"$',
            text,
            flags=re.MULTILINE,
        )
        if match is None:
            raise RuntimeError(
                f"{relative_path}.import has no exported texture path."
            )
        imported.add(match.group(1))
    if len(imported) != len(EXPECTED_TEXTURE_PATHS):
        raise RuntimeError("Imported texture paths are not one-to-one.")
    return imported


def denied_metadata_tokens(payload: bytes) -> tuple[str, ...]:
    tokens = (
        "Waving",
        "WalkingGirlFrames.png",
        "extract_walking_girl.py",
        "extract_walking_girl",
        "Avatar.jpg",
        "Clapping.png",
        "CrossArm.png",
        "CrossArm2.png",
        "WalkRightSheet.png",
        "LTurning.png",
        "RTurning.png",
        "RWalking2.png",
        "Clapping2.png",
        "CrossArm3.png",
        "CrossArm4.png",
        "ControllerProbe/",
        "FrameExtraction/",
        ".ai-org/",
        ".tools/",
        ".vs/",
        "Build/",
        "Captures/",
        "README.md",
        "requirements.txt",
        "run-animation",
        "export-release",
    )
    normalized_single = payload.replace(b"\\", b"/").lower()
    normalized_utf16 = payload.replace(b"\\\x00", b"/\x00").lower()
    matches = []
    for token in tokens:
        normalized_token = token.replace("\\", "/")
        if (
            normalized_token.encode("utf-8").lower() in normalized_single
            or normalized_token.encode("utf-16-le").lower()
            in normalized_utf16
        ):
            matches.append(token)
    return tuple(matches)


def validate_metadata_descriptors(
    entries: dict[str, PackEntry],
    metadata_names: set[str],
) -> None:
    aggregate = sum(entries[name].size for name in metadata_names)
    if aggregate > MAXIMUM_METADATA_AGGREGATE_SIZE:
        raise RuntimeError(
            f"Pack metadata aggregate is {aggregate} bytes; maximum is "
            f"{MAXIMUM_METADATA_AGGREGATE_SIZE}."
        )
    oversized = sorted(
        name
        for name in metadata_names
        if entries[name].size > MAXIMUM_METADATA_ENTRY_SIZE
    )
    if oversized:
        raise RuntimeError(
            f"Pack metadata entries exceed 4 MiB: {oversized}."
        )


def validate_pack_metadata(
    path: Path,
    entries: dict[str, PackEntry],
    metadata_names: set[str],
) -> None:
    validate_metadata_descriptors(entries, metadata_names)
    pack_start, pack_size = locate_pack(path)
    denied = {}
    for name in sorted(metadata_names):
        payload = read_pack_entry(
            path,
            pack_start,
            pack_size,
            entries[name],
        )
        matches = denied_metadata_tokens(payload)
        if matches:
            denied[name] = matches
    if denied:
        raise RuntimeError(
            f"{path.name} metadata contains denied inventory tokens: {denied}."
        )


def validate_pack_negative_controls() -> None:
    for name, payload in (
        ("ascii_waving", b"res://Waving.png"),
        (
            "ascii_walking_girl_source",
            b"res://WalkingGirlFrames.png",
        ),
        (
            "ascii_walking_girl_extractor",
            b"FrameExtraction/extract_walking_girl.py",
        ),
        (
            "utf16_development_path",
            (
                r"C:\repo\TCFAnimation\FrameExtraction\validate_release.py"
            ).encode("utf-16-le"),
        ),
    ):
        if not denied_metadata_tokens(payload):
            raise RuntimeError(
                f"Metadata token negative control was accepted: {name}."
            )
        print(f"metadata_negative_control={name} rejected=true")

    try:
        validate_pack_entry_bounds(
            100,
            1024,
            PackEntry(100, MAXIMUM_METADATA_ENTRY_SIZE + 1),
            MAXIMUM_METADATA_ENTRY_SIZE,
        )
    except RuntimeError:
        print("metadata_negative_control=entry_4mib_plus_1 rejected=true")
    else:
        raise RuntimeError("Oversized metadata entry control was accepted.")

    aggregate_entries = {
        f"metadata_{index}": PackEntry(0, 4 * 1024 * 1024)
        for index in range(4)
    }
    aggregate_entries["metadata_4"] = PackEntry(0, 1)
    try:
        validate_metadata_descriptors(
            aggregate_entries,
            set(aggregate_entries),
        )
    except RuntimeError:
        print("metadata_negative_control=aggregate_16mib_plus_1 rejected=true")
    else:
        raise RuntimeError("Oversized metadata aggregate control was accepted.")

    try:
        validate_pack_entry_bounds(
            100,
            100,
            PackEntry(199, 2),
            MAXIMUM_METADATA_ENTRY_SIZE,
        )
    except RuntimeError:
        print("metadata_negative_control=out_of_pack rejected=true")
    else:
        raise RuntimeError("Out-of-pack descriptor control was accepted.")


def validate_pack_payload(
    path: Path,
    entries: dict[str, PackEntry],
) -> None:
    engine_metadata = {
        ".godot/global_script_class_cache.cfg",
        ".godot/uid_cache.bin",
        "Main.tscn.remap",
        "project.binary",
    }
    imported_textures = expected_imported_textures()
    import_metadata = {
        f"{relative_path}.import"
        for relative_path in EXPECTED_TEXTURE_PATHS
    }
    scene_payloads = {
        entry
        for entry in entries
        if re.fullmatch(
            r"\.godot/exported/\d+/export-[0-9a-f]+-Main\.scn",
            entry,
        )
    }
    if len(scene_payloads) != 1:
        raise RuntimeError(
            f"{path.name} must contain exactly one exported Main scene."
        )
    expected_entries = (
        engine_metadata
        | imported_textures
        | import_metadata
        | scene_payloads
        | set(EXPECTED_SCRIPT_PAYLOADS)
        | {"ActionMessages.json", "AnimationConfig.json"}
    )
    actual_entries = set(entries)
    denied_entries = sorted(actual_entries - expected_entries)
    missing_entries = sorted(expected_entries - actual_entries)
    if denied_entries or missing_entries:
        raise RuntimeError(
            f"{path.name} payload mismatch; denied={denied_entries} "
            f"missing={missing_entries}."
        )

    deny_tokens = (
        "Waving",
        "WalkingGirlFrames.png",
        "extract_walking_girl.py",
        "extract_walking_girl",
        "ControllerProbe",
        "FrameExtraction",
        ".ai-org",
        "README",
        "Build/",
        "LTurning.png",
        "RTurning.png",
        "RWalking2.png",
        "Clapping2.png",
        "CrossArm3.png",
        "CrossArm4.png",
    )
    included_school_sources = sorted(
        source_name
        for source_name in (
            f"School{school_number}.png"
            for school_number in range(1, 7)
        )
        if source_name in entries
    )
    if included_school_sources:
        raise RuntimeError(
            f"{path.name} contains root school source images: "
            f"{included_school_sources}."
        )
    denied_payloads = [
        entry
        for entry in entries
        if entry not in engine_metadata
        and any(token.casefold() in entry.casefold() for token in deny_tokens)
    ]
    if denied_payloads:
        raise RuntimeError(
            f"{path.name} contains denied payload entries: {denied_payloads}."
        )
    source_scripts = {
        entry: entries[entry].size
        for entry in EXPECTED_SCRIPT_PAYLOADS
        if entries[entry].size > 1
    }
    if source_scripts:
        raise RuntimeError(
            f"{path.name} contains C# source script content: {source_scripts}."
        )
    validate_pack_metadata(
        path,
        entries,
        engine_metadata | import_metadata,
    )
    print(
        f"pack_payload_ok={path.name} runtime_scene=1 runtime_scripts=2 "
        "runtime_json=2 "
        "runtime_textures=86 import_metadata=86 engine_metadata=4 "
        "denied_payloads=false source_content=false metadata_denied_tokens=0"
    )


def validate_managed_release_payload(build_root: Path) -> None:
    pdbs = sorted(
        path.relative_to(ROOT).as_posix()
        for path in build_root.rglob("*.pdb")
        if path.is_file()
    )
    if pdbs:
        raise RuntimeError(f"Release contains debug symbols: {pdbs}.")

    project_assemblies = sorted(
        path
        for path in build_root.rglob("TCFAnimation.dll")
        if path.is_file()
    )
    if not project_assemblies:
        raise RuntimeError("Release has no TCFAnimation.dll payload.")
    absolute_root = str(ROOT)
    path_needles = (
        absolute_root.encode("utf-8"),
        absolute_root.encode("utf-16-le"),
    )
    leaking = []
    for assembly in project_assemblies:
        payload = assembly.read_bytes()
        if any(needle in payload for needle in path_needles):
            leaking.append(assembly.relative_to(ROOT).as_posix())
    if leaking:
        raise RuntimeError(
            f"Release assemblies contain absolute project paths: {leaking}."
        )
    print(
        "managed_payload_ok=pdb_absent absolute_project_paths=false "
        f"project_assemblies={len(project_assemblies)}"
    )


def validate_built_artifacts() -> None:
    build_root = ROOT / "Build"
    pack_artifacts = tuple(
        path
        for path in (
            build_root / "TCFAnimation.exe",
            build_root / "TCFAnimation.pck",
        )
        if path.is_file()
    )
    if not pack_artifacts:
        print("artifact_validation=skipped reason=no_built_exe_or_pck")
        return

    manifests = {}
    for artifact in pack_artifacts:
        manifest = read_pack_manifest(artifact)
        validate_pack_payload(artifact, manifest)
        manifests[artifact.name] = manifest
    if len(manifests) > 1:
        manifest_sets = {
            frozenset(manifest)
            for manifest in manifests.values()
        }
        if len(manifest_sets) != 1:
            raise RuntimeError(
                "Built executable and standalone PCK manifests differ."
            )
    validate_managed_release_payload(build_root)
    print(
        f"artifact_validation=pass artifacts={len(pack_artifacts)} "
        "manifest_only=true extraction_artifacts=false"
    )


def main() -> int:
    validate_extractor_segmentation_contract()
    validate_sources()
    validate_walking_girl_source_geometry()
    validate_walking_girl_extractor_contract()
    validate_frame_tree()
    validate_inventory_counts()
    validate_walking_girl_frames()
    validate_animation_config()
    validate_girl_geometry_contract()
    validate_action_messages()
    validate_logo_effect()
    validate_neon_background()
    validate_runtime_pngs()
    regenerated = regenerate_in_memory()
    validate_independent_lower_anatomy(regenerated)
    validate_generated_contracts(regenerated)
    validate_school_runtime_assets(regenerated)
    validate_viewport_fit_configuration()
    validate_identity_and_continuity(regenerated)
    validate_release_stage_inventory()
    validate_export_hygiene()
    validate_requirements_lock()
    validate_pack_negative_controls()
    validate_built_artifacts()
    validate_sources()
    print(
        "ASSET_RELEASE_CHECK_PASS frames=33 girl_frames=12 "
        "school_overlays=33 backgrounds=6 "
        f"sources={len(SOURCE_CONTRACTS)} "
        f"character_textures={EXPECTED_CHARACTER_TEXTURE_COUNT} "
        f"runtime_textures={len(EXPECTED_TEXTURE_PATHS)} read_only=true "
        f"export_resources={len(EXPECTED_EXPORT_RESOURCES)} "
        f"stage_entries={len(EXPECTED_STAGE_ENTRIES)} "
        "girl_bounds=108,57,406,840 girl_offscreen_left=-187.5 "
        "artifact_manifest=checked_if_present"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

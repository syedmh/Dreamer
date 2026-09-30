from __future__ import annotations

import ctypes
import hashlib
import json
import subprocess
import time
from ctypes import wintypes
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


EXE = Path(r"C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build\TCFAnimation.exe")
PROJECT = EXE.parent.parent
OUT = Path(
    r"C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions"
    r"\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final"
    r"\runtime-highfps"
)

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

PW_RENDERFULLCONTENT = 2
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_CLOSE = 0x0010


class RECT(ctypes.Structure):
    _fields_ = [
        ("left", wintypes.LONG),
        ("top", wintypes.LONG),
        ("right", wintypes.LONG),
        ("bottom", wintypes.LONG),
    ]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def find_window(pid: int, timeout: float = 15.0) -> int:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        found: list[int] = []

        @EnumWindowsProc
        def callback(hwnd: int, _lparam: int) -> bool:
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(hwnd):
                found.append(hwnd)
                return False
            return True

        user32.EnumWindows(callback, 0)
        if found:
            return found[0]
        time.sleep(0.05)
    raise RuntimeError(f"No visible HWND for pid {pid}")


def key_lparam(scan: int, up: bool, extended: bool = False) -> int:
    value = 1 | (scan << 16)
    if extended:
        value |= 0x01000000
    if up:
        value |= 0xC0000000
    return value


def key_press(hwnd: int, vk: int, scan: int, extended: bool = False) -> None:
    user32.PostMessageW(hwnd, WM_KEYDOWN, vk, key_lparam(scan, False, extended))
    time.sleep(0.01)
    user32.PostMessageW(hwnd, WM_KEYUP, vk, key_lparam(scan, True, extended))


class WindowCapture:
    def __init__(self, hwnd: int):
        self.hwnd = hwnd
        rect = RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
            raise ctypes.WinError(ctypes.get_last_error())
        self.width = rect.right - rect.left
        self.height = rect.bottom - rect.top
        self.window_dc = user32.GetWindowDC(hwnd)
        self.memory_dc = gdi32.CreateCompatibleDC(self.window_dc)
        self.bitmap = gdi32.CreateCompatibleBitmap(
            self.window_dc, self.width, self.height
        )
        self.previous = gdi32.SelectObject(self.memory_dc, self.bitmap)
        self.info = BITMAPINFO()
        self.info.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        self.info.bmiHeader.biWidth = self.width
        self.info.bmiHeader.biHeight = -self.height
        self.info.bmiHeader.biPlanes = 1
        self.info.bmiHeader.biBitCount = 32
        self.info.bmiHeader.biCompression = 0
        self.buffer = ctypes.create_string_buffer(self.width * self.height * 4)

    def grab(self) -> np.ndarray:
        if not user32.PrintWindow(self.hwnd, self.memory_dc, PW_RENDERFULLCONTENT):
            raise ctypes.WinError(ctypes.get_last_error())
        lines = gdi32.GetDIBits(
            self.memory_dc,
            self.bitmap,
            0,
            self.height,
            self.buffer,
            ctypes.byref(self.info),
            0,
        )
        if lines != self.height:
            raise RuntimeError(f"GetDIBits returned {lines}/{self.height}")
        bgra = np.frombuffer(self.buffer, dtype=np.uint8).reshape(
            self.height, self.width, 4
        )
        return bgra[:, :, [2, 1, 0]].copy()

    def close(self) -> None:
        gdi32.SelectObject(self.memory_dc, self.previous)
        gdi32.DeleteObject(self.bitmap)
        gdi32.DeleteDC(self.memory_dc)
        user32.ReleaseDC(self.hwnd, self.window_dc)


def record_changes(
    scenario: str,
    trigger,
    duration: float,
    interval: float = 0.025,
) -> dict[str, object]:
    process = subprocess.Popen(
        [str(EXE)],
        cwd=PROJECT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    hwnd = find_window(process.pid)
    time.sleep(2.5)
    capture = WindowCapture(hwnd)
    baseline = capture.grab()
    trigger(hwnd)
    start = time.perf_counter()
    next_sample = 0.0
    samples = 0
    changes: list[dict[str, object]] = []
    last_digest: str | None = None
    while True:
        now = time.perf_counter() - start
        if now > duration:
            break
        if now < next_sample:
            time.sleep(next_sample - now)
        image = capture.grab()
        samples += 1
        content = image[38:, :, :]
        digest = hashlib.sha256(content.tobytes()).hexdigest()[:16]
        if digest != last_digest:
            change_index = len(changes)
            path = OUT / f"{scenario}-change-{change_index:02d}.png"
            Image.fromarray(image).save(path)
            changes.append(
                {
                    "index": change_index,
                    "time_ms": round((time.perf_counter() - start) * 1000, 1),
                    "sha256_16": digest,
                    "path": path.name,
                }
            )
            last_digest = digest
        next_sample += interval
    user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
    try:
        stdout, stderr = process.communicate(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        stdout, stderr = process.communicate()
    capture.close()
    return {
        "scenario": scenario,
        "samples": samples,
        "changes": changes,
        "baseline_sha256_16": hashlib.sha256(baseline.tobytes()).hexdigest()[:16],
        "exit": process.returncode,
        "stdout": stdout.strip(),
        "stderr": stderr.strip(),
    }


def contact_sheet(scenario: str, result: dict[str, object]) -> None:
    paths = [OUT / item["path"] for item in result["changes"]]
    tiles = []
    for item, path in zip(result["changes"], paths):
        image = Image.open(path).convert("RGB")
        image.thumbnail((480, 300), Image.Resampling.LANCZOS)
        tile = Image.new("RGB", (500, 340), (45, 45, 45))
        tile.paste(image, ((500 - image.width) // 2, 34))
        ImageDraw.Draw(tile).text(
            (8, 10),
            f"change {item['index']:02d} at {item['time_ms']} ms",
            fill="white",
            font=ImageFont.load_default(),
        )
        tiles.append(tile)
    columns = 3
    rows = (len(tiles) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * 500, rows * 340), (80, 80, 80))
    for index, tile in enumerate(tiles):
        sheet.paste(tile, ((index % columns) * 500, (index // columns) * 340))
    sheet.save(OUT / f"{scenario}-changes-contact.png")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    results = []
    results.append(
        record_changes(
            "clap",
            lambda hwnd: key_press(hwnd, 0x43, 0x2E),
            duration=2.15,
        )
    )

    def cross_trigger(hwnd: int) -> None:
        key_press(hwnd, 0x58, 0x2D)

    crossing = record_changes("cross-entry", cross_trigger, duration=0.65)
    results.append(crossing)

    def release_trigger(hwnd: int) -> None:
        key_press(hwnd, 0x58, 0x2D)
        time.sleep(0.65)
        key_press(hwnd, 0x58, 0x2D)

    results.append(
        record_changes("cross-release", release_trigger, duration=1.65)
    )

    for result in results:
        contact_sheet(result["scenario"], result)
    (OUT / "highfps-results.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8"
    )
    print(
        "HIGHFPS_CAPTURE_COMPLETE "
        + " ".join(
            f"{item['scenario']}_samples={item['samples']}_changes={len(item['changes'])}"
            for item in results
        )
    )


if __name__ == "__main__":
    main()

from __future__ import annotations

import ctypes
import hashlib
import json
import subprocess
import time
from ctypes import wintypes
from pathlib import Path

import numpy as np
from PIL import Image, ImageGrab


EXE = Path(r"C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build\TCFAnimation.exe")
OUT = Path(__file__).resolve().parent

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

# Per-monitor-v2 DPI awareness prevents GetClientRect/ClientToScreen coordinates
# from being virtualized at the host's 125% scale while ImageGrab uses physical
# desktop pixels.
DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = ctypes.c_void_p(-4)
user32.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)

SW_RESTORE = 9
WM_CLOSE = 0x0010
PW_RENDERFULLCONTENT = 0x00000002
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0
BI_RGB = 0

INPUT_KEYBOARD = 1
KEYEVENTF_EXTENDEDKEY = 0x0001
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_SCANCODE = 0x0008
SC_LEFT = 0x4B
SC_ENTER = 0x1C


class RECT(ctypes.Structure):
    _fields_ = [
        ("left", wintypes.LONG),
        ("top", wintypes.LONG),
        ("right", wintypes.LONG),
        ("bottom", wintypes.LONG),
    ]


class POINT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]


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


ULONG_PTR = ctypes.c_ulonglong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_ulong


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wintypes.WORD),
        ("wScan", wintypes.WORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wintypes.LONG),
        ("dy", wintypes.LONG),
        ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [
        ("uMsg", wintypes.DWORD),
        ("wParamL", wintypes.WORD),
        ("wParamH", wintypes.WORD),
    ]


class INPUT_UNION(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _anonymous_ = ("union",)
    _fields_ = [("type", wintypes.DWORD), ("union", INPUT_UNION)]


EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.SendInput.argtypes = (wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int)
user32.SendInput.restype = wintypes.UINT


def find_window(pid: int) -> int:
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        found: list[int] = []

        @EnumWindowsProc
        def callback(hwnd: int, _lparam: int) -> bool:
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(hwnd):
                found.append(int(hwnd))
                return False
            return True

        user32.EnumWindows(callback, 0)
        if found:
            return found[0]
        time.sleep(0.05)
    raise RuntimeError(f"No visible HWND for pid {pid}")


def pid_for(hwnd: int) -> int:
    owner = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
    return int(owner.value)


def focus(hwnd: int, pid: int) -> None:
    user32.ShowWindow(hwnd, SW_RESTORE)
    current_thread = kernel32.GetCurrentThreadId()
    foreground = user32.GetForegroundWindow()
    foreground_thread = user32.GetWindowThreadProcessId(foreground, None)
    target_thread = user32.GetWindowThreadProcessId(hwnd, None)
    attached_foreground = bool(
        foreground_thread
        and foreground_thread != current_thread
        and user32.AttachThreadInput(current_thread, foreground_thread, True)
    )
    attached_target = bool(
        target_thread
        and target_thread != current_thread
        and target_thread != foreground_thread
        and user32.AttachThreadInput(current_thread, target_thread, True)
    )
    try:
        user32.BringWindowToTop(hwnd)
        user32.SetActiveWindow(hwnd)
        user32.SetFocus(hwnd)
        user32.SetForegroundWindow(hwnd)
    finally:
        if attached_target:
            user32.AttachThreadInput(current_thread, target_thread, False)
        if attached_foreground:
            user32.AttachThreadInput(current_thread, foreground_thread, False)
    time.sleep(0.3)
    foreground = int(user32.GetForegroundWindow())
    if foreground != hwnd or pid_for(foreground) != pid:
        raise RuntimeError(
            f"Foreground failed expected pid={pid} hwnd={hwnd}; "
            f"actual pid={pid_for(foreground) if foreground else 0} hwnd={foreground}"
        )


def send_scan(scan: int, down: bool, extended: bool = False) -> None:
    flags = KEYEVENTF_SCANCODE
    if not down:
        flags |= KEYEVENTF_KEYUP
    if extended:
        flags |= KEYEVENTF_EXTENDEDKEY
    item = INPUT(
        type=INPUT_KEYBOARD,
        ki=KEYBDINPUT(0, scan, flags, 0, 0),
    )
    if user32.SendInput(1, ctypes.byref(item), ctypes.sizeof(INPUT)) != 1:
        raise ctypes.WinError(ctypes.get_last_error())


def tap_scan(scan: int, extended: bool = False) -> None:
    send_scan(scan, True, extended)
    time.sleep(0.04)
    send_scan(scan, False, extended)


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise ctypes.WinError(ctypes.get_last_error())
    return rect.left, rect.top, rect.right, rect.bottom


def client_rect_screen(hwnd: int) -> tuple[int, int, int, int]:
    rect = RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(rect)):
        raise ctypes.WinError(ctypes.get_last_error())
    origin = POINT(0, 0)
    if not user32.ClientToScreen(hwnd, ctypes.byref(origin)):
        raise ctypes.WinError(ctypes.get_last_error())
    return origin.x, origin.y, origin.x + rect.right, origin.y + rect.bottom


def dib_capture(dc: int, x: int, y: int, width: int, height: int) -> np.ndarray:
    memory_dc = gdi32.CreateCompatibleDC(dc)
    bitmap = gdi32.CreateCompatibleBitmap(dc, width, height)
    previous = gdi32.SelectObject(memory_dc, bitmap)
    try:
        if not gdi32.BitBlt(memory_dc, 0, 0, width, height, dc, x, y, SRCCOPY):
            raise ctypes.WinError(ctypes.get_last_error())
        info = BITMAPINFO()
        info.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        info.bmiHeader.biWidth = width
        info.bmiHeader.biHeight = -height
        info.bmiHeader.biPlanes = 1
        info.bmiHeader.biBitCount = 32
        info.bmiHeader.biCompression = BI_RGB
        buffer = ctypes.create_string_buffer(width * height * 4)
        lines = gdi32.GetDIBits(
            memory_dc, bitmap, 0, height, buffer, ctypes.byref(info), DIB_RGB_COLORS
        )
        if lines != height:
            raise RuntimeError(f"GetDIBits returned {lines}/{height}")
        bgra = np.frombuffer(buffer, np.uint8).reshape(height, width, 4)
        return bgra[:, :, [2, 1, 0]].copy()
    finally:
        gdi32.SelectObject(memory_dc, previous)
        gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(memory_dc)


def capture_printwindow_client(hwnd: int) -> Image.Image:
    wl, wt, wr, wb = window_rect(hwnd)
    cl, ct, cr, cb = client_rect_screen(hwnd)
    width, height = wr - wl, wb - wt
    window_dc = user32.GetWindowDC(hwnd)
    memory_dc = gdi32.CreateCompatibleDC(window_dc)
    bitmap = gdi32.CreateCompatibleBitmap(window_dc, width, height)
    previous = gdi32.SelectObject(memory_dc, bitmap)
    try:
        if not user32.PrintWindow(hwnd, memory_dc, PW_RENDERFULLCONTENT):
            raise ctypes.WinError(ctypes.get_last_error())
        info = BITMAPINFO()
        info.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        info.bmiHeader.biWidth = width
        info.bmiHeader.biHeight = -height
        info.bmiHeader.biPlanes = 1
        info.bmiHeader.biBitCount = 32
        buffer = ctypes.create_string_buffer(width * height * 4)
        lines = gdi32.GetDIBits(
            memory_dc, bitmap, 0, height, buffer, ctypes.byref(info), DIB_RGB_COLORS
        )
        if lines != height:
            raise RuntimeError(f"GetDIBits returned {lines}/{height}")
        bgra = np.frombuffer(buffer, np.uint8).reshape(height, width, 4)
        rgb = bgra[:, :, [2, 1, 0]].copy()
        x0, y0 = cl - wl, ct - wt
        x1, y1 = x0 + (cr - cl), y0 + (cb - ct)
        return Image.fromarray(rgb[y0:y1, x0:x1])
    finally:
        gdi32.SelectObject(memory_dc, previous)
        gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(memory_dc)
        user32.ReleaseDC(hwnd, window_dc)


def capture_all(hwnd: int, state: str) -> dict[str, object]:
    dwmapi.DwmFlush()
    time.sleep(0.15)
    bbox = client_rect_screen(hwnd)
    methods: dict[str, Image.Image] = {}
    methods["imagegrab"] = ImageGrab.grab(bbox=bbox, all_screens=True).convert("RGB")
    desktop_dc = user32.GetDC(0)
    try:
        methods["bitblt"] = Image.fromarray(
            dib_capture(
                desktop_dc,
                bbox[0],
                bbox[1],
                bbox[2] - bbox[0],
                bbox[3] - bbox[1],
            )
        )
    finally:
        user32.ReleaseDC(0, desktop_dc)
    methods["printwindow"] = capture_printwindow_client(hwnd)

    result: dict[str, object] = {"state": state, "clientRect": list(bbox), "methods": {}}
    for method, image in methods.items():
        path = OUT / f"probe-{state}-{method}.png"
        image.save(path)
        data = np.asarray(image)
        method_result = {
            "path": str(path),
            "size": [image.width, image.height],
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
            "nonBlackPixels": int(np.any(data > 8, axis=2).sum()),
        }
        result["methods"][method] = method_result
        print(
            f"CAPTURE state={state} method={method} size={image.width}x{image.height} "
            f"sha256={method_result['sha256']} nonblack={method_result['nonBlackPixels']}"
        )
    return result


def image_delta(left: str, right: str) -> dict[str, object]:
    a = np.asarray(Image.open(left).convert("RGB"))
    b = np.asarray(Image.open(right).convert("RGB"))
    if a.shape != b.shape:
        return {"sameShape": False, "leftShape": list(a.shape), "rightShape": list(b.shape)}
    channel_delta = np.abs(a.astype(np.int16) - b.astype(np.int16))
    changed = np.any(channel_delta > 4, axis=2)
    return {
        "sameShape": True,
        "changedPixels": int(changed.sum()),
        "meanAbsoluteDelta": float(channel_delta.mean()),
        "maxChannelDelta": int(channel_delta.max()),
    }


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    process = subprocess.Popen(
        [str(EXE)],
        cwd=r"C:\Windows",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    hwnd = find_window(process.pid)
    time.sleep(2.0)
    focus(hwnd, process.pid)
    results = [capture_all(hwnd, "idle")]

    send_scan(SC_LEFT, True, extended=True)
    time.sleep(1.35)
    results.append(capture_all(hwnd, "left"))
    send_scan(SC_LEFT, False, extended=True)
    time.sleep(0.5)

    tap_scan(SC_ENTER)
    time.sleep(0.5)
    results.append(capture_all(hwnd, "dialogue"))

    deltas: dict[str, object] = {}
    for method in ("imagegrab", "bitblt", "printwindow"):
        idle = results[0]["methods"][method]["path"]
        left = results[1]["methods"][method]["path"]
        dialogue = results[2]["methods"][method]["path"]
        deltas[method] = {
            "idleToLeft": image_delta(idle, left),
            "idleToDialogue": image_delta(idle, dialogue),
        }
        print(f"DELTA method={method} {json.dumps(deltas[method], separators=(',', ':'))}")

    user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
    try:
        stdout, stderr = process.communicate(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        stdout, stderr = process.communicate()
    summary = {
        "executableSha256": hashlib.sha256(EXE.read_bytes()).hexdigest().upper(),
        "pid": process.pid,
        "hwnd": hwnd,
        "foregroundPid": pid_for(user32.GetForegroundWindow())
        if user32.GetForegroundWindow()
        else 0,
        "captures": results,
        "deltas": deltas,
        "exitCode": process.returncode,
        "stdout": stdout.strip(),
        "stderr": stderr.strip(),
    }
    (OUT / "probe-capture-input-results.json").write_text(
        json.dumps(summary, indent=2), encoding="utf-8"
    )
    print(
        f"PROBE_COMPLETE exit={process.returncode} "
        f"sha256={summary['executableSha256']}"
    )


if __name__ == "__main__":
    main()

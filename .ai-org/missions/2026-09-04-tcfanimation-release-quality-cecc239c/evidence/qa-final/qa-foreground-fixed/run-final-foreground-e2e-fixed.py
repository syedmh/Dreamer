from __future__ import annotations

import ctypes
import hashlib
import json
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path

import numpy as np
from PIL import Image, ImageGrab


PROJECT = Path(r"C:\Users\syedhu\source\repos\Dreamer\TCFAnimation")
EXE = PROJECT / "Build" / "TCFAnimation.exe"
ROOT = Path(
    r"C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions"
    r"\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final"
)
FIXED_ROOT = ROOT / "qa-foreground-fixed"
OUT = FIXED_ROOT / "runtime"

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi", use_last_error=True)

# The old harness was DPI-unaware. Win32 returned a virtualized 1024x576
# client box while ImageGrab cropped physical desktop pixels at 125% DPI,
# which included unrelated windows and omitted part of the Godot client.
DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = ctypes.c_void_p(-4)
if not user32.SetProcessDpiAwarenessContext(
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
):
    error = ctypes.get_last_error()
    if error not in (0, 5):
        raise ctypes.WinError(error)

SW_RESTORE = 9
VK_LEFT = 0x25
VK_RIGHT = 0x27
VK_RETURN = 0x0D
VK_ESCAPE = 0x1B
VK_F4 = 0x73
VK_F11 = 0x7A
VK_MENU = 0x12
VK_CONTROL = 0x11
VK_A = 0x41
VK_V = 0x56
VK_C = 0x43
VK_X = 0x58
VK_P = 0x50
VK_W = 0x57
VK_ADD = 0x6B
VK_SUBTRACT = 0x6D
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_EXTENDEDKEY = 0x0001
KEYEVENTF_SCANCODE = 0x0008
INPUT_KEYBOARD = 1
INPUT_MOUSE = 0
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002
HWND_TOPMOST = -1
HWND_NOTOPMOST = -2
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001
SWP_SHOWWINDOW = 0x0040


class RECT(ctypes.Structure):
    _fields_ = [
        ("left", wintypes.LONG),
        ("top", wintypes.LONG),
        ("right", wintypes.LONG),
        ("bottom", wintypes.LONG),
    ]


class POINT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]


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
    _fields_ = [
        ("mi", MOUSEINPUT),
        ("ki", KEYBDINPUT),
        ("hi", HARDWAREINPUT),
    ]


class INPUT(ctypes.Structure):
    _anonymous_ = ("union",)
    _fields_ = [("type", wintypes.DWORD), ("union", INPUT_UNION)]


EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

user32.GetForegroundWindow.restype = wintypes.HWND
user32.SetFocus.restype = wintypes.HWND
user32.SetActiveWindow.restype = wintypes.HWND
user32.SendInput.argtypes = (wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int)
user32.SendInput.restype = wintypes.UINT
user32.OpenClipboard.argtypes = (wintypes.HWND,)
user32.OpenClipboard.restype = wintypes.BOOL
user32.EmptyClipboard.argtypes = ()
user32.EmptyClipboard.restype = wintypes.BOOL
user32.SetClipboardData.argtypes = (wintypes.UINT, wintypes.HANDLE)
user32.SetClipboardData.restype = wintypes.HANDLE
user32.CloseClipboard.argtypes = ()
user32.CloseClipboard.restype = wintypes.BOOL
kernel32.GlobalAlloc.argtypes = (wintypes.UINT, ctypes.c_size_t)
kernel32.GlobalAlloc.restype = wintypes.HGLOBAL
kernel32.GlobalLock.argtypes = (wintypes.HGLOBAL,)
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalUnlock.argtypes = (wintypes.HGLOBAL,)
kernel32.GlobalFree.argtypes = (wintypes.HGLOBAL,)
user32.GetClipboardData.argtypes = (wintypes.UINT,)
user32.GetClipboardData.restype = wintypes.HANDLE


records: list[dict[str, object]] = []
focus_records: list[dict[str, object]] = []
process_records: list[dict[str, object]] = []
observations: dict[str, object] = {}


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


def pid_for(hwnd: int) -> int:
    owner = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
    return int(owner.value)


def focus(hwnd: int, pid: int, label: str) -> None:
    user32.ShowWindow(hwnd, SW_RESTORE)
    current_thread = kernel32.GetCurrentThreadId()
    foreground_before = user32.GetForegroundWindow()
    foreground_thread = user32.GetWindowThreadProcessId(foreground_before, None)
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
        user32.AllowSetForegroundWindow(0xFFFFFFFF)
        user32.BringWindowToTop(hwnd)
        user32.SetActiveWindow(hwnd)
        user32.SetFocus(hwnd)
        user32.SetForegroundWindow(hwnd)
    finally:
        if attached_target:
            user32.AttachThreadInput(current_thread, target_thread, False)
        if attached_foreground:
            user32.AttachThreadInput(current_thread, foreground_thread, False)
    deadline = time.monotonic() + 3.0
    while time.monotonic() < deadline and user32.GetForegroundWindow() != hwnd:
        tap(VK_MENU)
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.05)
    if user32.GetForegroundWindow() != hwnd:
        left, top, right, bottom = window_rect(hwnd)
        user32.SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW,
        )
        user32.SetWindowPos(
            hwnd,
            HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW,
        )
        click_x = max(left + 20, min(right - 20, left + (right - left) // 2))
        click_y = max(top + 10, min(bottom - 10, top + 18))
        user32.SetCursorPos(click_x, click_y)
        for flags in (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP):
            item = INPUT(
                type=INPUT_MOUSE,
                mi=MOUSEINPUT(0, 0, 0, flags, 0, 0),
            )
            sent = user32.SendInput(1, ctypes.byref(item), ctypes.sizeof(INPUT))
            if sent != 1:
                raise ctypes.WinError(ctypes.get_last_error())
        time.sleep(0.2)
    foreground = int(user32.GetForegroundWindow())
    foreground_pid = pid_for(foreground) if foreground else 0
    row = {
        "label": label,
        "expectedPid": pid,
        "expectedHwnd": int(hwnd),
        "foregroundPid": foreground_pid,
        "foregroundHwnd": foreground,
        "passed": foreground == hwnd and foreground_pid == pid,
    }
    focus_records.append(row)
    print(
        "FOCUS_CHECK "
        f"label={label} expected_pid={pid} foreground_pid={foreground_pid} "
        f"expected_hwnd={int(hwnd)} foreground_hwnd={foreground} "
        f"passed={str(row['passed']).lower()}"
    )
    if not row["passed"]:
        raise RuntimeError(f"Could not foreground target for {label}: {row}")


def send_key(vk: int, up: bool = False, extended: bool = False) -> None:
    scan_codes = {
        VK_LEFT: 0x4B,
        VK_RIGHT: 0x4D,
        VK_RETURN: 0x1C,
        VK_ESCAPE: 0x01,
        VK_F4: 0x3E,
        VK_F11: 0x57,
        VK_MENU: 0x38,
        VK_CONTROL: 0x1D,
        VK_A: 0x1E,
        VK_V: 0x2F,
        VK_C: 0x2E,
        VK_X: 0x2D,
        VK_P: 0x19,
        VK_W: 0x11,
        VK_ADD: 0x4E,
        VK_SUBTRACT: 0x4A,
    }
    scan = scan_codes.get(vk)
    if scan is None:
        raise ValueError(f"No physical scan code configured for virtual key 0x{vk:02X}")
    flags = KEYEVENTF_SCANCODE | (KEYEVENTF_KEYUP if up else 0)
    if extended:
        flags |= KEYEVENTF_EXTENDEDKEY
    item = INPUT(
        type=INPUT_KEYBOARD,
        ki=KEYBDINPUT(0, scan, flags, 0, 0),
    )
    sent = user32.SendInput(1, ctypes.byref(item), ctypes.sizeof(INPUT))
    if sent != 1:
        raise ctypes.WinError(ctypes.get_last_error())


def key_down(vk: int, extended: bool = False) -> None:
    send_key(vk, False, extended)


def key_up(vk: int, extended: bool = False) -> None:
    send_key(vk, True, extended)


def tap(vk: int, extended: bool = False, delay: float = 0.025) -> None:
    key_down(vk, extended)
    time.sleep(delay)
    key_up(vk, extended)


def chord(modifier: int, key: int) -> None:
    if modifier == VK_MENU:
        # Send Alt as a virtual-key modifier so the following physical
        # scan-code Enter event carries AltPressed through Godot/Win32.
        item = INPUT(
            type=INPUT_KEYBOARD,
            ki=KEYBDINPUT(VK_MENU, 0, 0, 0, 0),
        )
        if user32.SendInput(1, ctypes.byref(item), ctypes.sizeof(INPUT)) != 1:
            raise ctypes.WinError(ctypes.get_last_error())
    else:
        key_down(modifier)
    time.sleep(0.03)
    tap(key)
    time.sleep(0.03)
    if modifier == VK_MENU:
        item = INPUT(
            type=INPUT_KEYBOARD,
            ki=KEYBDINPUT(VK_MENU, 0, KEYEVENTF_KEYUP, 0, 0),
        )
        if user32.SendInput(1, ctypes.byref(item), ctypes.sizeof(INPUT)) != 1:
            raise ctypes.WinError(ctypes.get_last_error())
    else:
        key_up(modifier)


def set_clipboard(text: str) -> None:
    encoded = (text + "\0").encode("utf-16-le")
    handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(encoded))
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    pointer = kernel32.GlobalLock(handle)
    if not pointer:
        raise ctypes.WinError(ctypes.get_last_error())
    ctypes.memmove(pointer, encoded, len(encoded))
    kernel32.GlobalUnlock(handle)
    deadline = time.monotonic() + 2.0
    while not user32.OpenClipboard(None):
        if time.monotonic() >= deadline:
            raise ctypes.WinError(ctypes.get_last_error())
        time.sleep(0.02)
    try:
        user32.EmptyClipboard()
        if not user32.SetClipboardData(CF_UNICODETEXT, handle):
            raise ctypes.WinError(ctypes.get_last_error())
        handle = None
    finally:
        user32.CloseClipboard()
        if handle:
            kernel32.GlobalFree(handle)


def paste(hwnd: int, pid: int, label: str, text: str) -> None:
    focus(hwnd, pid, label)
    set_clipboard(text)
    chord(VK_CONTROL, VK_V)
    time.sleep(0.25)


def get_clipboard() -> str:
    deadline = time.monotonic() + 2.0
    while not user32.OpenClipboard(None):
        if time.monotonic() >= deadline:
            raise ctypes.WinError(ctypes.get_last_error())
        time.sleep(0.02)
    try:
        handle = user32.GetClipboardData(CF_UNICODETEXT)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        pointer = kernel32.GlobalLock(handle)
        if not pointer:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return ctypes.wstring_at(pointer)
        finally:
            kernel32.GlobalUnlock(handle)
    finally:
        user32.CloseClipboard()


def copy_all(hwnd: int, pid: int, label: str) -> str:
    focus(hwnd, pid, label)
    chord(VK_CONTROL, VK_A)
    chord(VK_CONTROL, VK_C)
    time.sleep(0.15)
    return get_clipboard()


def replace_text(hwnd: int, pid: int, label: str, text: str) -> None:
    focus(hwnd, pid, label)
    chord(VK_CONTROL, VK_A)
    set_clipboard(text)
    chord(VK_CONTROL, VK_V)
    time.sleep(0.2)


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
    return (
        origin.x,
        origin.y,
        origin.x + rect.right - rect.left,
        origin.y + rect.bottom - rect.top,
    )


def snap(
    hwnd: int,
    pid: int,
    name: str,
    require_focus: bool = True,
    flush_composition: bool = True,
) -> Path:
    if require_focus:
        focus(hwnd, pid, f"snap-{name}")
    # Wait for the OpenGL back buffer to reach the compositor before taking a
    # real foreground desktop crop.
    if flush_composition:
        dwmapi.DwmFlush()
    if require_focus:
        time.sleep(0.10)
    bbox = client_rect_screen(hwnd)
    image = ImageGrab.grab(bbox=bbox, all_screens=True).convert("RGB")
    path = OUT / f"{name}.png"
    image.save(path)
    row = {
        "name": name,
        "pid": pid,
        "hwnd": int(hwnd),
        "clientRect": bbox,
        "width": image.width,
        "height": image.height,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
        "foregroundPid": pid_for(user32.GetForegroundWindow()),
        "path": str(path),
    }
    records.append(row)
    print(
        f"SNAP name={name} pid={pid} client={image.width}x{image.height} "
        f"foreground_pid={row['foregroundPid']} sha256={row['sha256']}"
    )
    return path


def delta(left: Path, right: Path, crop: tuple[int, int, int, int] | None = None) -> dict[str, object]:
    a = np.asarray(Image.open(left).convert("RGB"))
    b = np.asarray(Image.open(right).convert("RGB"))
    if crop:
        x0, y0, x1, y1 = crop
        a = a[y0:y1, x0:x1]
        b = b[y0:y1, x0:x1]
    if a.shape != b.shape:
        return {"sameShape": False, "leftShape": list(a.shape), "rightShape": list(b.shape)}
    changed = np.any(a != b, axis=2)
    return {
        "sameShape": True,
        "changedPixels": int(changed.sum()),
        "maxChannelDelta": int(np.abs(a.astype(np.int16) - b.astype(np.int16)).max()),
    }


class App:
    def __init__(self, name: str, args: list[str] | None = None):
        self.name = name
        command = [str(EXE), *(args or [])]
        self.process = subprocess.Popen(
            command,
            cwd=r"C:\Windows",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        self.hwnd = find_window(self.process.pid)
        time.sleep(1.2)
        focus(self.hwnd, self.process.pid, f"start-{name}")
        print(
            f"SCENARIO_START name={name} pid={self.process.pid} hwnd={self.hwnd} "
            f"args={json.dumps(args or [])}"
        )

    @property
    def pid(self) -> int:
        return self.process.pid

    def close(self) -> None:
        if self.process.poll() is None:
            focus(self.hwnd, self.pid, f"close-{self.name}")
            chord(VK_MENU, VK_F4)
        try:
            stdout, stderr = self.process.communicate(timeout=8)
        except subprocess.TimeoutExpired:
            self.process.kill()
            stdout, stderr = self.process.communicate()
        row = {
            "name": self.name,
            "pid": self.pid,
            "exitCode": self.process.returncode,
            "stdout": stdout.strip(),
            "stderr": stderr.strip(),
        }
        process_records.append(row)
        print(f"SCENARIO_EXIT name={self.name} pid={self.pid} exit={self.process.returncode}")
        if row["stdout"]:
            print("STDOUT_BEGIN")
            print(row["stdout"])
            print("STDOUT_END")
        if row["stderr"]:
            print("STDERR_BEGIN")
            print(row["stderr"])
            print("STDERR_END")

    def __enter__(self) -> "App":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


def capture_timeline(
    app: App,
    prefix: str,
    offsets: list[float],
    start_index: int = 0,
) -> None:
    started = time.perf_counter()
    for index, offset in enumerate(offsets, start=start_index):
        remaining = started + offset - time.perf_counter()
        if remaining > 0:
            time.sleep(remaining)
        snap(app.hwnd, app.pid, f"{prefix}-{index:02d}", require_focus=False)


def capture_clap_sequence(app: App) -> None:
    # Sample faster than the 8 FPS animation and let the analyzer collapse
    # consecutive classifications. This avoids phase assumptions between
    # SendInput, Godot's input dispatch, rendering, and DWM composition.
    # Keep the physical key down through the first composition-flushed capture
    # so frame 0 cannot elapse during tap's key-down/key-up delay.
    dwmapi.DwmFlush()
    key_down(VK_C)
    # The physical input reaches this OpenGL/DWM presentation path after
    # roughly 35-40 ms; 60 ms is inside the 125 ms first-frame interval.
    time.sleep(0.060)
    started = time.perf_counter()
    index = 0
    while time.perf_counter() - started < 2.05:
        snap(
            app.hwnd,
            app.pid,
            f"clap-sample-{index:03d}",
            require_focus=False,
            flush_composition=index != 0,
        )
        if index == 0:
            key_up(VK_C)
        index += 1


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    # A prior interrupted automation run can leave synthetic key state down.
    # Normalize every key this harness uses before launching the first app.
    for vk, extended in (
        (VK_LEFT, True),
        (VK_RIGHT, True),
        (VK_RETURN, False),
        (VK_ESCAPE, False),
        (VK_F4, False),
        (VK_F11, False),
        (VK_MENU, False),
        (VK_CONTROL, False),
        (VK_A, False),
        (VK_V, False),
        (VK_C, False),
        (VK_X, False),
        (VK_P, False),
        (VK_W, False),
        (VK_ADD, False),
        (VK_SUBTRACT, False),
    ):
        key_up(vk, extended)
    time.sleep(0.2)
    analysis: dict[str, object] = {}

    with App("directional") as app:
        snap(app.hwnd, app.pid, "directional-idle")
        key_down(VK_LEFT, True)
        time.sleep(0.18)
        snap(app.hwnd, app.pid, "directional-left-turn")
        time.sleep(0.65)
        snap(app.hwnd, app.pid, "directional-left-walk")
        time.sleep(3.4)
        snap(app.hwnd, app.pid, "directional-left-edge")
        key_up(VK_LEFT, True)
        time.sleep(0.5)
        snap(app.hwnd, app.pid, "directional-left-return")
        key_down(VK_RIGHT, True)
        time.sleep(0.8)
        snap(app.hwnd, app.pid, "directional-right-walk-before-reversal")
        key_up(VK_RIGHT, True)
        key_down(VK_LEFT, True)
        time.sleep(0.25)
        snap(app.hwnd, app.pid, "directional-reversal")
        key_down(VK_RIGHT, True)
        time.sleep(0.55)
        snap(app.hwnd, app.pid, "directional-both-held-neutral")
        key_up(VK_LEFT, True)
        key_up(VK_RIGHT, True)
        time.sleep(0.4)
        key_down(VK_RIGHT, True)
        time.sleep(7.2)
        snap(app.hwnd, app.pid, "directional-right-edge")
        key_up(VK_RIGHT, True)
        time.sleep(0.5)
        snap(app.hwnd, app.pid, "directional-right-return")

    with App("speed-w-noop") as app:
        for _ in range(16):
            tap(VK_SUBTRACT)
        snap(app.hwnd, app.pid, "speed-min")
        key_down(VK_RIGHT, True)
        time.sleep(1.4)
        snap(app.hwnd, app.pid, "speed-min-walk")
        key_up(VK_RIGHT, True)
        time.sleep(0.5)
        for _ in range(20):
            tap(VK_ADD)
        snap(app.hwnd, app.pid, "speed-max")
        key_down(VK_LEFT, True)
        time.sleep(1.4)
        snap(app.hwnd, app.pid, "speed-max-walk")
        key_up(VK_LEFT, True)
        time.sleep(0.5)
        before = snap(app.hwnd, app.pid, "w-noop-before")
        tap(VK_W)
        time.sleep(0.3)
        after = snap(app.hwnd, app.pid, "w-noop-after")
        analysis["wNoopDelta"] = delta(before, after)

    with App("clap") as app:
        capture_clap_sequence(app)
        time.sleep(0.3)
        snap(app.hwnd, app.pid, "clap-return-front")
        tap(VK_C)
        time.sleep(0.42)
        snap(app.hwnd, app.pid, "clap-before-interrupt")
        key_down(VK_RIGHT, True)
        time.sleep(0.42)
        snap(app.hwnd, app.pid, "clap-direction-interrupted")
        tap(VK_C)
        time.sleep(0.25)
        snap(app.hwnd, app.pid, "clap-c-ignored-moving")
        key_up(VK_RIGHT, True)

    with App("cross") as app:
        # As with clap, hold the physical trigger through the first flushed
        # rendered capture to deterministically retain the entry frame 0.
        dwmapi.DwmFlush()
        key_down(VK_X)
        # This OpenGL/DWM path presents the first cross-arm frame 45-50 ms
        # after the physical scan-code input. Capture inside its 125 ms hold.
        time.sleep(0.060)
        snap(
            app.hwnd,
            app.pid,
            "cross-entry-00",
            require_focus=False,
            flush_composition=False,
        )
        key_up(VK_X)
        capture_timeline(app, "cross-entry", [0.10, 0.225], start_index=1)
        time.sleep(0.6)
        held = snap(app.hwnd, app.pid, "cross-held")
        tap(VK_C)
        key_down(VK_LEFT, True)
        time.sleep(0.45)
        blocked = snap(app.hwnd, app.pid, "cross-held-inputs-blocked")
        analysis["crossHeldInputDelta"] = delta(held, blocked)
        tap(VK_X)
        capture_timeline(app, "cross-release", [0.02 + 0.125 * i for i in range(6)])
        time.sleep(0.35)
        snap(app.hwnd, app.pid, "cross-release-arrow-still-held")
        key_up(VK_LEFT, True)
        time.sleep(0.2)
        key_down(VK_LEFT, True)
        time.sleep(0.55)
        snap(app.hwnd, app.pid, "cross-arrow-repress-moves")
        key_up(VK_LEFT, True)

    exact_text = "ASCII w/W | العربية | 😀🧪"
    with App("dialogue-text-lifecycle") as app:
        tap(VK_RETURN)
        time.sleep(0.2)
        snap(app.hwnd, app.pid, "dialogue-empty-input")
        paste(app.hwnd, app.pid, "dialogue-exact-paste", exact_text)
        observations["dialogueExactClipboard"] = copy_all(
            app.hwnd, app.pid, "dialogue-exact-copy"
        )
        snap(app.hwnd, app.pid, "dialogue-exact-input")
        tap(VK_P)
        observations["dialoguePTypeableClipboard"] = copy_all(
            app.hwnd, app.pid, "dialogue-p-typeable-copy"
        )
        replace_text(
            app.hwnd,
            app.pid,
            "dialogue-restore-exact",
            exact_text,
        )
        controls_before = snap(app.hwnd, app.pid, "dialogue-controls-before")
        tap(VK_C)
        tap(VK_X)
        tap(VK_SUBTRACT)
        tap(VK_ADD)
        key_down(VK_RIGHT, True)
        time.sleep(0.35)
        key_up(VK_RIGHT, True)
        controls_after = snap(app.hwnd, app.pid, "dialogue-controls-after")
        width = Image.open(controls_before).width
        height = Image.open(controls_before).height
        analysis["dialogueCharacterControlDelta"] = delta(
            controls_before,
            controls_after,
            (0, height // 3, width, height),
        )
        tap(VK_RETURN)
        time.sleep(0.35)
        submitted = snap(app.hwnd, app.pid, "dialogue-exact-submitted")
        tap(VK_RETURN)
        paste(app.hwnd, app.pid, "dialogue-whitespace-paste", " \t \r\n ")
        observations["dialogueWhitespaceClipboard"] = copy_all(
            app.hwnd, app.pid, "dialogue-whitespace-copy"
        )
        tap(VK_RETURN)
        time.sleep(0.25)
        whitespace = snap(app.hwnd, app.pid, "dialogue-whitespace-submitted")
        analysis["whitespacePreservesBubbleDelta"] = delta(submitted, whitespace)
        tap(VK_RETURN)
        paste(app.hwnd, app.pid, "dialogue-cancel-paste", "cancel me")
        tap(VK_ESCAPE)
        time.sleep(0.25)
        cancelled = snap(app.hwnd, app.pid, "dialogue-cancelled")
        analysis["escapePreservesBubbleDelta"] = delta(submitted, cancelled)
        tap(VK_P)
        time.sleep(0.2)
        snap(app.hwnd, app.pid, "dialogue-hidden-p")

    with App("dialogue-fullscreen-arbitration") as app:
        snap(app.hwnd, app.pid, "dialogue-alt-enter-closed-windowed")
        chord(VK_MENU, VK_RETURN)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-alt-enter-closed-fullscreen")
        chord(VK_MENU, VK_RETURN)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-alt-enter-closed-back-windowed")
        tap(VK_RETURN)
        paste(app.hwnd, app.pid, "dialogue-fullscreen-paste", "fullscreen w/W 😀")
        observations["dialogueClipboardBeforeFullscreen"] = copy_all(
            app.hwnd, app.pid, "dialogue-fullscreen-copy-before"
        )
        current_client = client_rect_screen(app.hwnd)
        focused_transitions = [
            [
                current_client[2] - current_client[0],
                current_client[3] - current_client[1],
            ]
        ]
        tap(VK_F11)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-f11-open-fullscreen")
        current_client = client_rect_screen(app.hwnd)
        focused_transitions.append(
            [
                current_client[2] - current_client[0],
                current_client[3] - current_client[1],
            ]
        )
        observations["dialogueClipboardAfterF11"] = copy_all(
            app.hwnd, app.pid, "dialogue-f11-copy"
        )
        chord(VK_MENU, VK_RETURN)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-alt-enter-open-windowed")
        current_client = client_rect_screen(app.hwnd)
        focused_transitions.append(
            [
                current_client[2] - current_client[0],
                current_client[3] - current_client[1],
            ]
        )
        observations["dialogueClipboardAfterAltEnterToWindowed"] = copy_all(
            app.hwnd, app.pid, "dialogue-alt-enter-windowed-copy"
        )
        first_alt_enter_succeeded = (
            current_client[2] - current_client[0] == 1280
            and current_client[3] - current_client[1] == 720
        )
        chord(VK_MENU, VK_RETURN)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-alt-enter-open-fullscreen")
        current_client = client_rect_screen(app.hwnd)
        focused_transitions.append(
            [
                current_client[2] - current_client[0],
                current_client[3] - current_client[1],
            ]
        )
        observations["dialogueClipboardAfterAltEnterToFullscreen"] = copy_all(
            app.hwnd, app.pid, "dialogue-alt-enter-fullscreen-copy"
        )
        second_alt_enter_succeeded = (
            current_client[2] - current_client[0] > 1280
            and current_client[3] - current_client[1] > 720
        )
        observations["dialogueFocusedFullscreenTransitions"] = focused_transitions
        observations["dialogueAltEnterEditingSucceeded"] = (
            first_alt_enter_succeeded and second_alt_enter_succeeded
        )
        if not second_alt_enter_succeeded:
            # Recover with F11 so Escape ownership can still be validated
            # independently after recording the Alt+Enter product failure.
            tap(VK_F11)
            time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-before-first-escape-fullscreen")
        tap(VK_ESCAPE)
        time.sleep(0.35)
        snap(app.hwnd, app.pid, "dialogue-first-escape-cancel-only")
        tap(VK_ESCAPE)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-second-escape-exits-fullscreen")

    with App("dialogue-scalar-limits") as app:
        tap(VK_RETURN)
        paste(app.hwnd, app.pid, "dialogue-500-paste", "😀" * 500)
        observations["dialogue500Clipboard"] = copy_all(
            app.hwnd, app.pid, "dialogue-500-copy"
        )
        snap(app.hwnd, app.pid, "dialogue-500-input")
        tap(VK_RETURN)
        time.sleep(0.35)
        p500 = snap(app.hwnd, app.pid, "dialogue-500-submitted")
        tap(VK_P)
        tap(VK_RETURN)
        paste(app.hwnd, app.pid, "dialogue-501-paste", "😀" * 500 + "🧪")
        observations["dialogue501Clipboard"] = copy_all(
            app.hwnd, app.pid, "dialogue-501-copy"
        )
        snap(app.hwnd, app.pid, "dialogue-501-input")
        tap(VK_RETURN)
        time.sleep(0.35)
        p501 = snap(app.hwnd, app.pid, "dialogue-501-submitted")
        analysis["scalar500vs501SubmittedDelta"] = delta(p500, p501)

    with App("dialogue-edges") as app:
        key_down(VK_LEFT, True)
        time.sleep(4.2)
        key_up(VK_LEFT, True)
        time.sleep(0.5)
        tap(VK_RETURN)
        paste(app.hwnd, app.pid, "dialogue-left-edge-paste", "Left edge العربية 😀")
        tap(VK_RETURN)
        time.sleep(0.35)
        snap(app.hwnd, app.pid, "dialogue-left-edge-windowed")
        tap(VK_F11)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-left-edge-fullscreen")
        tap(VK_F11)
        time.sleep(0.6)
        tap(VK_P)
        key_down(VK_RIGHT, True)
        time.sleep(7.2)
        key_up(VK_RIGHT, True)
        time.sleep(0.5)
        tap(VK_RETURN)
        paste(app.hwnd, app.pid, "dialogue-right-edge-paste", "Right edge العربية 😀")
        tap(VK_RETURN)
        time.sleep(0.35)
        snap(app.hwnd, app.pid, "dialogue-right-edge-windowed")
        tap(VK_F11)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-right-edge-fullscreen")
        tap(VK_ESCAPE)
        time.sleep(0.6)
        snap(app.hwnd, app.pid, "dialogue-right-edge-back-windowed")

    for requested in ("1000x800", "1280x720", "1536x864", "1536x960"):
        with App(f"viewport-{requested}", ["--resolution", requested]) as app:
            snap(app.hwnd, app.pid, f"viewport-{requested}-center")
            key_down(VK_RIGHT, True)
            time.sleep(4.4)
            key_up(VK_RIGHT, True)
            time.sleep(0.4)
            tap(VK_RETURN)
            paste(
                app.hwnd,
                app.pid,
                f"viewport-{requested}-paste",
                f"{requested} edge 😀",
            )
            tap(VK_RETURN)
            time.sleep(0.3)
            snap(app.hwnd, app.pid, f"viewport-{requested}-right-dialogue")

    summary = {
        "executable": str(EXE),
        "exeBytes": EXE.stat().st_size,
        "exeSha256": hashlib.sha256(EXE.read_bytes()).hexdigest().upper(),
        "interaction": "Win32 SendInput into verified foreground HWND; clipboard CF_UNICODETEXT paste",
        "screenshots": records,
        "focusChecks": focus_records,
        "processes": process_records,
        "observations": observations,
        "analysis": analysis,
        "focusAllPassed": all(row["passed"] for row in focus_records),
    }
    capture_results = FIXED_ROOT / "foreground-e2e-capture-results.json"
    capture_results.write_text(
        json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    analyzer = subprocess.run(
        [
            sys.executable,
            "-B",
            str(FIXED_ROOT / "analyze_foreground_e2e.py"),
            str(capture_results),
        ],
        cwd=FIXED_ROOT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if analyzer.returncode != 0:
        print(
            "FOREGROUND_E2E_FAIL "
            f"capture_screenshots={len(records)} "
            f"analyzer_exit={analyzer.returncode}"
        )
        raise SystemExit(analyzer.returncode)
    print(
        "FOREGROUND_E2E_PASS "
        f"screenshots={len(records)} focus_checks={len(focus_records)} "
        f"focus_all={str(summary['focusAllPassed']).lower()} "
        f"processes={len(process_records)}"
    )


if __name__ == "__main__":
    main()

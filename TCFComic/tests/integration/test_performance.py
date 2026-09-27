from __future__ import annotations

import ctypes
import json
import os
import platform
import signal
import statistics
import subprocess
import sys
import threading
import time
from pathlib import Path

import pytest

from conftest import write_config, write_image
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner


class _LatencyProvider(FakeProvider):
    def __init__(self, entered: list[float], complete: threading.Event) -> None:
        super().__init__()
        self.entered = entered
        self.complete = complete

    def transform(self, request, output):
        self.entered.append(time.monotonic())
        result = super().transform(request, output)
        if len(self.requests) == 10:
            self.complete.set()
        return result


@pytest.mark.performance
def test_detection_latency_ten_arrivals_per_minute(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(
        tmp_path,
        stable_seconds=0,
        poll_interval_seconds=0.1,
    )
    entered: list[float] = []
    complete = threading.Event()
    shutdown = threading.Event()
    provider = _LatencyProvider(entered, complete)
    errors: list[BaseException] = []

    def watch() -> None:
        try:
            with Processor(
                config,
                quiet_logger,
                runner=InProcessAttemptRunner(provider),
            ) as processor:
                processor.watch(shutdown_event=shutdown)
        except BaseException as exc:
            errors.append(exc)

    thread = threading.Thread(target=watch, daemon=False)
    thread.start()
    lock_path = config.paths.destination / ".tcfcomic" / "runtime.lock"
    deadline = time.monotonic() + 10
    while not lock_path.exists() and time.monotonic() < deadline:
        time.sleep(0.01)
    assert lock_path.exists()

    arrival_times: dict[str, float] = {}
    schedule_start = time.monotonic()
    for index in range(10):
        target = schedule_start + index * 6.0
        remaining = target - time.monotonic()
        if remaining > 0:
            time.sleep(remaining)
        name = f"arrival-{index:02d}.png"
        write_image(config.paths.source / name, color=(index, index, index))
        arrival_times[name] = time.monotonic()

    assert complete.wait(15), f"only {len(provider.requests)} arrivals dispatched"
    shutdown.set()
    thread.join(timeout=15)
    assert not thread.is_alive()
    assert errors == []
    latencies = sorted(
        entered_at - arrived_at
        for entered_at, arrived_at in zip(
            entered, arrival_times.values(), strict=True
        )
    )
    p95 = statistics.quantiles(latencies, n=100, method="inclusive")[94]
    print(
        json.dumps(
            {
                "test": "NFR-01",
                "arrivals": 10,
                "modeled_rate_per_minute": 10,
                "p95_seconds": p95,
                "max_seconds": max(latencies),
                "platform": platform.platform(),
                "python": sys.version,
            },
            sort_keys=True,
        )
    )
    assert p95 < 10.0


class _FILETIME(ctypes.Structure):
    _fields_ = [("low", ctypes.c_uint32), ("high", ctypes.c_uint32)]


class _PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_uint32),
        ("PageFaultCount", ctypes.c_uint32),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    ]


def _filetime_seconds(value: _FILETIME) -> float:
    return ((value.high << 32) | value.low) / 10_000_000.0


def _process_cpu_seconds(handle: int) -> float:
    creation = _FILETIME()
    exit_time = _FILETIME()
    kernel = _FILETIME()
    user = _FILETIME()
    succeeded = ctypes.windll.kernel32.GetProcessTimes(
        handle,
        ctypes.byref(creation),
        ctypes.byref(exit_time),
        ctypes.byref(kernel),
        ctypes.byref(user),
    )
    if not succeeded:
        raise ctypes.WinError()
    return _filetime_seconds(kernel) + _filetime_seconds(user)


def _working_set_bytes(handle: int) -> tuple[int, int]:
    counters = _PROCESS_MEMORY_COUNTERS_EX()
    counters.cb = ctypes.sizeof(counters)
    succeeded = ctypes.windll.psapi.GetProcessMemoryInfo(
        handle, ctypes.byref(counters), counters.cb
    )
    if not succeeded:
        raise ctypes.WinError()
    return counters.WorkingSetSize, counters.PeakWorkingSetSize


@pytest.mark.performance
@pytest.mark.skipif(os.name != "nt", reason="Windows external resource measurement")
def test_idle_watch_five_minute_windows_resource_budget(tmp_path: Path) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(
        tmp_path,
        stable_seconds=3,
        poll_interval_seconds=1,
    )
    process = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "tcfcomic",
            "watch",
            "--config",
            str(config_path),
        ],
        cwd=project,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
    )
    lock_path = config.paths.destination / ".tcfcomic" / "runtime.lock"
    deadline = time.monotonic() + 15
    while not lock_path.exists() and time.monotonic() < deadline:
        if process.poll() is not None:
            stdout, stderr = process.communicate()
            raise AssertionError((process.returncode, stdout, stderr))
        time.sleep(0.05)
    assert lock_path.exists()

    handle = int(process._handle)
    cpu_start = _process_cpu_seconds(handle)
    wall_start = time.monotonic()
    working_sets: list[int] = []
    peak_sets: list[int] = []
    try:
        while time.monotonic() - wall_start < 300:
            working, peak = _working_set_bytes(handle)
            working_sets.append(working)
            peak_sets.append(peak)
            time.sleep(1)
    finally:
        if process.poll() is None:
            process.send_signal(signal.CTRL_BREAK_EVENT)
    stdout, stderr = process.communicate(timeout=15)
    elapsed = time.monotonic() - wall_start
    cpu_seconds = _process_cpu_seconds(handle)
    logical_processors = os.cpu_count() or 1
    average_cpu_percent = (
        (cpu_seconds - cpu_start) / elapsed / logical_processors * 100
    )
    average_working_set_mb = statistics.fmean(working_sets) / (1024 * 1024)
    peak_working_set_mb = max(peak_sets) / (1024 * 1024)
    measurement = {
        "test": "NFR-02",
        "elapsed_seconds": elapsed,
        "samples": len(working_sets),
        "average_cpu_percent": average_cpu_percent,
        "average_working_set_mb": average_working_set_mb,
        "peak_working_set_mb": peak_working_set_mb,
        "logical_processors": logical_processors,
        "platform": platform.platform(),
        "python": sys.version,
        "processor": platform.processor(),
    }
    print(json.dumps(measurement, sort_keys=True))
    assert process.returncode == 0, (stdout, stderr)
    assert elapsed >= 300
    assert average_cpu_percent <= 2.0
    assert average_working_set_mb <= 300
    assert peak_working_set_mb <= 300

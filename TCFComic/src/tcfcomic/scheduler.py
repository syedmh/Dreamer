from __future__ import annotations

import threading
import time
from datetime import UTC, datetime, timedelta
from typing import Callable


class SchedulerClock:
    """UTC epoch advanced by monotonic elapsed time until the next restart."""

    def __init__(
        self, *,
        utc: Callable[[], datetime] = lambda: datetime.now(UTC),
        monotonic: Callable[[], float] = time.monotonic,
        wait: Callable[[threading.Event, float], bool] = lambda event, delay: event.wait(delay),
    ) -> None:
        self._epoch = utc()
        self.monotonic = monotonic
        self._origin = monotonic()
        self.wait = wait

    def now(self) -> datetime:
        return self._epoch + timedelta(seconds=self.monotonic() - self._origin)

    def timestamp(self) -> str:
        return self.now().isoformat()

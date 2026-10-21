# age deliberately reads passwords from a terminal, not environment variables.
# Test-only PTY driver; no application secrets, only the fixed test passphrase.
import errno
import os
import pty
import select
import sys
import time

pid, fd = pty.fork()
if pid == 0:
    os.execv(sys.argv[1], sys.argv[1:])
try:
    pending = b""
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if not select.select([fd], [], [], 0.1)[0]:
            continue
        try:
            data = os.read(fd, 4096)
        except OSError as error:
            if error.errno == errno.EIO:
                break
            raise
        if not data:
            break
        pending += data
        if b":" in pending:
            os.write(fd, b"password\n")
            pending = b""
    else:
        os.kill(pid, 9)
        raise RuntimeError("age CLI timed out")
    _, status = os.waitpid(pid, 0)
    sys.exit(os.waitstatus_to_exitcode(status))
finally:
    os.close(fd)

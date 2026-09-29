import fcntl
import os
import pty
import re
import select
import struct
import sys
import termios
import time

if len(sys.argv) != 2:
    raise SystemExit("usage: pty_input_probe.py <linux-probe-binary>")

binary = sys.argv[1]
flags_read, flags_write = os.pipe()
pid, master = pty.fork()
if pid == 0:
    os.close(flags_read)
    fcntl.ioctl(0, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
    os.write(flags_write, struct.pack("I", termios.tcgetattr(0)[3]))
    os.close(flags_write)
    os.execv(binary, [binary])

os.close(flags_write)
initial_lflags = struct.unpack("I", os.read(flags_read, 4))[0]
os.close(flags_read)
initial_echo = bool(initial_lflags & termios.ECHO)
initial_canon = bool(initial_lflags & termios.ICANON)
output = bytearray()
deadline = time.time() + 4
while b"READY " not in output and time.time() < deadline:
    ready, _, _ = select.select([master], [], [], 0.1)
    if ready:
        try:
            output.extend(os.read(master, 4096))
        except OSError:
            break
if b"READY " not in output:
    raise SystemExit(f"probe did not reach ready state: {output!r}")
deadline = time.time() + 1
while b"READ_PENDING " not in output and time.time() < deadline:
    ready, _, _ = select.select([master], [], [], 0.1)
    if ready:
        try:
            output.extend(os.read(master, 4096))
        except OSError:
            break
if b"READ_PENDING True" not in output:
    raise SystemExit(f"async input read blocked the session setup: {output!r}")

sent = b"x" * 80 + b"!"
for value in sent:
    os.write(master, bytes([value]))
    ready, _, _ = select.select([master], [], [], 0.005)
    if ready:
        try:
            output.extend(os.read(master, 4096))
        except OSError:
            break
    time.sleep(0.05)

deadline = time.time() + 4
while time.time() < deadline:
    ready, _, _ = select.select([master], [], [], 0.05)
    if ready:
        try:
            output.extend(os.read(master, 4096))
        except OSError:
            break
    done, _ = os.waitpid(pid, os.WNOHANG)
    if done:
        break
else:
    os.kill(pid, 9)
    os.waitpid(pid, 0)
    raise SystemExit(f"probe did not exit: {output!r}")

text = output.decode("utf-8", "replace")
remote = "".join(re.findall(r"REMOTE:(.*?)\r?\n", text, re.S))
outside = re.sub(r"REMOTE:.*?\r?\n", "", text, flags=re.S)
size_polls = re.findall(r"SIZE (\d+) echo=False canon=False", text)
restored = re.search(r"RESTORED echo=(True|False) canon=(True|False)", text)
print(f"{text.splitlines()[0]}")
print(f"asynchronous read stayed pending without blocking setup: {'READ_PENDING True' in text}")
print(f"received input bytes: {len(remote)} / {len(sent)}")
print(f"unframed locally echoed x bytes: {outside.count('x')}")
print(f"size polls while raw: {len(size_polls)}")
print(f"restored original echo/canonical flags: {restored.groups() if restored else 'missing'}")
print(f"transcript: {text[:1000]!r}")
if remote != sent.decode("ascii"):
    raise SystemExit("raw input stream did not deliver the exact input bytes")
if outside.count("x"):
    raise SystemExit("terminal locally echoed input while resize polling was active")
if len(size_polls) < 3:
    raise SystemExit("probe did not exercise repeated terminal-size polling")
if restored is None or (restored.group(1) == "True") != initial_echo or (restored.group(2) == "True") != initial_canon:
    raise SystemExit("terminal mode was not restored after the interactive session")

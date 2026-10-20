"""Start the HUD only while a supported local Codex client is running.

The small user-session watcher reads executable paths, never process arguments
or chat logs. It owns only HUDs it launched; a manually opened HUD is preserved.
"""
from __future__ import annotations

import argparse
import base64
import ctypes
import os
from pathlib import Path
import plistlib
import shutil
import signal
import subprocess
import sys
import time
import uuid

if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
from dashboard import data_directory
from metrics import atomic_json, read_selection


def process_kind(executable, extra=()):
    path = str(executable).replace("\\", "/").lower()
    normalize = lambda p: os.path.normcase(str(p).replace("\\", "/"))
    if any(normalize(executable) == normalize(p) for p in extra):
        return "custom"
    name = path.rsplit("/", 1)[-1]
    desktop = "/codex.app/contents/macos/" in path or "/openai.codex_" in path
    if desktop and name in ("codex", "codex.exe", "chatgpt.exe"):
        return "desktop"
    # Desktop/CLI/IDE extensions share the official native Codex backend. An
    # editor or terminal alone, ChatGPT, and this tool's own processes do not count.
    if name in ("codex", "codex.exe"):
        return "backend"
    return None


def windows_processes(extra=()):
    from ctypes import wintypes as w
    class Entry(ctypes.Structure):
        _fields_ = [("size", w.DWORD), ("usage", w.DWORD), ("pid", w.DWORD),
                    ("heap", ctypes.c_size_t), ("module", w.DWORD), ("threads", w.DWORD),
                    ("parent", w.DWORD), ("priority", w.LONG), ("flags", w.DWORD), ("name", w.WCHAR * 260)]
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    k.CreateToolhelp32Snapshot.argtypes = (w.DWORD, w.DWORD)
    k.CreateToolhelp32Snapshot.restype = w.HANDLE
    k.Process32FirstW.argtypes = k.Process32NextW.argtypes = (w.HANDLE, ctypes.POINTER(Entry))
    k.OpenProcess.argtypes = (w.DWORD, w.BOOL, w.DWORD)
    k.OpenProcess.restype = w.HANDLE
    k.QueryFullProcessImageNameW.argtypes = (w.HANDLE, w.DWORD, w.LPWSTR, ctypes.POINTER(w.DWORD))
    k.ProcessIdToSessionId.argtypes = (w.DWORD, ctypes.POINTER(w.DWORD))
    k.CloseHandle.argtypes = (w.HANDLE,)
    session = w.DWORD()
    if not k.ProcessIdToSessionId(os.getpid(), ctypes.byref(session)):
        raise OSError(ctypes.get_last_error(), "Cannot identify this Windows session")
    snapshot = k.CreateToolhelp32Snapshot(2, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        raise OSError(ctypes.get_last_error(), "Cannot enumerate processes")
    found = []
    try:
        entry = Entry(size=ctypes.sizeof(Entry))
        more = k.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            if entry.name.lower() in {"codex.exe", "chatgpt.exe", *(str(p).replace("\\", "/").rsplit("/", 1)[-1].lower() for p in extra)}:
                other = w.DWORD()
                if k.ProcessIdToSessionId(entry.pid, ctypes.byref(other)) and other.value == session.value:
                    handle = k.OpenProcess(0x1000, False, entry.pid)
                    if handle:
                        try:
                            name, size = ctypes.create_unicode_buffer(32768), w.DWORD(32768)
                            if k.QueryFullProcessImageNameW(handle, 0, name, ctypes.byref(size)):
                                kind = process_kind(name.value, extra)
                                if kind:
                                    found.append((entry.pid, kind))
                        finally:
                            k.CloseHandle(handle)
            more = k.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        k.CloseHandle(snapshot)
    return found


def supported_processes(extra=()):
    if os.name == "nt":
        return windows_processes(extra)
    if Path("/proc/self/exe").exists():
        found = []
        for directory in Path("/proc").iterdir():
            if not directory.name.isdigit():
                continue
            try:
                if directory.stat().st_uid != os.getuid():
                    continue
                kind = process_kind(os.readlink(directory / "exe"), extra)
                if kind:
                    found.append((int(directory.name), kind))
            except (OSError, ValueError):
                continue  # A process may exit during enumeration.
        return found
    if os.name == "posix":
        result = subprocess.run(["/bin/ps", "-ww", "-axo", "pid=,uid=,comm="],
                                capture_output=True, text=True, timeout=5, check=True)
        found = []
        for line in result.stdout.splitlines():
            parts = line.strip().split(None, 2)
            if len(parts) == 3 and parts[1] == str(os.getuid()):
                kind = process_kind(parts[2], extra)
                if kind:
                    found.append((int(parts[0]), kind))
        return found
    raise RuntimeError("Application following needs Windows, macOS or Linux")


def process_alive(pid):
    if not isinstance(pid, int) or pid <= 0:
        return False
    if os.name == "nt":
        from ctypes import wintypes as w
        k = ctypes.WinDLL("kernel32", use_last_error=True)
        k.OpenProcess.argtypes, k.OpenProcess.restype = (w.DWORD, w.BOOL, w.DWORD), w.HANDLE
        k.GetExitCodeProcess.argtypes = (w.HANDLE, ctypes.POINTER(w.DWORD))
        k.CloseHandle.argtypes = (w.HANDLE,)
        handle = k.OpenProcess(0x1000, False, pid)
        if not handle:
            return False
        try:
            code = w.DWORD()
            return bool(k.GetExitCodeProcess(handle, ctypes.byref(code)) and code.value == 259)
        finally:
            k.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


class Lifecycle:
    """Debounce startup/exit, suppress repeated launches after a manual exit."""
    def __init__(self, settle=1.0, grace=6.0):
        self.settle, self.grace = settle, grace
        self.active_since = self.empty_since = None
        self.observed_hud = False
        self.dismissed = False
        self.closing = False

    def step(self, targets, hud_running, owned, now, keep_open=False):
        if keep_open and not targets:
            targets = ["resident"]
        if targets:
            self.empty_since = None
            if self.active_since is None:
                self.active_since = now
            if self.observed_hud and not hud_running:
                self.dismissed = not self.closing
                self.closing = False
            self.observed_hud = hud_running
            if not hud_running and not self.dismissed and now - self.active_since >= self.settle:
                return "start"
        else:
            self.active_since = None
            if self.empty_since is None:
                self.empty_since = now
            if now - self.empty_since >= self.grace:
                self.dismissed = self.observed_hud = False
                if owned and hud_running and not self.closing:
                    self.closing = True
                    return "close"
        if not hud_running:
            self.closing = False
        return None


class WatcherLock:
    def __init__(self, folder):
        folder.mkdir(parents=True, exist_ok=True)
        self.file = (folder / "watcher.lock").open("a+b")
        if self.file.tell() == 0:
            self.file.write(b"0")
            self.file.flush()
        self.file.seek(0)

    def acquire(self):
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return True
        except OSError:
            return False

    def close(self):
        self.file.close()


def default_config():
    return data_directory() / "app-follow/config.json"


def overlay_command():
    if getattr(sys, "frozen", False):
        return [sys.executable]
    return [sys.executable, "-I", "-X", "utf8", str(Path(__file__).with_name("overlay.py").resolve())]


def hud_command(interface="auto"):
    if getattr(sys, "frozen", False):
        return overlay_command()
    if interface == "dashboard" or (interface == "auto" and os.name != "nt" and sys.platform != "darwin" and
                                    not os.environ.get("DISPLAY") and not os.environ.get("WAYLAND_DISPLAY")):
        return [sys.executable, "-I", "-X", "utf8", str(Path(__file__).with_name("dashboard.py").resolve()), "--no-browser"]
    exe = Path(__file__).with_name("CodexTokenHud.exe")
    return [str(exe.resolve())] if os.name == "nt" and exe.exists() else overlay_command()


def watcher_command(config):
    if getattr(sys, "frozen", False):
        return [sys.executable, "--watch-apps", "--config", str(config)]
    return [sys.executable, "-I", "-X", "utf8", str(Path(__file__).resolve()), "--watch", "--config", str(config)]


def enabled(installation=None, config=None):
    saved = read_selection(config or default_config())
    return bool(saved.get("enabled") and (installation is None or saved.get("installation") == installation))


def desktop_exec(command):
    # Desktop-entry escaping has two layers; percent is a field-code prefix.
    def quote(value):
        value = value.replace("%", "%%").replace("\\", "\\\\").replace('"', '\\"').replace("$", "\\$").replace("`", "\\`")
        return '"' + value + '"'
    return " ".join(quote(v) for v in command).replace("\\", "\\\\")


def systemd_exec(command):
    return " ".join('"' + v.replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%").replace("$", "$$") + '"' for v in command)


def powershell(script):
    script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8\n" + script + "\nexit 0"
    command = ["powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand",
               base64.b64encode(script.encode("utf-16le")).decode("ascii")]
    result = subprocess.run(command, capture_output=True, creationflags=0x08000000, timeout=25)
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace")[-1800:] or "Cannot change application-follow task")


def register(config, value, turn_on):
    command = watcher_command(config)
    if os.name == "nt":
        literal = lambda s: "'" + str(s).replace("'", "''") + "'"
        task = "CodexTokenHud-" + os.environ["USERNAME"]
        args = subprocess.list2cmdline(command[1:])
        legacy = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData/Local") / "CodexTokenHud/app-follow/config.json"
        legacy_args = subprocess.list2cmdline(watcher_command(legacy)[1:])
        # A legacy task pointing to this HUD is migrated. Never replace another
        # copy's task, even when both copies use the same Python interpreter.
        script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8\n"
        script += f"$taskName={literal(task)}; $taskOld=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue\n"
        script += f"if ($taskOld -and -not (($taskOld.Actions[0].Execute -eq {literal(value['installation'])}) -or (($taskOld.Actions[0].Execute -eq {literal(command[0])}) -and (($taskOld.Actions[0].Arguments -eq {literal(args)}) -or ($taskOld.Actions[0].Arguments -eq {literal(legacy_args)}))))) {{ throw 'Startup task belongs to another copy; disable it there first.' }}\n"
        if turn_on:
            script += f"$taskAction=New-ScheduledTaskAction -Execute {literal(command[0])} -Argument {literal(args)} -WorkingDirectory {literal(str(Path(value['installation']).parent))}\n"
            script += "$taskUser=[Security.Principal.WindowsIdentity]::GetCurrent().Name\n"
            script += "$taskTrigger=New-ScheduledTaskTrigger -AtLogOn -User $taskUser\n"
            script += "$taskPrincipal=New-ScheduledTaskPrincipal -UserId $taskUser -LogonType Interactive -RunLevel Highest\n"
            script += "$taskSettings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew\n"
            script += "Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Settings $taskSettings -Description 'Wait for Codex Desktop, CLI or IDE backend; open HUD only while a client runs' -Force | Out-Null\n"
        else:
            script += "if($taskOld){Unregister-ScheduledTask -TaskName $taskName -Confirm:$false}\n"
        powershell(script)
    elif sys.platform == "darwin":
        path = Path.home() / "Library/LaunchAgents/io.github.drkang7.codex-token-hud.follow.plist"
        if path.exists() and plistlib.loads(path.read_bytes()).get("ProgramArguments") != command:
            raise RuntimeError("Application-follow agent belongs to another copy")
        if turn_on:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(plistlib.dumps({"Label": "io.github.drkang7.codex-token-hud.follow",
                                           "ProgramArguments": command, "RunAtLoad": True,
                                           "ProcessType": "Background"}))
            domain = f"gui/{os.getuid()}"
            loaded = subprocess.run(["launchctl", "print", domain + "/io.github.drkang7.codex-token-hud.follow"], capture_output=True)
            if loaded.returncode:
                result = subprocess.run(["launchctl", "bootstrap", domain, str(path)], capture_output=True)
                if result.returncode:
                    raise RuntimeError("Could not load user LaunchAgent: " + result.stderr.decode(errors="replace"))
        elif path.exists():
            # Removing registration and clearing enabled lets the watcher exit
            # normally, keeping any manually continued HUD alive.
            path.unlink()
    else:
        unit_path = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / "systemd/user/codex-token-hud-follow.service"
        desktop_path = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / "autostart/codex-token-hud-follow.desktop"
        headless = unit_path.exists() or (not desktop_path.exists() and not os.environ.get("DISPLAY") and not os.environ.get("WAYLAND_DISPLAY"))
        if headless:
            if not shutil.which("systemctl") or subprocess.run(["systemctl", "--user", "show-environment"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5).returncode:
                raise RuntimeError("No desktop autostart or systemd user manager. Use app_follow.py --run CLIENT [ARGS...] to follow the client directly.")
            path = unit_path
            unit = "[Unit]\nDescription=Codex Token HUD client watcher\n[Service]\nType=simple\nExecStart=" + systemd_exec(command) + "\n[Install]\nWantedBy=default.target\n"
            if path.exists() and ("ExecStart=" + systemd_exec(command) + "\n") not in path.read_text():
                raise RuntimeError("Application-follow service belongs to another copy")
            if turn_on:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(unit, encoding="utf-8")
                subprocess.run(["systemctl", "--user", "daemon-reload"], check=True, capture_output=True)
                subprocess.run(["systemctl", "--user", "enable", path.name], check=True, capture_output=True)
            elif path.exists():
                subprocess.run(["systemctl", "--user", "disable", path.name], check=True, capture_output=True)
                path.unlink()
                subprocess.run(["systemctl", "--user", "daemon-reload"], check=True, capture_output=True)
            return
        path = desktop_path
        entry = "[Desktop Entry]\nType=Application\nName=Codex Token HUD application watcher\nExec=" + desktop_exec(command) + "\nTerminal=false\nX-CodexTokenHud-Owner=" + value["installation"] + "\n"
        if path.exists() and ("X-CodexTokenHud-Owner=" + value["installation"] + "\n") not in path.read_text():
            raise RuntimeError("Application-follow entry belongs to another copy")
        if turn_on:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(entry, encoding="utf-8")
        else:
            path.unlink(missing_ok=True)


def configure(command, runtime, codex_home, turn_on, config=None, extra=(), resident=False):
    config = config or default_config()
    installation = str(Path(command[0] if getattr(sys, "frozen", False) or len(command) == 1 else next(v for v in command[1:] if v.endswith(".py"))).resolve())
    saved = read_selection(config)
    if os.name == "nt" and not saved:
        legacy = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData/Local") / "CodexTokenHud/app-follow/config.json"
        previous = read_selection(legacy)
        if previous.get("installation") == installation:
            saved = previous
    if saved.get("enabled") and saved.get("installation") != installation:
        raise RuntimeError("Application following is enabled for another copy; disable it there first")
    value = {"enabled": bool(turn_on), "installation": installation, "command": command,
             "resident": bool(resident or saved.get("resident", False)),
             "runtime": str(Path(runtime).resolve()), "codex_home": str(Path(codex_home).resolve()),
             "client_executables": [str(Path(p).resolve()) for p in extra] or saved.get("client_executables", [])}
    register(config, value, turn_on)
    atomic_json(config, value)
    if turn_on:
        if os.name == "nt":
            # Let Task Scheduler own the long-lived watcher. It must not keep an
            # installer/terminal job alive or die when that caller is closed.
            task = "CodexTokenHud-" + os.environ["USERNAME"]
            powershell("Start-ScheduledTask -TaskName '" + task.replace("'", "''") + "'")
        else:
            subprocess.Popen(watcher_command(config), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, start_new_session=True)


def watch(config, duration=0):
    value = read_selection(config)
    watch_folder = Path(value.get("lock_folder", str(config.parent)))
    lock = WatcherLock(watch_folder)
    if not lock.acquire():
        lock.close()
        if value.get("until_idle"):
            atomic_json(config, {"enabled": False})
        return 0
    state, child, owner, error = Lifecycle(), None, "", ""
    started, retry_at = time.monotonic(), 0
    runtime = Path(value.get("runtime", str(config.parent)))
    leases = Path(value.get("lease_root", str(config.parent / "launches")))
    try:
        while enabled(config=config) and (not duration or time.monotonic() - started < duration):
            now = time.monotonic()
            try:
                targets = supported_processes(value.get("client_executables", []))
            except (OSError, subprocess.SubprocessError) as failure:
                # An enumeration error is not evidence that all apps closed.
                error = str(failure)
                if not value.get("until_idle"):
                    time.sleep(2)
                    continue
                targets = []  # Explicit leases work without process enumeration.
            # Explicit launchers share their client lifetime with the installed
            # watcher, so it cannot close a HUD while an unknown-name client runs.
            for path in leases.glob("*.json"):
                active = read_selection(path)
                if active.get("active") and ((active.get("explicit") and process_alive(active.get("pid"))) or time.time() - active.get("at", 0) < 6):
                    targets.append((active["pid"], "launched"))
            targets = list(set(targets))
            status = read_selection(runtime / "status.json")
            external = bool(process_alive(status.get("pid")) and time.time() * 1000 - status.get("updated_at_ms", 0) < 5000)
            running = (child is not None and child.poll() is None) or external
            action = state.step(targets, running, child is not None, now, keep_open=value.get("resident", False))
            if action == "start" and now >= retry_at:
                owner = uuid.uuid4().hex
                try:
                    child = subprocess.Popen(value["command"] + ["--follow-owner", owner, "--runtime", str(runtime),
                                               "--codex-home", value["codex_home"]], stdin=subprocess.DEVNULL,
                                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                             **({"creationflags": 0x08000000} if os.name == "nt" else {"start_new_session": True}))
                    state.observed_hud = True
                    error = ""
                except OSError as failure:
                    child, error, retry_at = None, str(failure), now + 15
            elif action == "close":
                atomic_json(runtime / "app-close.request.json", {"owner": owner})
            atomic_json(watch_folder / "status.json", {"pid": os.getpid(), "active_clients": len(targets),
                        "desktop_running": any(kind == "desktop" for _, kind in targets),
                        "resident": bool(value.get("resident", False)),
                        "managed_pid": child.pid if child and child.poll() is None else None,
                        "dismissed": state.dismissed, "last_error": error, "updated_at_ms": time.time() * 1000})
            persistent = value.get("persistent_config") and enabled(config=Path(value["persistent_config"]))
            if value.get("until_idle") and not persistent and not targets and state.empty_since is not None and now - state.empty_since >= state.grace:
                if child is None or child.poll() is not None:
                    break
            time.sleep(2)
    finally:
        if value.get("until_idle"):
            atomic_json(config, {"enabled": False})
        atomic_json(watch_folder / "status.json", {"pid": None, "updated_at_ms": time.time() * 1000})
        lock.close()
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_mutually_exclusive_group(required=True)
    actions.add_argument("--watch", "--watch-apps", action="store_true")
    actions.add_argument("--enable", action="store_true")
    actions.add_argument("--disable", action="store_true")
    actions.add_argument("--run", nargs=argparse.REMAINDER, help="Launch and follow any compatible client, passing all remaining arguments unchanged")
    parser.add_argument("--config", type=Path, default=default_config())
    parser.add_argument("--windows-exe", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--codex-home", type=Path, default=Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex"))
    parser.add_argument("--duration", type=float, default=0, help=argparse.SUPPRESS)
    parser.add_argument("--resident", action="store_true", help="Keep the HUD visible even when all clients close")
    parser.add_argument("--interface", choices=("auto", "overlay", "dashboard"), default="auto")
    parser.add_argument("--client-executable", type=Path, action="append", default=[], help="Also follow this exact executable path")
    args = parser.parse_args(argv)
    if args.watch:
        return watch(args.config, args.duration)
    command = [str(args.windows_exe.resolve())] if args.windows_exe else hud_command(args.interface)
    runtime = args.runtime or data_directory() / ("runtime" if len(command) == 1 and os.name == "nt" else
                                                "follow-dashboard-runtime" if "--no-browser" in command else "overlay-runtime")
    if args.run is not None:
        if not args.run:
            parser.error("--run needs a client executable and optional arguments")
        child = subprocess.Popen(args.run)
        temporary = args.config.parent / ("launch-" + uuid.uuid4().hex) / "config.json"
        lease = args.config.parent / "launches" / (uuid.uuid4().hex + ".json")
        atomic_json(lease, {"pid": child.pid, "active": True, "explicit": True, "at": time.time()})
        atomic_json(temporary, {"enabled": True, "command": command, "runtime": str(runtime),
                               "codex_home": str(args.codex_home), "client_executables": [str(p) for p in args.client_executable],
                               "lease_root": str(args.config.parent / "launches"), "until_idle": True,
                               "lock_folder": str(args.config.parent), "persistent_config": str(args.config)})
        previous_sigint = signal.signal(signal.SIGINT, lambda *_: None)
        try:
            subprocess.Popen(watcher_command(temporary), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, **({"creationflags": 0x08000000} if os.name == "nt" else {"start_new_session": True}))
            return child.wait()
        finally:
            signal.signal(signal.SIGINT, previous_sigint)
            lease.unlink(missing_ok=True)
    configure(command, runtime, args.codex_home, args.enable, args.config, args.client_executable, resident=args.resident)
    print("Application following enabled" if args.enable else "Application following disabled")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

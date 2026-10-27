"""Opt-in Claude collector: preserves the original command, stores only safe fields."""
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path.home() / '.terminalmgr' / 'ai'
CONFIG = Path(os.environ.get('CLAUDE_CONFIG_DIR', str(Path.home() / '.claude'))) / 'settings.json'
PREVIOUS = ROOT / 'previous-statusline.json'


def atomic(path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd, temporary = tempfile.mkstemp(dir=path.parent, prefix='.tmp-')
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(value, stream, ensure_ascii=False)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def command():
    import shlex
    args = [sys.executable, str(Path(__file__).resolve()), '--collect']
    return subprocess.list2cmdline(args) if os.name == 'nt' else shlex.join(args)


def install(remove=False):
    settings = json.loads(CONFIG.read_text(encoding='utf-8-sig')) if CONFIG.exists() else {}
    old = settings.get('statusLine')
    ours = isinstance(old, dict) and old.get('command') == command()
    if remove:
        if not ours:
            raise ValueError('Statuszeile wurde geändert; nichts überschrieben.')
        previous = json.loads(PREVIOUS.read_text(encoding='utf-8'))
        if previous is None:
            settings.pop('statusLine', None)
        else:
            settings['statusLine'] = previous
    else:
        if ours:
            print('Bereits eingerichtet. Claude neu starten, falls noch keine Daten erscheinen.')
            return
        if old and (not isinstance(old, dict) or old.get('type') != 'command'):
            raise ValueError('Vorhandene Statuszeile ist kein command-Typ.')
        if CONFIG.exists():
            shutil.copy2(CONFIG, str(CONFIG) + '.terminalmgr-' + str(time.time_ns()) + '.bak')
        atomic(PREVIOUS, old)
        settings['statusLine'] = dict(old or {}, type='command', command=command())
    atomic(CONFIG, settings)
    print('Statuszeile wiederhergestellt.' if remove else 'Eingerichtet. Claude neu starten; Limits kommen nach der nächsten Antwort.')


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def collect():
    raw = sys.stdin.buffer.read(1048577)
    try:
        if len(raw) > 1048576:
            raise ValueError('input too large')
        data = json.loads(raw)
        snapshot = {'observed_at': int(time.time())}
        session = data.get('session_id', '')
        if not isinstance(session, str) or not re.fullmatch(r'[a-zA-Z0-9_-]{1,100}', session):
            raise ValueError('invalid session')
        snapshot['session_id'] = session
        for group, fields in {
            'model': ('display_name',),
            'context_window': ('used_percentage', 'total_input_tokens', 'total_output_tokens'),
            'cost': ('total_cost_usd', 'total_duration_ms', 'total_api_duration_ms'),
        }.items():
            source = data.get(group) or {}
            snapshot[group] = {key: source[key] for key in fields if number(source.get(key)) or (group == 'model' and isinstance(source.get(key), str))}
        snapshot['rate_limits'] = {}
        for name in ('five_hour', 'seven_day', 'spend_limit'):
            source = (data.get('rate_limits') or {}).get(name) or {}
            snapshot['rate_limits'][name] = {key: source[key] for key in ('used_percentage', 'resets_at') if number(source.get(key))}
        auth = subprocess.run(['claude', 'auth', 'status'], capture_output=True, timeout=4, check=False)
        identity = json.loads(auth.stdout)
        snapshot['auth_identity'] = {key: identity[key] for key in ('authMethod', 'email', 'orgId') if isinstance(identity.get(key), str)}
        atomic(ROOT / 'claude' / (session + '.json'), snapshot)
        for old in sorted((ROOT / 'claude').glob('*.json'), key=lambda p: p.stat().st_mtime, reverse=True)[20:]:
            old.unlink(missing_ok=True)
    except Exception:
        pass  # Never break the original status line or print authentication output.
    try:
        previous = json.loads(PREVIOUS.read_text(encoding='utf-8'))
        if previous and previous.get('command'):
            subprocess.run(previous['command'], input=raw, shell=True, check=False, timeout=5)
    except Exception:
        pass


if __name__ == '__main__':
    if '--install' in sys.argv or '--uninstall' in sys.argv:
        try:
            install('--uninstall' in sys.argv)
        except Exception:
            print('Einrichtung fehlgeschlagen. Python, Zugriffsrechte und vorhandene Statuszeile prüfen.')
            sys.exit(1)
    else:
        collect()

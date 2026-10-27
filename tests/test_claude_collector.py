import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('collector', Path(__file__).parents[1] / 'src/TerminalMgr.App/Integrations/claude-statusline.py')
collector = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(collector)


class CollectorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.paths = patch.multiple(collector, ROOT=root / 'ai', CONFIG=root / 'claude/settings.json', PREVIOUS=root / 'ai/previous.json')
        self.paths.start()

    def tearDown(self):
        self.paths.stop()
        self.temp.cleanup()

    def test_install_preserves_and_restores_existing_settings(self):
        original = {'theme': 'dark', 'statusLine': {'type': 'command', 'command': 'echo existing', 'padding': 2}}
        collector.atomic(collector.CONFIG, original)
        collector.install()
        installed = json.loads(collector.CONFIG.read_text())
        self.assertEqual(installed['theme'], 'dark')
        self.assertEqual(installed['statusLine']['padding'], 2)
        self.assertEqual(json.loads(collector.PREVIOUS.read_text()), original['statusLine'])
        collector.install()  # Idempotence must not replace the saved original with our own wrapper.
        collector.install(remove=True)
        self.assertEqual(json.loads(collector.CONFIG.read_text()), original)

    def test_refuse_to_overwrite_changed_statusline(self):
        collector.install()
        collector.atomic(collector.CONFIG, {'statusLine': {'type': 'command', 'command': 'user changed this'}})
        with self.assertRaises(ValueError):
            collector.install(remove=True)

    def test_allowlist_and_original_input(self):
        collector.atomic(collector.PREVIOUS, {'type': 'command', 'command': 'original command'})
        data = {'session_id': 'session-one', 'transcript_path': 'PRIVATE', 'token': 'SECRET', 'rate_limits': {'five_hour': {'used_percentage': 38, 'resets_at': 1999999999, 'secret': 'SECRET'}}, 'model': {'display_name': 'Example'}}
        raw = json.dumps(data).encode()
        output = type('Result', (), {'stdout': b'{"authMethod":"claude.ai","email":"user@example.test","accessToken":"SECRET"}'})()
        with patch.object(collector.sys, 'stdin', type('Input', (), {'buffer': io.BytesIO(raw)})()), patch.object(collector.subprocess, 'run', return_value=output) as run:
            collector.collect()
        snapshot = (collector.ROOT / 'claude/session-one.json').read_text()
        self.assertNotIn('PRIVATE', snapshot)
        self.assertNotIn('SECRET', snapshot)
        self.assertEqual(json.loads(snapshot)['rate_limits']['five_hour']['used_percentage'], 38)
        self.assertEqual(run.call_args.kwargs['input'], raw)


if __name__ == '__main__':
    unittest.main()

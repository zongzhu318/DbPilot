import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('dmg', Path(__file__).with_name('create-dmg.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DmgTests(unittest.TestCase):
    def run_case(self, errors):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        app = Path(temp.name) / 'DbPilot.app'
        app.mkdir()
        output = Path(temp.name) / 'installers/DbPilot.dmg'
        output.parent.mkdir()
        output.write_bytes(b'previous valid image')
        creates = []
        def run(args, **kwargs):
            if args[:2] == ['hdiutil', 'create']:
                creates.append(args)
                error = errors[min(len(creates)-1, len(errors)-1)]
                if error:
                    return subprocess.CompletedProcess(args, 1, '', error)
                Path(args[-1]).write_bytes(b'new valid image')
            return subprocess.CompletedProcess(args, 0, '', '')
        return app, output, creates, run

    def test_busy_retries_in_fresh_directories_and_publishes_verified_image(self):
        app, output, creates, run = self.run_case(['hdiutil: create failed - Resource busy', None])
        with patch.object(module.subprocess, 'run', side_effect=run) as commands, patch.object(module.time, 'sleep'):
            module.create_dmg(app, output)
        self.assertEqual(len(creates), 2)
        self.assertNotEqual(creates[0][-1], creates[1][-1])
        self.assertIn('HFS+', creates[1])
        self.assertEqual(output.read_bytes(), b'new valid image')
        self.assertEqual(commands.call_args.args[0][:2], ['hdiutil', 'verify'])
        self.assertFalse(Path(creates[0][-1]).parent.exists())

    def test_busy_retry_limit_preserves_previous_output(self):
        app, output, creates, run = self.run_case(['Resource busy'])
        with patch.object(module.subprocess, 'run', side_effect=run), patch.object(module.time, 'sleep'):
            with self.assertRaisesRegex(RuntimeError, 'attempt 3/3'):
                module.create_dmg(app, output)
        self.assertEqual(len(creates), 3)
        self.assertEqual(output.read_bytes(), b'previous valid image')

    def test_other_errors_are_not_retried(self):
        app, output, creates, run = self.run_case(['Permission denied'])
        with patch.object(module.subprocess, 'run', side_effect=run), patch.object(module.time, 'sleep'):
            with self.assertRaisesRegex(RuntimeError, 'Permission denied'):
                module.create_dmg(app, output)
        self.assertEqual(len(creates), 1)
        self.assertEqual(output.read_bytes(), b'previous valid image')

    def test_verification_failure_preserves_previous_output(self):
        app, output, creates, run = self.run_case([None])
        def fail_verification(args, **kwargs):
            if args[:2] == ['hdiutil', 'verify']:
                raise subprocess.CalledProcessError(1, args)
            return run(args, **kwargs)
        with patch.object(module.subprocess, 'run', side_effect=fail_verification):
            with self.assertRaises(subprocess.CalledProcessError):
                module.create_dmg(app, output)
        self.assertEqual(output.read_bytes(), b'previous valid image')

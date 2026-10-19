import importlib.util
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('macos_signing', Path(__file__).with_name('sign-macos-bundle.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
ENTITLEMENTS = Path(__file__).resolve().parents[2] / 'packaging/macos/entitlements.plist'


class MacSigningTests(unittest.TestCase):
    def test_moves_symbols_and_signs_app_after_nested_code(self):
        with tempfile.TemporaryDirectory() as temp:
            app = Path(temp).resolve() / 'DbStudio.app'
            code = app / 'Contents/MacOS'
            code.mkdir(parents=True)
            (code / 'DbStudio').write_bytes(b'executable')
            (code / 'DbStudio.pdb').write_bytes(b'symbols')
            (code / 'libDbStudioUpdater.dylib').write_bytes(b'library')
            def run(args, **kwargs):
                return subprocess.CompletedProcess(args, 0, 'Mach-O' if args[0] == 'file' and args[-1].endswith('.dylib') else '')
            with patch.object(module.subprocess, 'run', side_effect=run) as commands:
                module.sign_bundle(app, '-', ENTITLEMENTS)
            self.assertFalse((code / 'DbStudio.pdb').exists())
            self.assertEqual((app / 'Contents/Resources/DebugSymbols/DbStudio.pdb').read_bytes(), b'symbols')
            signs = [call.args[0] for call in commands.call_args_list if call.args[0][:2] == ['codesign', '--force']]
            self.assertEqual([args[-1] for args in signs], [str(code / 'libDbStudioUpdater.dylib'), str(app)])
            self.assertIn('--entitlements', signs[-1])
            self.assertEqual(commands.call_args.args[0], ['codesign', '--verify', '--deep', '--strict', str(app)])

    @unittest.skipUnless(sys.platform == 'darwin', 'requires macOS codesign')
    def test_real_codesign_accepts_app_with_leftover_publish_symbols(self):
        with tempfile.TemporaryDirectory() as temp:
            app = Path(temp).resolve() / 'DbStudio.app'
            code = app / 'Contents/MacOS'
            code.mkdir(parents=True)
            shutil.copy('/bin/echo', code / 'DbStudio')
            (code / 'DbStudio.pdb').write_bytes(b'leftover publish symbols')
            (app / 'Contents/Info.plist').write_bytes(plistlib.dumps(dict(
                CFBundleIdentifier='com.dbpilot.signingtest', CFBundleExecutable='DbStudio', CFBundlePackageType='APPL')))
            module.sign_bundle(app, '-', ENTITLEMENTS)


if __name__ == '__main__':
    unittest.main()

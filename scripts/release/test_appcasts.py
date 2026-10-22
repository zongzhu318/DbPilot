import hashlib
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('appcasts', Path(__file__).with_name('generate-appcasts.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class AppcastTests(unittest.TestCase):
    def test_platform_feeds_and_checksums_use_final_installer_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            names = ['DbPilot-1.2.3-osx-arm64.dmg', 'DbPilot-1.2.3-osx-x64.dmg',
                     'DbPilot-1.2.3-win-x64-setup.exe']
            for name in names:
                (directory / name).write_bytes(name.encode())
            def sign(args, **kwargs):
                if '--verify' in args:
                    return subprocess.CompletedProcess(args, 0, '')
                asset = Path(args[-1])
                if asset.suffix == '.xml':
                    return subprocess.CompletedProcess(args, 0, '')
                return subprocess.CompletedProcess(args, 0, f'sparkle:edSignature="test-signature" length="{asset.stat().st_size}"')
            with patch.object(module.subprocess, 'run', side_effect=sign) as signer:
                module.generate(directory, 'owner/repo', '1.2.3', 'sign_update', 'secret-file')
            self.assertEqual(signer.call_count, 12)
            for rid, name in zip(['osx-arm64', 'osx-x64', 'win-x64'], names):
                root = ET.parse(directory / f'appcast-{rid}.xml').getroot()
                enclosure = root.find('channel/item/enclosure')
                self.assertEqual(enclosure.get('url'), f'https://github.com/owner/repo/releases/download/v1.2.3/{name}')
                self.assertEqual(enclosure.get(f'{{{module.NS}}}edSignature'), 'test-signature')
                self.assertEqual(root.find(f'channel/item/{{{module.NS}}}version').text, '1.2.3')
                self.assertEqual(enclosure.get(f'{{{module.NS}}}os'), 'windows' if rid.startswith('win') else 'macos')
            checksums = (directory / 'SHA256SUMS.txt').read_text().splitlines()
            self.assertEqual(len(checksums), 6)
            for line in checksums:
                digest, name = line.split('  ')
                self.assertEqual(digest, hashlib.sha256((directory / name).read_bytes()).hexdigest())

    def test_macos_feeds_embed_markdown_release_notes(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            names = ['DbPilot-1.2.3-osx-arm64.dmg', 'DbPilot-1.2.3-osx-x64.dmg',
                     'DbPilot-1.2.3-win-x64-setup.exe']
            for name in names:
                (directory / name).write_bytes(name.encode())
            def sign(args, **kwargs):
                if '--verify' in args:
                    return subprocess.CompletedProcess(args, 0, '')
                asset = Path(args[-1])
                if asset.suffix == '.xml':
                    return subprocess.CompletedProcess(args, 0, '')
                return subprocess.CompletedProcess(args, 0, f'sparkle:edSignature="test-signature" length="{asset.stat().st_size}"')
            notes = '### 新功能\n\n- 支持显示升级说明\n- 修复若干问题'
            with patch.object(module.subprocess, 'run', side_effect=sign):
                module.generate(directory, 'owner/repo', '1.2.3', 'sign_update', 'secret-file', notes)
            for rid in ('osx-arm64', 'osx-x64'):
                item = ET.parse(directory / f'appcast-{rid}.xml').getroot().find('channel/item')
                description = item.find('description')
                self.assertIsNotNone(description)
                self.assertEqual(description.text, notes)
                self.assertEqual(description.get(f'{{{module.NS}}}format'), 'markdown')
                self.assertIsNone(item.find(f'{{{module.NS}}}releaseNotesLink'))
            win_item = ET.parse(directory / 'appcast-win-x64.xml').getroot().find('channel/item')
            self.assertIsNone(win_item.find('description'))
            self.assertEqual(win_item.find(f'{{{module.NS}}}releaseNotesLink').text,
                             'https://github.com/owner/repo/releases/tag/v1.2.3')

    def test_missing_installer_prevents_release_feed(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(FileNotFoundError):
                module.generate(temp, 'owner/repo', '1.2.3', 'sign_update', 'secret-file')

    def test_bad_signing_output_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            (Path(temp) / 'DbPilot-1.2.3-osx-arm64.dmg').write_bytes(b'installer')
            with patch.object(module.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, 'length="999"')):
                with self.assertRaises(ValueError):
                    module.generate(temp, 'owner/repo', '1.2.3', 'sign_update', 'secret-file')


if __name__ == '__main__':
    unittest.main()

#!/usr/bin/env python3
"""Sign final installer bytes and create separate Sparkle feeds per runtime."""
import argparse
import datetime
import email.utils
import hashlib
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

NS = 'http://www.andymatuschak.org/xml-namespaces/sparkle'
ET.register_namespace('sparkle', NS)


def generate(directory, repository, version, signer, key_file):
    directory = Path(directory)
    assets = {
        'osx-arm64': f'DbStudio-{version}-osx-arm64.dmg',
        'osx-x64': f'DbStudio-{version}-osx-x64.dmg',
        'win-x64': f'DbStudio-{version}-win-x64-setup.exe',
    }
    for rid, name in assets.items():
        installer = directory / name
        if not installer.is_file():
            raise FileNotFoundError(installer)
        result = subprocess.run([str(signer), '--ed-key-file', str(key_file), str(installer)],
                                check=True, capture_output=True, text=True)
        # sign_update emits XML attributes. Parse rather than guessing the signature format.
        attrs = ET.fromstring(f'<enclosure xmlns:sparkle="{NS}" {result.stdout.strip()}/>').attrib
        if not attrs.get(f'{{{NS}}}edSignature') or int(attrs['length']) != installer.stat().st_size:
            raise ValueError(f'Invalid signing output for {name}')
        root = ET.Element('rss', version='2.0')
        channel = ET.SubElement(root, 'channel')
        ET.SubElement(channel, 'title').text = f'DbStudio {rid}'
        ET.SubElement(channel, 'link').text = f'https://github.com/{repository}/releases'
        item = ET.SubElement(channel, 'item')
        ET.SubElement(item, 'title').text = f'DbStudio {version}'
        ET.SubElement(item, 'pubDate').text = email.utils.format_datetime(datetime.datetime.now(datetime.timezone.utc))
        ET.SubElement(item, f'{{{NS}}}version').text = version
        ET.SubElement(item, f'{{{NS}}}shortVersionString').text = version
        if rid.startswith('osx'):
            ET.SubElement(item, f'{{{NS}}}minimumSystemVersion').text = '12.0'
        else:
            ET.SubElement(item, f'{{{NS}}}releaseNotesLink').text = f'https://github.com/{repository}/releases/tag/v{version}'
        attrs.update(url=f'https://github.com/{repository}/releases/download/v{version}/{name}',
                     type='application/octet-stream')
        attrs[f'{{{NS}}}os'] = 'windows' if rid.startswith('win') else 'macos'
        if rid.startswith('win'):
            attrs[f'{{{NS}}}installerArguments'] = '/SILENT /SUPPRESSMSGBOXES /NORESTART'
            # WinSparkle accepts enclosure version attributes as well as item elements.
            attrs[f'{{{NS}}}version'] = version
            attrs[f'{{{NS}}}shortVersionString'] = version
        ET.SubElement(item, 'enclosure', attrs)
        ET.indent(root)
        feed = directory / f'appcast-{rid}.xml'
        ET.ElementTree(root).write(feed, encoding='utf-8', xml_declaration=True)
        subprocess.run([str(signer), '--ed-key-file', str(key_file), str(feed)],
                       check=True, capture_output=True, text=True)
        subprocess.run([str(signer), '--ed-key-file', str(key_file), '--verify', str(installer),
                        attrs[f'{{{NS}}}edSignature']], check=True, capture_output=True, text=True)
        subprocess.run([str(signer), '--ed-key-file', str(key_file), '--verify', str(feed)],
                       check=True, capture_output=True, text=True)
    files = [directory / name for name in assets.values()]
    files += sorted(directory.glob('appcast-*.xml'))
    checksums = []
    for path in files:
        with path.open('rb') as stream:
            checksums.append(f'{hashlib.file_digest(stream, "sha256").hexdigest()}  {path.name}\n')
    (directory / 'SHA256SUMS.txt').write_text(''.join(checksums), encoding='utf-8')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--directory', required=True)
    p.add_argument('--repository', required=True)
    p.add_argument('--version', required=True)
    p.add_argument('--signer', required=True)
    p.add_argument('--key-file', required=True)
    a = p.parse_args()
    generate(a.directory, a.repository, a.version, a.signer, a.key_file)

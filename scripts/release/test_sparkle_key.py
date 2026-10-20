import base64
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin', 'requires macOS Swift CryptoKit')
class SparkleKeyTests(unittest.TestCase):
    def test_official_formats_and_mismatch_without_leaking_secrets(self):
        # RFC 8032 Ed25519 test vector; never read the developer's actual private key.
        seed = bytes.fromhex('9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60')
        public = bytes.fromhex('d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a')
        legacy = bytes(64) + public  # Format/embedded-public-key check; sign_update verifies signatures later.
        script = Path(__file__).with_name('validate-sparkle-key.swift')
        cache = str(Path(tempfile.gettempdir()) / 'dbpilot-swift-module-cache')
        for private, expected, success in [(seed, public, True), (legacy, public, True),
                                           (seed, bytes(32), False), (bytes(64), public, False)]:
            with self.subTest(length=len(private), success=success):
                private_text = base64.b64encode(private).decode()
                env = dict(os.environ, SPARKLE_PRIVATE_KEY=private_text,
                           SPARKLE_PUBLIC_KEY=base64.b64encode(expected).decode())
                result = subprocess.run(['swift', '-module-cache-path', cache, str(script)], env=env,
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, success, result.stderr)
                self.assertNotIn(private_text, result.stdout + result.stderr)
